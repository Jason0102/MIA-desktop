"""Hermes tool for sending an explicit MIA task to local Codex."""

import os
from pathlib import Path

from .bridge import registration, directory_registration
from .extensions import extension_registration
from .history import registration as history_registration, conversation_registration
from .browser_tools import registration as browser_registration
from .scheduled_tasks import register_scheduled_tasks
from .webhook_events import register_webhook
from .isolated_code import block_native_code, registration as code_registration


def calendar_workflow(_args, **_kwargs):
    from hermes_constants import get_hermes_home
    from tools.registry import tool_error, tool_result

    path = Path(get_hermes_home()) / "skills" / "mia-calendar" / "SKILL.md"
    try:
        return tool_result({"instructions": path.read_text(encoding="utf-8-sig")})
    except OSError:
        return tool_error("The MIA calendar skill is missing. Restart with Start-Mia.ps1 to install it.")


def register(ctx):
    ctx.register_tool(**registration())
    ctx.register_tool(**extension_registration())
    ctx.register_tool(**directory_registration())
    ctx.register_tool(**history_registration())
    ctx.register_tool(**conversation_registration())
    ctx.register_tool(**browser_registration())
    ctx.register_tool(
        name="mia_calendar_workflow", toolset="mia_codex", handler=calendar_workflow,
        schema={
            "name": "mia_calendar_workflow",
            "description": "Read MIA's installed mia-calendar skill before managing events or checking schedule and transit conflicts.",
            "parameters": {"type": "object", "properties": {}, "additionalProperties": False},
        },
    )
    register_scheduled_tasks(ctx)
    register_webhook(ctx)
    ctx.register_hook("pre_tool_call", block_native_code)
    if os.environ.get("MIA_ISOLATED_CODE") == "1":
        ctx.register_tool(**code_registration())
