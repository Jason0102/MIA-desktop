"""Run the pinned Hermes CLI with installed dependencies and MIA's own profile."""

import os
import sys

sys.path.insert(0, os.environ["MIA_HERMES_SOURCE"])
os.environ["HERMES_DISABLE_LAZY_INSTALLS"] = "1"

from hermes_cli.main import main

if __name__ == "__main__":
    if sys.argv[1:] == ["mia-configure-calendar"]:
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
        main()
