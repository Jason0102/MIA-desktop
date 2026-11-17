"""Regression check for independent DM admission and explicit continuation."""
import asyncio
import json
import os
from pathlib import Path
import sys
import tempfile
from types import SimpleNamespace

ROOT = Path(__file__).resolve().parents[2]
sys.path[:0] = [os.environ.get('MIA_HERMES_SOURCE') or str(ROOT / '.mia-runtime/hermes/hermes-agent'), str(Path(__file__).parent)]


async def check():
    from gateway.session import SessionSource
    from gateway.config import Platform
    from gateway.platforms.base import BasePlatformAdapter
    from gateway.platforms.event import MessageEvent, ProcessingOutcome
    from hermes_constants import pin_process_hermes_home
    from mia_discord_tasks import install

    entered = []
    release = asyncio.Event()

    async def handle(adapter, event):
        from gateway.session import build_session_key
        event._gateway_accepted = True
        entered.append((event, build_session_key(event.source, profile=event.source.profile)))
        await release.wait()

    BasePlatformAdapter.handle_message = handle
    from gateway.run_notifications import GatewayNotificationsMixin
    from gateway.run_turn_prepare import t
    delivered_notices = []
    async def deliver_notice(runner, source, content):
        delivered_notices.append(content)
    GatewayNotificationsMixin._deliver_platform_notice = deliver_notice
    extracted_inputs = []
    async def extracted(adapter, response, event, *args, **kwargs):
        extracted_inputs.append(response)
        return SimpleNamespace(text_content='Done', media_files=[('example.pdf', False)], local_files=[])
    BasePlatformAdapter._extract_response_content = extracted
    with tempfile.TemporaryDirectory(dir=ROOT / '.mia-runtime') as folder:
        os.environ['HERMES_HOME'] = str(Path(folder) / 'hermes')
        pin_process_hermes_home(Path(folder) / 'hermes')
        state = Path(folder) / 'mia'
        state.mkdir()
        os.environ['MIA_DATA_DIR'] = str(state)
        os.environ['MIA_HERMES_USER_REGISTRY'] = str(state / 'users.json')
        (state / 'users.json').write_text(json.dumps({'Users': [{'Id': 'jason'}, {'Id': 'other'}], 'Bindings': [
            {'Provider': 'discord', 'Subject': '123456789012345678', 'UserId': 'jason'},
            {'Provider': 'discord', 'Subject': '234567890123456789', 'UserId': 'other'}]}))
        install()
        notices = []
        async def send(*args, **kwargs):
            notices.append(args)
        adapter = SimpleNamespace(_canonicalize=lambda source: None, send=send)

        def event(identifier, reply=None, user='123456789012345678'):
            source = SessionSource(Platform.DISCORD, '100', user_id=user,
                                   profile='mia-discord-' + user)
            return MessageEvent('independent work', source=source, message_id=identifier,
                                reply_to_message_id=reply)

        first, second = event('111'), event('222')
        await GatewayNotificationsMixin._deliver_platform_notice(None, first.source,
            t('gateway.notify.no_home_channel', platform='Discord', sethome_cmd='/sethome'))
        await GatewayNotificationsMixin._deliver_platform_notice(None, first.source, 'Delivery failed')
        assert delivered_notices == ['Delivery failed'], 'Hide setup hints, preserve errors'
        jobs = [asyncio.create_task(BasePlatformAdapter.handle_message(adapter, e)) for e in (first, second)]
        await asyncio.sleep(.05)
        assert len(entered) == 2 and entered[0][1] != entered[1][1]
        assert all(not job.done() for job in jobs), 'Jobs must overlap'
        assert first.source.chat_id == second.source.chat_id == '100'
        snapshots = list((state / 'discord-work').glob('*.json'))
        assert len(snapshots) == 2
        assert all(json.loads(p.read_text())['Status'] == 'queued' for p in snapshots)
        release.set()
        await asyncio.gather(*jobs)
        resumed = event('333', reply='111')
        await BasePlatformAdapter.handle_message(adapter, resumed)
        assert entered[-1][1] == entered[0][1]
        await BasePlatformAdapter.handle_message(adapter, event('444', reply='111', user='234567890123456789'))
        assert len(entered) == 3 and len(notices) == 1
        from gateway.session import build_session_key
        restored = SessionSource.from_dict(first.source.to_dict())
        assert build_session_key(restored, profile=restored.profile) == entered[0][1]
        from gateway.session import SessionStore
        from gateway.config import GatewayConfig
        os.environ['HERMES_HOME'] = str(Path(folder) / 'hermes')
        Path(os.environ['HERMES_HOME']).mkdir(exist_ok=True)
        store = SessionStore(Path(folder) / 'hermes' / 'sessions', GatewayConfig())
        a = store.get_or_create_session(first.source)
        b = store.get_or_create_session(second.source)
        assert a.session_id != b.session_id, 'Native recovery must not adopt the other job'
        assert store.get_or_create_session(restored).session_id == a.session_id
        for db in store._db_handles.values():
            db.close()
        assert os.environ['HERMES_GATEWAY_BUSY_ACK_ENABLED'] == 'false'
        await BasePlatformAdapter._run_processing_hook(adapter, 'on_processing_start', first)
        await BasePlatformAdapter._extract_response_content(adapter, 'Done<!--mia-kind:task--><!--mia-chat:音樂,should-not-persist-->', first, entered[0][1])
        assert extracted_inputs[-1] == 'Done', 'Internal classification must not reach Discord'
        await BasePlatformAdapter._run_processing_hook(adapter, 'on_processing_complete', first, ProcessingOutcome.SUCCESS)
        snapshots = [json.loads(p.read_text()) for p in (state / 'discord-work').glob('*.json')]
        completed = next(s for s in snapshots if s['Task'] == 'mia-task-111')
        assert completed['UserId'] == 'jason' and completed['Reply'] == 'Done'
        assert completed['Files'] == ['example.pdf'] and completed['Status'] == 'completed'
        assert completed['StartedAt']
        assert completed['Kind'] == 'task'
        await BasePlatformAdapter._run_processing_hook(adapter, 'on_processing_complete', second, ProcessingOutcome.FAILURE)
        snapshots = [json.loads(p.read_text()) for p in (state / 'discord-work').glob('*.json')]
        assert next(s for s in snapshots if s['Task'] == 'mia-task-222')['Status'] == 'failed'
        await BasePlatformAdapter._extract_response_content(adapter, 'Done<!--mia-kind:task-->', resumed, entered[-1][1])
        await BasePlatformAdapter._run_processing_hook(adapter, 'on_processing_complete', resumed, ProcessingOutcome.CANCELLED)
        from mia_interaction import InteractionMemory
        events = InteractionMemory(state, 'jason').read()
        assert sum(item['Kind'] == 'message' for item in events) == 3
        assert sum(item['Kind'] == 'task_new' for item in events) == 1
        assert [item['Group'] for item in events if item['Kind'] == 'task_resume'] == ['mia-task-111']
        assert [item['Topics'] for item in events if item['Kind'] == 'chat' and item.get('TopicConfidence') == 'classified'] == [['音樂']]
        assert not InteractionMemory(state, 'other').read()
        assert not list((InteractionMemory(state, 'jason').root / InteractionMemory(state, 'jason').generation()).glob('active-*'))
        # Natural, unquoted follow-ups reuse the same native session key.
        from mia_discord_work import WorkMirror
        from mia_discord_tasks import recent_conversation
        from datetime import datetime, timezone, timedelta
        mirror = WorkMirror(Path(folder) / 'hermes')
        mirror.update(first, Status='completed')
        followup = event('555')
        followup.text = 'Which campus and day did we just discuss?'
        await BasePlatformAdapter.handle_message(adapter, followup)
        assert entered[-1][1] == entered[0][1], 'Unquoted follow-up lost native history'
        assert recent_conversation(state, event('x').source, 'next') is None, 'Active task must not capture a parallel request'
        mirror.update(followup, Status='completed')
        explicit = event('666')
        explicit.text = '新任務：independent work'
        await BasePlatformAdapter.handle_message(adapter, explicit)
        assert entered[-1][1] != entered[0][1]
        mirror.update(first, Status='interrupted')
        assert recent_conversation(state, first.source, 'retry') == 'mia-task-111', 'Gateway restart must retain context for a retry'
        discard = event('777')
        discard.text = '/discard'
        await BasePlatformAdapter.handle_message(adapter, discard)
        assert entered[-1][1] == entered[0][1], 'Recovery command must target the current conversation'
        snapshots = [json.loads(p.read_text(encoding='utf-8')) for p in (state / 'discord-work').glob('*.json')]
        assert next(s for s in snapshots if s['Task'] == 'mia-task-111')['Status'] == 'interrupted', 'A recovery command alone is not a queued task'
        paused = event('888', reply='111')
        await BasePlatformAdapter.handle_message(adapter, paused)
        before_results = sum(item['Kind'] == 'message_result' for item in InteractionMemory(state, 'jason').read())
        await BasePlatformAdapter._run_processing_hook(adapter, 'on_processing_start', paused)
        await BasePlatformAdapter._extract_response_content(adapter,
            '⏸️ This conversation is paused: a previous turn did not finish when Hermes restarted, and only you know whether to send it again.', paused, entered[-1][1])
        assert '前文' in extracted_inputs[-1] and '/discard' in extracted_inputs[-1]
        await BasePlatformAdapter._run_processing_hook(adapter, 'on_processing_complete', paused, ProcessingOutcome.SUCCESS)
        assert sum(item['Kind'] == 'message_result' for item in InteractionMemory(state, 'jason').read()) == before_results, 'Paused notice is not a completed turn'
        await BasePlatformAdapter._run_processing_hook(adapter, 'on_processing_start', paused)
        await BasePlatformAdapter._extract_response_content(adapter, 'Friday, Guangfu<!--mia-kind:task-->', paused, entered[-1][1])
        await BasePlatformAdapter._run_processing_hook(adapter, 'on_processing_complete', paused, ProcessingOutcome.SUCCESS)
        assert sum(item['Kind'] == 'message_result' for item in InteractionMemory(state, 'jason').read()) == before_results + 1
        assert recent_conversation(state, event('x', user='234567890123456789').source, 'next') is None
        other_channel = event('x').source
        other_channel.chat_id = 'different'
        assert recent_conversation(state, other_channel, 'next') is None
        mirror.update(first, Status='completed', UpdatedAt=(datetime.now(timezone.utc)-timedelta(hours=1)).isoformat())
        for path in (state / 'discord-work').glob('*.json'):
            data = json.loads(path.read_text(encoding='utf-8'))
            data['UpdatedAt'] = (datetime.now(timezone.utc)-timedelta(hours=1)).isoformat()
            path.write_text(json.dumps(data))
        assert recent_conversation(state, first.source, 'next') is None, 'Expired conversation must not be adopted'
        print('PASS: unquoted history continuity, explicit new tasks, active-task/channel/user isolation and expiry.')
        print('PASS: concurrent DM jobs, reply continuation, user isolation, durable source round-trip, quiet acknowledgements.')
        print('PASS: durable work-center snapshots, identity, lifecycle, results and attachments.')
        print('PASS: mixed Discord interactions, task continuation and exclusion of unbound topic labels.')


asyncio.run(check())
