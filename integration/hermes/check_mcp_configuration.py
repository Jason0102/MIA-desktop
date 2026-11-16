"""Test Hermes discovery and MIA MCP configuration in an isolated profile."""

from contextlib import contextmanager
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer
import json
import os
from pathlib import Path
import subprocess
import sys
import tempfile
from threading import Thread


HERE = Path(__file__).resolve().parent
SOURCE = HERE.parents[1] / ".mia-runtime/hermes/hermes-agent"
TOOL_NAME = "mcp__mia_calendar__mia_calendar"
FIXTURE_TOKEN = "synthetic-mia-test-value"


class FixtureMcp(BaseHTTPRequestHandler):
    def log_message(self, *_args):
        pass

    def do_POST(self):
        if self.headers.get("Authorization") != "Bearer " + FIXTURE_TOKEN:
            self.send_error(401)
            return
        request = json.loads(self.rfile.read(int(self.headers["Content-Length"])))
        method = request["method"]
        if "id" not in request:
            self.send_response(202)
            self.end_headers()
            return
        if method == "initialize":
            result = {"protocolVersion": request["params"]["protocolVersion"], "capabilities": {"tools": {}},
                      "serverInfo": {"name": "health-fixture", "version": "1.0"}}
        elif method == "tools/list":
            result = {"tools": [{"name": "fixture_echo", "description": "Return a diagnostic marker.",
                                 "inputSchema": {"type": "object", "properties": {}}}]}
        elif method == "tools/call":
            result = {"content": [{"type": "text", "text": "MIA FIXTURE OK"}]}
        elif method == "ping":
            result = {}
        else:
            self.send_error(404)
            return
        body = json.dumps({"jsonrpc": "2.0", "id": request["id"], "result": result}).encode()
        self.send_response(200)
        self.send_header("Content-Type", "application/json")
        self.send_header("Content-Length", str(len(body)))
        self.end_headers()
        self.wfile.write(body)


@contextmanager
def http_fixture():
    with ThreadingHTTPServer(("127.0.0.1", 0), FixtureMcp) as server:
        thread = Thread(target=server.serve_forever, daemon=True)
        thread.start()
        try:
            yield f"http://127.0.0.1:{server.server_port}/mcp"
        finally:
            server.shutdown()
            thread.join()


def cli(args, env, expected=0):
    command = [sys.executable, "-I", "-B", str(HERE / "mia_hermes.py"), *args]
    result = subprocess.run(command, env=env, capture_output=True, encoding="utf-8", timeout=60)
    assert result.returncode == expected, (args, result.stdout, result.stderr)
    return result.stdout


def probe(code, env):
    setup = "import os, sys; sys.path.insert(0, os.environ['MIA_HERMES_SOURCE']); "
    result = subprocess.run([sys.executable, "-I", "-B", "-c", setup + code],
                            env=env, capture_output=True, encoding="utf-8", timeout=40)
    assert result.returncode == 0, (result.stdout, result.stderr)
    return result.stdout


def configure_fixture(url, token, env):
    code = (
        "from hermes_cli.mcp_config import _save_mcp_server; "
        "assert _save_mcp_server('test_http_mcp', {'url': " + repr(url)
        + ", 'headers': {'Authorization': " + repr("Bearer " + token) + "}, 'connect_timeout': 6})"
    )
    probe(code, env)


def verify(workspace):
    profile = workspace / "hermes"
    profile.mkdir()
    env = dict(os.environ, HERMES_HOME=str(profile), MIA_HERMES_SOURCE=str(SOURCE),
               MIA_CODEX_WORKSPACE=str(workspace), HERMES_DISABLE_LAZY_INSTALLS="1",
               PYTHONIOENCODING="utf-8")
    cli(["mia-configure-calendar"], env)
    assert "mia_calendar" in cli(["mcp", "test", "mia_calendar"], env)
    discover = "from tools.mcp_tool_discovery import discover_mcp_tools; tools=discover_mcp_tools(); "
    assert "MIA CALENDAR READ OK" in probe(
        discover + f"assert {TOOL_NAME!r} in tools, tools; "
        "from tools.registry import registry; import json; "
        f"answer=registry.dispatch({TOOL_NAME!r}, "
        "{'action':'list','window_start':'2026-11-01T00:00:00+08:00','window_end':'2026-11-02T00:00:00+08:00'}); "
        "assert json.loads(json.loads(answer)['result'])['total']==0, answer; print('MIA CALENDAR READ OK')", env)
    cli(["mia-mcp-toggle", "disable", "mia_calendar"], env)
    cli(["mia-configure-calendar"], env)
    probe(discover + f"assert {TOOL_NAME!r} not in tools, tools", env)
    cli(["mia-mcp-toggle", "enable", "mia_calendar"], env)
    probe(discover + f"assert {TOOL_NAME!r} in tools, tools", env)

    with http_fixture() as url:
        configure_fixture(url, FIXTURE_TOKEN, env)
        assert "fixture_echo" in cli(["mcp", "test", "test_http_mcp"], env)
        assert "MIA NATIVE HTTP OK" in probe(
            "from tools.mcp_tool_discovery import discover_mcp_tools; "
            "tools=discover_mcp_tools(allowed_mcp_names=['test_http_mcp']); "
            "from tools.registry import registry; "
            "name='mcp__test_http_mcp__fixture_echo'; assert name in tools, tools; "
            "answer=registry.dispatch(name, {}); assert 'MIA FIXTURE OK' in answer, answer; "
            "print('MIA NATIVE HTTP OK')", env)
        configure_fixture(url, "expired", env)
        cli(["mcp", "test", "test_http_mcp"], env, expected=1)
        configure_fixture(url, FIXTURE_TOKEN, env)
    cli(["mcp", "test", "test_http_mcp"], env, expected=1)


if __name__ == "__main__":
    with tempfile.TemporaryDirectory(prefix="mia-mcp-check-", dir=SOURCE.parent.parent) as scratch:
        verify(Path(scratch))
    print("PASS: MIA MCP registration, disable/re-enable, native tool dispatch, bearer rejection and disconnect")
