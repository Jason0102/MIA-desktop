"""Conversational management; execution leases remain owned by the desktop."""
import json
import os
from pathlib import Path
import subprocess
import uuid


def handle(args, session_id=None, **_kwargs):
    try:
        action = args.get("action")
        if action not in {"list", "create", "approve", "changes", "block", "unblock"}:
            raise ValueError("Unsupported card action.")
        if action != "list":
            main_session = Path(os.environ["MIA_DATA_DIR"]) / "hermes-session.txt"
            if not session_id or main_session.read_text(encoding="utf-8-sig").strip() != session_id:
                raise ValueError("Only the user's main conversation can change work cards; workers cannot approve their own output.")
        fields = {key: value for key, value in args.items()
                  if key in {"action", "title", "body", "parents", "key", "id", "note"}}
        if action == "create":
            fields.setdefault("key", uuid.uuid4().hex)
        result = subprocess.run(
            [os.environ["MIA_HERMES_EXECUTABLE"], "-I", "-B", os.environ["MIA_HERMES_KANBAN_ENTRY"]],
            input=json.dumps(fields), encoding="utf-8", capture_output=True, timeout=25,
            creationflags=subprocess.CREATE_NO_WINDOW if os.name == "nt" else 0)
        if result.returncode:
            raise ValueError("Work card service failed; no success was confirmed.")
        return json.dumps(json.loads(result.stdout), ensure_ascii=False)
    except (ValueError, KeyError, OSError, subprocess.SubprocessError) as error:
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
