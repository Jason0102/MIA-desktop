"""Hermes tool for sending an explicit MIA task to local Codex."""

from .bridge import registration, directory_registration
from .extensions import extension_registration
from .history import registration as history_registration, conversation_registration


def register(ctx):
    ctx.register_tool(**registration())
    ctx.register_tool(**extension_registration())
    ctx.register_tool(**directory_registration())
    ctx.register_tool(**history_registration())
    ctx.register_tool(**conversation_registration())
