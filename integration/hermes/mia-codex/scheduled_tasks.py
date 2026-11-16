"""Expose native Hermes schedules with local-only MIA result delivery."""

import json
import os
import re
import tempfile
import uuid
from datetime import datetime, timezone
from pathlib import Path
from threading import Lock

PLATFORM = "miacron"
CHANNEL = "desktop"
DESTINATION = f"{PLATFORM}:{CHANNEL}"
MANAGED_ID = re.compile(r"[0-9a-f]{12}")
REMINDER_SCRIPT = re.compile(r"mia-reminders/[0-9a-f]{32}\.py")
MUTATION_LOCK = Lock()
TASK_TOOLS = ["web", "vision", "todo", "mia_codex", "no_mcp"]


def _directory(root, *parts):
    path = Path(root).absolute().joinpath(*parts)
    for parent in (*reversed(path.parents), path):
        if parent.is_symlink() or (parent.exists() and getattr(parent.lstat(), "st_file_attributes", 0) & 0x400):
            raise OSError("MIA schedule folders cannot use redirected paths.")
    path.mkdir(parents=True, exist_ok=True)
    return path


def _outbox():
    root = Path(os.environ["MIA_CODEX_WORKSPACE"])
    return _directory(root, "state", "scheduled-notices", "pending")


def _write_notice(content, job_id):
    from cron.jobs import get_job

    job = get_job(job_id) or {}
    identifier = uuid.uuid4().hex
    outbox = _outbox()
    notice = {
        "id": identifier,
        "job_id": job_id,
        "title": str(job.get("name") or "排程結果")[:120],
        "kind": "reminder" if job.get("no_agent") else "task",
        "text": content[:24000],
        "received_at": datetime.now(timezone.utc).isoformat(),
    }
    with tempfile.NamedTemporaryFile(mode="w", encoding="utf-8", dir=outbox, delete=False) as file:
        temp = Path(file.name)
        try:
            json.dump(notice, file, ensure_ascii=False)
            file.flush()
            os.fsync(file.fileno())
        except BaseException:
            file.close()
            temp.unlink(missing_ok=True)
            raise
    try:
        temp.replace(outbox / f"{identifier}.json")
    finally:
        temp.unlink(missing_ok=True)
    return identifier


def _enabled():
    return os.environ.get("MIA_CRON_DELIVERY_ENABLED") == "true" and bool(os.environ.get("MIA_CODEX_WORKSPACE"))


def _make_adapter(config):
    from gateway.config import Platform
    from gateway.platforms.base import BasePlatformAdapter, SendResult

    class MiaCronDelivery(BasePlatformAdapter):
        async def connect(self, *, is_reconnect=False):
            _outbox()
            self._mark_connected()
            return True

        async def disconnect(self):
            self._mark_disconnected()

        async def get_chat_info(self, chat_id):
            return {"id": chat_id, "name": "MIA Desktop", "type": "dm"} if chat_id == CHANNEL else {}

        async def send(self, chat_id, content, reply_to=None, metadata=None):
            job_id = str((metadata or {}).get("job_id") or "")
            if chat_id != CHANNEL or not MANAGED_ID.fullmatch(job_id):
                return SendResult(False, error="MIA accepts only local Hermes cron results.")
            try:
                identifier = _write_notice(content, job_id)
            except (OSError, KeyError, ValueError):
                return SendResult(False, error="Unable to persist MIA's scheduled notice.")
            return SendResult(True, message_id=identifier)

    return MiaCronDelivery(config=config, platform=Platform(PLATFORM))


def _text(params, name, limit):
    value = params.get(name)
    if not isinstance(value, str) or not 0 < len(value.strip()) <= limit:
        raise ValueError(f"{name} must contain between 1 and {limit} characters.")
    return value.strip()


def _schedule(params):
    from cron.jobs import parse_schedule

    schedule = _text(params, "schedule", 100)
    parsed = parse_schedule(schedule)
    if parsed["kind"] == "once":
        if not schedule.lower().startswith("in "):
            instant = datetime.fromisoformat(schedule)
            if instant.utcoffset() is None:
                raise ValueError("A one-time date must include its UTC offset.")
        if datetime.fromisoformat(parsed["run_at"]) <= datetime.now(timezone.utc):
            raise ValueError("Choose a future time.")
    return schedule


def _time_options(params):
    options = {}
    if "timezone" in params:
        options["timezone"] = _text(params, "timezone", 64)
    if "catch_up" in params:
        if not isinstance(params["catch_up"], bool):
            raise ValueError("catch_up must be true or false.")
        options["catch_up"] = params["catch_up"]
    return options


def _new_script(message):
    from hermes_constants import get_hermes_home

    script_dir = _directory(get_hermes_home(), "scripts", "mia-reminders")
    path = script_dir / f"{uuid.uuid4().hex}.py"
    # A bytes literal represents every character as data, including quotes and newlines.
    path.write_text(f"import sys\nsys.stdout.buffer.write({message.encode('utf-8')!r})\n", encoding="ascii")
    return f"mia-reminders/{path.name}"


def _delete_script(script):
    from hermes_constants import get_hermes_home

    if isinstance(script, str) and REMINDER_SCRIPT.fullmatch(script):
        try:
            directory = _directory(get_hermes_home(), "scripts", "mia-reminders")
            (directory / Path(script).name).unlink(missing_ok=True)
        except OSError:
            pass


def _managed(job):
    return job is not None and job.get("deliver") == DESTINATION


def _summary(job):
    fields = ("id", "name", "schedule_display", "timezone", "catch_up", "next_run_at",
              "state", "last_run_at", "last_status", "last_error", "last_delivery_error")
    return {**{key: job.get(key) for key in fields}, "kind": "reminder" if job.get("no_agent") else "task"}


