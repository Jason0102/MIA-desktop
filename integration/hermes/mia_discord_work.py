"""Durable, per-identity work-center mirror of native Discord task lifecycle."""
import hashlib
import json
import logging
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


def install(home):
    from gateway.platforms.base import BasePlatformAdapter
    mirror = WorkMirror(home)
    original_hook = BasePlatformAdapter._run_processing_hook
    original_extract = BasePlatformAdapter._extract_response_content

    async def hook(adapter, name, *args, **kwargs):
        if args and name == 'on_processing_start':
            mirror.update(args[0], Status='running', Reply='', Files=[],
                          StartedAt=datetime.now(timezone.utc).isoformat())
        elif len(args) > 1 and name == 'on_processing_complete':
            outcome = args[1].name.lower()
            mirror.update(args[0], Status={'success': 'completed', 'failure': 'failed',
                                          'cancelled': 'cancelled'}.get(outcome, 'failed'))
        return await original_hook(adapter, name, *args, **kwargs)

    async def extract(adapter, response, event, *args, **kwargs):
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
