"""Independent Discord DM jobs on the same Hermes turn engine used by PC jobs.

Unquoted messages continue a recent completed conversation. Discord Reply
selects an explicit task; new-task prefixes and concurrent jobs stay separate.
The durable route index is keyed by sender and channel, never by message text.
"""
import os
import sqlite3
import sys
import json
import re
from datetime import datetime, timezone, timedelta
from pathlib import Path
from contextlib import closing


def recent_conversation(state, source, text):
    """Continue the latest finished turn for this sender, leaving concurrent jobs independent."""
    if re.match(r"^\s*(?:新任務|另外一個任務|另開任務|new task)\s*[:：]", text or "", re.I):
        return None
    candidates = []
    for path in (Path(state) / 'discord-work').glob('*.json'):
        try:
            item = json.loads(path.read_text(encoding='utf-8'))
            if item.get('DiscordUserId') != source.user_id or item.get('ChannelId') != source.chat_id:
                continue
            at = datetime.fromisoformat(item['UpdatedAt'])
            if timedelta(0) <= datetime.now(timezone.utc) - at <= timedelta(minutes=30):
                candidates.append((at, item))
        except (OSError, ValueError, KeyError, TypeError):
            continue
    if not candidates:
        return None
    latest = max(candidates, key=lambda pair: pair[0])[1]
    return latest.get('Task') if latest.get('Status') == 'completed' else None


def install():
    from gateway import session
    from gateway.platforms.base import BasePlatformAdapter
    from hermes_constants import get_process_hermes_home

    original_key = session.build_session_key
    original_handle = BasePlatformAdapter.handle_message
    original_enqueue = BasePlatformAdapter._enqueue_text_event
    from gateway.run_notifications import GatewayNotificationsMixin
    from gateway.run_turn_prepare import t
    original_notice = GatewayNotificationsMixin._deliver_platform_notice

    async def notice(runner, source, content):
        # Setup hints are separate from display/tool-progress notifications.
        # Match the localized setup notice only; operational errors still deliver.
        if source.platform.value == 'discord' and content == t(
                'gateway.notify.no_home_channel', platform='Discord', sethome_cmd='/sethome'):
            return
        return await original_notice(runner, source, content)

    GatewayNotificationsMixin._deliver_platform_notice = notice
    from mia_discord_work import install as install_work_mirror
    from mia_discord_interactions import DiscordInteractions
    interactions = DiscordInteractions(get_process_hermes_home())
    mirror = install_work_mirror(get_process_hermes_home(), interactions)
    from gateway.session_recovery import SessionRecoveryMixin
    original_find = SessionRecoveryMixin._find_gateway_session_row

    def find_task(store, *, session_key, source, allow_peer_fallback, **kwargs):
        if source.platform.value == 'discord' and (source.chat_id_alt or '').startswith('mia-task-'):
            allow_peer_fallback = False
        return original_find(store, session_key=session_key, source=source,
                             allow_peer_fallback=allow_peer_fallback, **kwargs)

    SessionRecoveryMixin._find_gateway_session_row = find_task
    database = get_process_hermes_home().parent / 'mia' / 'discord-tasks.sqlite'
    database.parent.mkdir(parents=True, exist_ok=True)
    with closing(sqlite3.connect(database)) as db, db:
        db.execute('CREATE TABLE IF NOT EXISTS routes (chat TEXT, user TEXT, message TEXT, task TEXT, PRIMARY KEY(chat,user,message))')

    def task_key(source, *args, **kwargs):
        key = original_key(source, *args, **kwargs)
        task = source.chat_id_alt or ''
        if source.platform.value == 'discord' and source.chat_type == 'dm' and task.startswith('mia-task-'):
            return key + ':' + task
        return key

    # All key consumers must agree, including already imported session stores.
    for module in tuple(sys.modules.values()):
        if module and getattr(module, 'build_session_key', None) is original_key:
            module.build_session_key = task_key

    async def handle(adapter, event):
        source = event.source
        if source.platform.value != 'discord' or source.chat_type != 'dm' or event.internal:
            return await original_handle(adapter, event)
        adapter._canonicalize(source)
        if source.profile_route_rejected or not (source.profile or '').startswith('mia-discord-'):
            return await original_handle(adapter, event)
        if not event.message_id or not source.user_id:
            return await original_handle(adapter, event)
        # Replies to a bot result reference the original user request via Discord's
        # message reference. Never accept another sender's task as the target.
        reply = event.reply_to_message_id
        raw_reply = getattr(getattr(event.raw_message, 'reference', None), 'resolved', None)
        if raw_reply and getattr(getattr(raw_reply, 'author', None), 'bot', False):
            reply = str(getattr(getattr(raw_reply, 'reference', None), 'message_id', '') or reply)
        with closing(sqlite3.connect(database)) as db, db:
            row = db.execute('SELECT task FROM routes WHERE chat=? AND user=? AND message=?',
                             (source.chat_id, source.user_id, reply or event.message_id)).fetchone()
            task = row[0] if row else 'mia-task-' + event.message_id
            continued = False
            if not row and not reply and not event.get_command():
                previous = recent_conversation(database.parent, source, event.text)
                if previous:
                    task = previous
                    continued = True
            if (event.get_command() or reply) and not row:
                # Unknown reply targets must not silently become unrelated tasks.
                await adapter.send(source.chat_id, '請用 Discord「回覆」選取要操作的任務，再傳送指令。', reply_to=event.message_id)
                return
            db.execute('INSERT OR IGNORE INTO routes VALUES (?,?,?,?)',
                       (source.chat_id, source.user_id, event.message_id, task))
        source.chat_id_alt = task
        command = bool(event.get_command())
        if not interactions.begin(event, task, bool(reply) or continued, command):
            await adapter.send(source.chat_id, '目前無法接收此訊息，請稍後再試。', reply_to=event.message_id)
            return
        try:
            if not row:
                mirror.update(event, Status='queued')
            return await original_handle(adapter, event)
        finally:
            if not getattr(event, '_gateway_accepted', False):
                interactions.end(event, None if command else 'cancelled', command=command)

    def enqueue(adapter, event):
        if event.source.platform.value == 'discord' and event.source.chat_type == 'dm':
            # Keep discrete user messages separate before the native burst merger.
            import asyncio
            task = asyncio.create_task(handle(adapter, event))
            pending.add(task)
            task.add_done_callback(finished)
            return
        return original_enqueue(adapter, event)

    pending = set()

    def finished(task):
        pending.discard(task)
        if not task.cancelled() and task.exception():
            import logging
            logging.getLogger(__name__).error('Discord task admission failed', exc_info=task.exception())

    BasePlatformAdapter.handle_message = handle
    BasePlatformAdapter._enqueue_text_event = enqueue
    os.environ['HERMES_GATEWAY_BUSY_ACK_ENABLED'] = 'false'
