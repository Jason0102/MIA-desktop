"""Durable, per-identity work-center mirror of native Discord task lifecycle."""
import hashlib
import json
import logging
import re
from datetime import datetime, timezone
from pathlib import Path


class WorkMirror:
    def __init__(self, home):
        self.state = home.parent / 'mia'
        self.directory = self.state / 'discord-work'

    def update(self, event, **changes):
        source = event.source
        task = source.chat_id_alt or ''
        if source.platform.value != 'discord' or not task.startswith('mia-task-'):
            return
        try:
            registry = json.loads((self.state / 'users.json').read_text(encoding='utf-8-sig'))
            owners = [b['UserId'] for b in registry['Bindings']
                      if b['Provider'] == 'discord' and b['Subject'] == source.user_id]
            if len(owners) != 1:
                return
            key = hashlib.sha256(f'{source.user_id}:{source.chat_id}:{task}'.encode()).hexdigest()
            path = self.directory / (key + '.json')
            data = json.loads(path.read_text(encoding='utf-8')) if path.exists() else {
                'Id': key, 'UserId': owners[0], 'DiscordUserId': source.user_id,
                'ChannelId': source.chat_id, 'Task': task, 'Request': event.text or '附件任務',
                'Reply': '', 'Files': [], 'Status': 'queued'}
            if data['UserId'] != owners[0]:
                return
            data.update(changes)
            data['UpdatedAt'] = datetime.now(timezone.utc).isoformat()
            from mia_discord import atomic_write
            atomic_write(path, json.dumps(data, ensure_ascii=False))
        except (OSError, ValueError, KeyError, TypeError):
            logging.getLogger(__name__).exception('Unable to mirror Discord work')


def install(home, interactions=None):
    from gateway.platforms.base import BasePlatformAdapter
    mirror = WorkMirror(home)
    original_hook = BasePlatformAdapter._run_processing_hook
    original_extract = BasePlatformAdapter._extract_response_content

    async def hook(adapter, name, *args, **kwargs):
        if args and name == 'on_processing_start':
            args[0]._mia_paused = False
            mirror.update(args[0], Status='running', Reply='', Files=[],
                          StartedAt=datetime.now(timezone.utc).isoformat())
        elif len(args) > 1 and name == 'on_processing_complete':
            if getattr(args[0], '_mia_paused', False):
                return await original_hook(adapter, name, *args, **kwargs)
            outcome = args[1].name.lower()
            result = {'success': 'completed', 'failure': 'failed', 'cancelled': 'cancelled'}.get(outcome, 'failed')
            mirror.update(args[0], Status=result)
            if interactions is not None:
                interactions.end(args[0], result)
        return await original_hook(adapter, name, *args, **kwargs)

    async def extract(adapter, response, event, *args, **kwargs):
        if event.source.platform.value == 'discord' and isinstance(response, str):
            if response.startswith('⏸️ This conversation is paused:') and 'previous turn did not finish' in response:
                event._mia_paused = True
                mirror.update(event, Status='interrupted')
                response = ('抱歉呀，米亞剛才重啟時中斷了上一回合。我先暫停處理，避免把工作重做；你的新訊息已保存。'
                            '請回覆這則訊息並傳送 /discard，略過中斷回合、保留前文並繼續已排隊的訊息；'
                            '此指令不會重跑中斷的工作。')
            kinds = re.findall(r'<!--mia-kind:(chat|task)-->', response)
            if kinds:
                mirror.update(event, Kind=kinds[-1])
            topics = re.findall(r'<!--mia-chat:([^<>]{0,180})-->', response)
            if interactions is not None:
                interactions.classify(event, kinds[-1] if kinds else None, topics[-1].split(',') if topics else [])
            response = re.sub(r'<!--mia-kind:(?:chat|task)-->', '', response).rstrip()
            response = re.sub(r'<!--mia-chat:[^<>\n]*-->', '', response).rstrip()
        result = await original_extract(adapter, response, event, *args, **kwargs)
        files = [str(p) for p, _ in result.media_files] + [str(p) for p in result.local_files]
        mirror.update(event, Reply=result.text_content or '', Files=list(dict.fromkeys(files)))
        return result

    BasePlatformAdapter._run_processing_hook = hook
    BasePlatformAdapter._extract_response_content = extract
    # A new gateway cannot still own turns left by its predecessor.
    if mirror.directory.exists():
        from mia_discord import atomic_write
        for path in mirror.directory.glob('*.json'):
            try:
                data = json.loads(path.read_text(encoding='utf-8'))
                if data.get('Status') in ('queued', 'running'):
                    data.update(Status='interrupted', UpdatedAt=datetime.now(timezone.utc).isoformat())
                    atomic_write(path, json.dumps(data, ensure_ascii=False))
            except (OSError, ValueError):
                logging.getLogger(__name__).exception('Unable to recover Discord work status')
    return mirror
