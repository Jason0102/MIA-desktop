"""Conversational management; execution leases remain owned by the desktop."""
import json
import os
from pathlib import Path
import subprocess
import uuid
import sqlite3
from contextlib import closing


def board_context(session_id):
    from hermes_constants import get_hermes_home, get_routing_process_hermes_home
    from mia_discord import read_registry
    home = get_hermes_home().resolve()
    root = Path(os.environ.get("MIA_HERMES_PROFILE_ROOT") or get_routing_process_hermes_home()).resolve()
    if home == root:
        main = Path(os.environ["MIA_DATA_DIR"]) / "hermes-session.txt"
        allowed = bool(session_id and main.exists() and main.read_text(encoding="utf-8-sig").strip() == session_id)
        return home, allowed
    registry = read_registry(Path(os.environ["MIA_HERMES_USER_REGISTRY"]))
    bindings = registry["Bindings"]
    local = next(b["UserId"] for b in bindings if b["Provider"] == "desktop" and b["Subject"] == "local")
    binding = next((b for b in bindings if b["Provider"] == "discord"
                    and home == root / "profiles" / ("mia-discord-" + b["Subject"])), None)
    if binding is None:
        raise ValueError("This source has no registered work-center identity.")
    allowed = False
    if session_id:
        with closing(sqlite3.connect((home / "state.db").as_uri() + "?mode=ro", uri=True)) as db:
            row = db.execute("SELECT source,user_id FROM sessions WHERE id=?", (session_id,)).fetchone()
            allowed = bool(row and row[0] == "discord" and row[1] == binding["Subject"])
    return (root if binding["UserId"] == local else home), allowed


def handle(args, session_id=None, **_kwargs):
    try:
        action = args.get("action")
        if action not in {"list", "create", "approve", "changes", "block", "unblock"}:
            raise ValueError("Unsupported card action.")
        board_home, can_manage = board_context(session_id)
        if action != "list" and not can_manage:
            raise ValueError("Only a verified user conversation can change work cards; delegated workers cannot approve their own output.")
        fields = {key: value for key, value in args.items()
                  if key in {"action", "title", "body", "parents", "key", "id", "note"}}
        if action == "create":
            fields.setdefault("key", uuid.uuid4().hex)
        result = subprocess.run(
            [os.environ["MIA_HERMES_EXECUTABLE"], "-I", "-B", os.environ["MIA_HERMES_KANBAN_ENTRY"]],
            input=json.dumps(fields), encoding="utf-8", capture_output=True, timeout=25,
            env={**os.environ, "HERMES_HOME": str(board_home)},
            creationflags=subprocess.CREATE_NO_WINDOW if os.name == "nt" else 0)
        if result.returncode:
            raise ValueError("Work card service failed; no success was confirmed.")
        return json.dumps(json.loads(result.stdout), ensure_ascii=False)
    except (ValueError, KeyError, OSError, StopIteration, sqlite3.Error, subprocess.SubprocessError) as error:
        return json.dumps({"error": str(error)}, ensure_ascii=False)


def registration():
    name = "mia_work_cards"
    return {"name": name, "toolset": "mia_codex", "handler": handle, "schema": {
        "name": name,
        "description": "Manage durable MIA work cards through conversation. List before selecting a card; never guess IDs. Split multi-step user requests into cards, use returned changed IDs as parents. Parents must be user-approved before children run. Only approve/return/unblock on explicit user instruction; workers must not approve their own output. After creating cards do not also execute the same work. Desktop work center runs ready cards and displays status.",
        "parameters": {"type": "object", "properties": {
            "action": {"type": "string", "enum": ["list", "create", "approve", "changes", "block", "unblock"]},
            "title": {"type": "string"}, "body": {"type": "string"},
            "parents": {"type": "array", "items": {"type": "string"}},
            "key": {"type": "string", "description": "Stable UUID for retry-safe creation."},
            "id": {"type": "string"}, "note": {"type": "string"}
        }, "required": ["action"], "additionalProperties": False}}}