def _create(params, jobs):
    name = _text(params, "name", 100)
    text = _text(params, "text", 16000)
    schedule = _schedule(params)
    fields = _time_options(params)
    fields.setdefault("catch_up", True)
    kind = params.get("kind")
    script = None
    if kind == "reminder":
        if len(text) > 1000:
            raise ValueError("Reminder text must contain at most 1000 characters.")
        script = _new_script(text)
        fields.update(script=script, no_agent=True)
    elif kind == "task":
        fields.update(enabled_toolsets=list(TASK_TOOLS), workdir=os.environ["MIA_CODEX_WORKSPACE"])
    else:
        raise ValueError("Choose reminder or task.")
    try:
        return jobs.create_job(
            prompt=text if kind == "task" else None, schedule=schedule, name=name,
            deliver=DESTINATION, failure_deliver=DESTINATION, attach_to_session=False, **fields)
    except BaseException:
        _delete_script(script)
        raise


def _update(params, job, jobs):
    fields = _time_options(params)
    if "name" in params:
        fields["name"] = _text(params, "name", 100)
    if "schedule" in params:
        fields["schedule"] = _schedule(params)
    script = None
    if "text" in params:
        if job.get("no_agent"):
            script = _new_script(_text(params, "text", 1000))
            fields.update(script=script, interpreter=None)
        else:
            fields["prompt"] = _text(params, "text", 16000)
    if not fields:
        raise ValueError("Choose a field to update.")
    try:
        updated = jobs.update_job(job["id"], fields)
    except BaseException:
        _delete_script(script)
        raise
    if updated is None:
        _delete_script(script)
    return updated


def _run_action(params):
    from cron import jobs

    action = params.get("action")
    if action == "list":
        rows = [_summary(job) for job in jobs.list_jobs(include_disabled=True) if _managed(job)]
        return {"jobs": rows}
    if action == "create":
        return {"job": _summary(_create(params, jobs))}
    job_id = _text(params, "id", 12)
    job = jobs.get_job(job_id) if MANAGED_ID.fullmatch(job_id) else None
    if not _managed(job):
        raise ValueError("找不到該米亞排程。")
    if action == "delete":
        if not jobs.remove_job(job_id):
            raise ValueError("排程已移除。")
        _delete_script(job.get("script"))
        return {"deleted": job_id}
    if action == "update":
        result = _update(params, job, jobs)
    elif action == "pause":
        result = jobs.pause_job(job_id)
    elif action == "resume":
        result = jobs.resume_job(job_id)
    elif action == "run":
        result = jobs.trigger_job(job_id)
    else:
        raise ValueError("Unknown scheduling action.")
    if result is None:
        raise ValueError("排程已移除。")
    return {"job": _summary(result)}


def manage(params, **_kwargs):
    from gateway.session_context import get_session_env

    if not _enabled():
        return json.dumps({"error": "請使用 MIA 的 Hermes 啟動器。"}, ensure_ascii=False)
    if get_session_env("HERMES_CRON_SESSION") == "1" and params.get("action") != "list":
        return json.dumps({"error": "排程工作本身無法修改排程。"}, ensure_ascii=False)
    try:
        with MUTATION_LOCK:
            result = _run_action(params)
    except ValueError as exc:
        result = {"error": str(exc)}
    except (OSError, RuntimeError):
        result = {"error": "無法讀寫排程資料，請檢查米亞工作區。"}
    if not result.get("error") and params.get("action") != "list":
        from tools.cronjob_tools import _notify_provider_jobs_changed_safe
        _notify_provider_jobs_changed_safe()
    return json.dumps(result, ensure_ascii=False)


def register_scheduled_tasks(ctx):
    ctx.register_platform(
        name=PLATFORM, label="MIA scheduled results", adapter_factory=_make_adapter,
        check_fn=_enabled, validate_config=lambda _config: _enabled(),
        env_enablement_fn=lambda: {"local_delivery": True} if _enabled() else None,
        cron_deliver_env_var="MIA_CRON_HOME_CHANNEL",
        parse_target_ref_fn=lambda target: (CHANNEL, None) if target == CHANNEL else None,
        validate_target_ref_fn=lambda target: target == CHANNEL)
    name = "mia_schedule_manage"
    ctx.register_tool(
        name=name, toolset="mia_codex", handler=manage, schema={
            "name": name,
            "description": (
                "Manage persistent MIA schedules at the user's request. A reminder is literal text "
                "and requires no model at fire time. A task runs Hermes tools and delivers locally. "
                "Use an offset-bearing ISO timestamp or 'in 30m' for a one-time schedule, 'every 30m' "
                "for a recurring interval, and a five-field cron expression plus IANA timezone for "
                "a recurring clock time. Advise that execution and delivery require MIA running; "
                "missed runs follow Hermes' catch-up policy. A run action queues the job for now."),
            "parameters": {"type": "object", "additionalProperties": False, "required": ["action"],
                "properties": {
                    "action": {"type": "string", "enum": ["create", "list", "update", "pause", "resume", "delete", "run"]},
                    "id": {"type": "string", "description": "Exact ID from list, required when acting on an existing schedule."},
                    "kind": {"type": "string", "enum": ["reminder", "task"], "description": "Required on create."},
                    "name": {"type": "string", "description": "Short label used in desktop results."},
                    "text": {"type": "string", "description": "Reminder text or task prompt."},
                    "schedule": {"type": "string"},
                    "timezone": {"type": "string", "description": "IANA zone, e.g. Asia/Taipei."},
                    "catch_up": {"type": "boolean", "description": "Allow native catch-up after sleep or shutdown, subject to its time window. Default true."}
                }}
        })
