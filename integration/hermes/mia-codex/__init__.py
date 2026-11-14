"""Hermes tool for sending an explicit MIA task to local Codex."""

from .bridge import registration
from .extensions import extension_registration


def register(ctx):
    ctx.register_tool(**registration())
    ctx.register_tool(**extension_registration())
