"""Verify shared task policy and identity-scoped remote work-center authority."""
import importlib
import json
import os
from pathlib import Path
import sqlite3
import sys
import tempfile
import types
from contextlib import closing

here = Path(__file__).resolve().parent
sys.path[:0] = [str(here), os.environ.get('MIA_HERMES_SOURCE') or str(here.parents[1] / ".mia-runtime/hermes/hermes-agent")]
from hermes_constants import set_hermes_home_override, reset_hermes_home_override
from mia_discord import prepare, role_instructions
import hermes_yaml as yaml

package = types.ModuleType("channel_fixture")
package.__path__ = [str(here / "mia-codex")]
sys.modules[package.__name__] = package
cards = importlib.import_module("channel_fixture.work_cards")

with tempfile.TemporaryDirectory() as folder:
    root = Path(folder).resolve() / "hermes"
    root.mkdir()
    state = Path(folder) / "mia"
    state.mkdir()
    registry = state / "users.json"
    users = ["123456789012345678", "234567890123456789"]
    registry.write_text(json.dumps({"Users": [{"Id": "local"}, {"Id": "guest"}],
        "Bindings": [{"Provider": "desktop", "Subject": "local", "UserId": "local"}] +
        [{"Provider": "discord", "Subject": user, "UserId": owner}
         for user, owner in zip(users, ["local", "guest"])]}))
    config = {"model": {"provider": "test"}, "agent": {"max_turns": 30},
              "platform_toolsets": {"api_server": ["mia_codex", "skills", "memory"]},
              "mcp_servers": {"fixture": {"command": "test"}}}
    (root / "config.yaml").write_text(yaml.safe_dump(config))
    prepare(root, registry)
    os.environ.update(MIA_HERMES_PROFILE_ROOT=str(root), MIA_HERMES_USER_REGISTRY=str(registry),
                      MIA_DATA_DIR=str(state))
    (state / "hermes-session.txt").write_text("desktop-main")
    policy = (here.parents[1] / "src/Mia/task-policy.md").read_text(encoding="utf-8-sig")
    for i, user in enumerate(users):
        profile = root / "profiles" / ("mia-discord-" + user)
        child = yaml.safe_load((profile / "config.yaml").read_text())
        assert child["agent"] == config["agent"]
        assert child["mcp_servers"] == config["mcp_servers"]
        assert set(config["platform_toolsets"]["api_server"]) <= set(child["platform_toolsets"]["discord"])
        soul = (profile / "SOUL.md").read_text(encoding="utf-8")
        assert policy in soul and "[遠端訊息／Discord]" in soul
        assert role_instructions(here.parents[1] / 'src/Mia/role.json') in soul
        extra = child['platforms']['discord']['extra']
        assert set(extra['user_allowed_commands']) == {'new', 'reset', 'stop', 'status', 'discard'}
        assert user not in extra['allow_admin_from'], 'Conversation recovery must not grant administrator commands'
        with closing(sqlite3.connect(profile / "state.db")) as db:
            db.execute("CREATE TABLE sessions(id TEXT,source TEXT,user_id TEXT)")
            db.executemany("INSERT INTO sessions VALUES(?,?,?)",
                           [("direct", "discord", user), ("worker", "subagent", user),
                            ("foreign", "discord", users[1-i])])
            db.commit()
        token = set_hermes_home_override(profile)
        try:
            assert cards.board_context("direct") == (root if i == 0 else profile, True)
            assert cards.board_context("worker")[1] is False
            assert cards.board_context("foreign")[1] is False
        finally:
            reset_hermes_home_override(token)
    token = set_hermes_home_override(root)
    try:
        assert cards.board_context("desktop-main") == (root, True)
        assert cards.board_context("worker") == (root, False)
    finally:
        reset_hermes_home_override(token)
print("PASS: shared PC/DC policy, tools, model execution settings, MCP, remote labels, owner board sharing, guest isolation and worker rejection.")
