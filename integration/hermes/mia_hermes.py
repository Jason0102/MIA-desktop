"""Run the pinned Hermes CLI with installed dependencies and MIA's own profile."""

import os
import sys

sys.path.insert(0, os.environ["MIA_HERMES_SOURCE"])
sys.path.append(os.path.dirname(os.path.abspath(__file__)))
os.environ["HERMES_DISABLE_LAZY_INSTALLS"] = "1"

if sys.argv[1:3] == ["gateway", "run"]:
    from pathlib import Path
    from mia_discord import install_model_credentials, restrict_attachment_paths
    from mia_shared_memory import install_from_environment, refresh_prompts
    mia_home = Path(os.environ["HERMES_HOME"])
    install_model_credentials(mia_home, mia_home.parent / "mia" / "users.json")
    shared_memory = install_from_environment(migrate=True)
    if shared_memory:
        refresh_prompts(shared_memory)
    restrict_attachment_paths()
    from mia_discord_tasks import install as install_discord_tasks
    install_discord_tasks()

from hermes_cli.main import main

if __name__ == "__main__":
    if sys.argv[1:] == ["mia-reset-memory"]:
        from mia_shared_memory import install_from_environment, reset_current
        install_from_environment()
        reset_current()
    elif sys.argv[1:] == ["mia-configure-calendar"]:
        from pathlib import Path
        from hermes_cli.mcp_config import _get_mcp_servers, _save_mcp_server

        script = Path(__file__).with_name("mia_calendar_mcp.py").resolve(strict=True)
        existing = _get_mcp_servers().get("mia_calendar", {})
        server = {
            "command": sys.executable,
            "args": ["-I", "-B", str(script)],
            "env": {"MIA_CODEX_WORKSPACE": os.environ["MIA_CODEX_WORKSPACE"]},
            "enabled": existing.get("enabled", True),
        }
        if existing != server:
            if not _save_mcp_server("mia_calendar", server):
                raise SystemExit("Could not configure the bundled MIA calendar MCP server")
    elif len(sys.argv) == 4 and sys.argv[1] == "mia-mcp-toggle":
        from hermes_cli.mcp_config import _get_mcp_servers, _save_mcp_server

        action, name = sys.argv[2:]
        existing = _get_mcp_servers().get(name)
        if existing is None or action not in {"enable", "disable"}:
            raise SystemExit("Unknown MCP server or action")
        if not _save_mcp_server(name, {**existing, "enabled": action == "enable"}):
            raise SystemExit("Could not update MCP server")
        print(f"{name}: {action}d")
    else:
        if sys.argv[1:3] == ["gateway", "run"]:
            from mia_session_cleanup import install

            install()
        main()
