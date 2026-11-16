"""Smoke-test the installed MIA plugin and calendar against an isolated Hermes gateway."""

from contextlib import ExitStack
import hashlib
import hmac
import json
import os
from pathlib import Path
import shutil
import socket
import subprocess
import sys
import tempfile
import time
from urllib.error import HTTPError, URLError
from urllib.request import ProxyHandler, Request, build_opener


HERE = Path(__file__).resolve().parent
RUNTIME = HERE.parents[1] / ".mia-runtime"
SOURCE = RUNTIME / "hermes" / "hermes-agent"
API_KEY = "local-gateway-smoke-only"
SECRET = "mia-gateway-smoke-" + "s" * 32


def request(port, path, content=None, headers=None):
    opener = build_opener(ProxyHandler({}))
    url = f"http://127.0.0.1:{port}{path}"
    try:
        response = opener.open(Request(url, data=content, headers=headers or {}), timeout=2)
    except HTTPError as exc:
        response = exc
    with response:
        data = response.read()
        return response.status, json.loads(data) if data.startswith((b"{", b"[")) else data.decode()


def port_pair():
    with ExitStack() as stack:
        ports = []
        for _ in range(2):
            handle = stack.enter_context(socket.socket())
            handle.bind(("127.0.0.1", 0))
            ports.append(handle.getsockname()[1])
        return ports


def await_result(gateway, action, assert_ok, timeout=20):
    deadline = time.monotonic() + timeout
    result = None
    while time.monotonic() < deadline and gateway.poll() is None:
        try:
            result = action()
            if assert_ok(result):
                return result
        except (OSError, URLError):
            pass
        time.sleep(0.25)
    raise AssertionError(f"Gateway check timed out. Last response: {result!r}")


def smoke(workspace):
    home = workspace / "state" / "hermes"
    plugin = home / "plugins" / "mia-codex"
    shutil.copytree(HERE / "mia-codex", plugin, ignore=shutil.ignore_patterns("__pycache__"))
    shutil.copytree(HERE / "skills", home / "skills")
    api_port, webhook_port = port_pair()
    rules = workspace / "rules.json"
    env = {
        **os.environ,
        "OPENAI_API_KEY": "sk-integration-test-only",
        "HERMES_HOME": str(home),
        "HERMES_DISABLE_LAZY_INSTALLS": "1",
        "HERMES_GATEWAY_LOCK_DIR": str(workspace / "locks"),
        "HERMES_RUNTIME_DIR": str(RUNTIME / "hermes" / "tools"),
        "MIA_HERMES_SOURCE": str(SOURCE),
        "MIA_HERMES_EXECUTABLE": sys.executable,
        "MIA_HERMES_ENTRY": str(HERE / "mia_hermes.py"),
        "MIA_CODEX_WORKSPACE": str(workspace),
        "MIA_ISOLATED_CODE": "",
        "MIA_CRON_DELIVERY_ENABLED": "true",
        "MIA_CRON_HOME_CHANNEL": "desktop",
        "MIA_WEBHOOK_RULES": str(rules),
        "MIA_WEBHOOK_PORT": str(webhook_port),
        "MIA_WEBHOOK_SECRET_SMOKE": SECRET,
        "API_SERVER_ENABLED": "true",
        "API_SERVER_HOST": "127.0.0.1",
        "API_SERVER_PORT": str(api_port),
        "API_SERVER_KEY": API_KEY,
        "TEMP": str(workspace),
        "TMP": str(workspace),
    }
    (home / "config.yaml").write_text(
        "plugins:\n  enabled: [mia-codex]\n"
        "platform_toolsets:\n  api_server: [web, vision, todo, clarify, memory, session_search, skills, mia_codex]\n"
        "kanban:\n  dispatch_in_gateway: false\n"
        "model:\n  provider: openai-api\n  default: gpt-4.1-mini\n  base_url: http://127.0.0.1:1/v1\n",
        encoding="utf-8")
    launch = [sys.executable, "-I", "-B", str(HERE / "mia_hermes.py")]

    def run(command):
        process = subprocess.run(command, env=env, cwd=workspace, capture_output=True,
                                 text=True, encoding="utf-8", errors="replace", timeout=45)
        if process.returncode:
            raise RuntimeError(process.stdout + process.stderr)
        return process.stdout

    run(launch + ["mia-configure-calendar"])
    assert "mia_calendar" in run(launch + ["mcp", "test", "mia_calendar"])
    skill_check = f"""
import json, sys
sys.path.insert(0, {str(SOURCE)!r})
from model_tools import get_tool_definitions
from tools.registry import registry
from tools.skill_provenance import BACKGROUND_REVIEW, set_current_write_origin

get_tool_definitions(enabled_toolsets=["skills"], quiet_mode=True)
name = "mia-report-workflow-check"
def call(tool, args):
    return json.loads(registry.dispatch(tool, args))
def manage(action, **args):
    return call("skill_manage", {{"operations": [{{"action": action, "name": name, **args}}]}})
"""
    run([sys.executable, "-I", "-B", "-c", skill_check + """
created = manage("create", content=(
    "---\\nname: mia-report-workflow-check\\n"
    "description: Use when checking report totals before delivery.\\n---\\n"
    "# Verify reports\\n\\nCompare all totals to the source.\\n"
))
assert created["success"] and not created.get("staged"), created
set_current_write_origin(BACKGROUND_REVIEW)
edit = dict(old_string="Compare all totals to the source.",
            new_string="Compare the totals to the source and record any mismatch.")
assert not manage("patch", **edit)["success"], "Review modified a skill without reading it"
assert "Compare all totals" in str(call("skill_view", {"name": name}))
updated = manage("patch", **edit)
assert updated["success"] and not updated.get("staged"), updated
"""])
    run([sys.executable, "-I", "-B", "-c", skill_check + """
assert name in str(call("skills_list", {}))
assert "record any mismatch" in str(call("skill_view", {"name": name}))
"""])
    checks = run([sys.executable, "-I", "-B", "-c", f"""
import json, sys
sys.path.insert(0, {str(SOURCE)!r})
from hermes_cli.plugins import discover_plugins
from tools.registry import registry
discover_plugins()
print(registry.dispatch("mia_calendar_workflow", {{}}))
print(registry.dispatch("mia_schedule_manage", {{
    "action":"create", "kind":"reminder", "name":"Gateway smoke",
    "text":"Gateway test notification", "schedule":"in 90m"
}}))
"""]).strip().splitlines()
    workflow = json.loads(checks[-2])
    assert "mcp__mia_calendar__mia_calendar" in workflow["instructions"], workflow
    job = json.loads(checks[-1])["job"]
    rules.write_text(json.dumps({"example": {
        "event": "ready", "job": job["id"], "secret_env": "MIA_WEBHOOK_SECRET_SMOKE"
    }}), encoding="utf-8")

    log = workspace / "gateway.log"
    with log.open("wb") as output:
        gateway = subprocess.Popen(launch + ["gateway", "run"], env=env, cwd=workspace,
                                   stdout=output, stderr=subprocess.STDOUT)
        try:
            api_headers = {"Authorization": f"Bearer {API_KEY}"}
            _, toolsets = await_result(gateway, lambda: request(api_port, "/v1/toolsets", headers=api_headers),
                                       lambda result: result[0] == 200, timeout=45)
            active = {row["name"]: set(row["tools"]) for row in toolsets["data"] if row["enabled"]}
            assert "mia_schedule_manage" in active["mia_codex"], toolsets
            assert "mia_browser" in active["mia_codex"], toolsets
            assert "mia_calendar_workflow" in active["mia_codex"], toolsets
            assert {"skills_list", "skill_view", "skill_manage"} <= active["skills"], toolsets
            assert {"mia_work_cards", "mia_templates", "mia_computer_use", "mia_powerpoint_check"} <= active["mia_codex"], toolsets
            assert "mia_execute_code" not in active["mia_codex"], active
            assert not {"browser", "terminal", "cronjob", "code_execution"} & active.keys(), active
            await_result(gateway, lambda: request(webhook_port, "/health"),
                         lambda result: result[0] == 200 and result[1]["service"] == "miawebhook")
            print("PASS: real gateway API toolsets, durable skill review edits, webhook health and calendar MCP probe",
                  flush=True)
            stamp = str(int(time.time()))
            delivery = "gateway-integration-smoke"
            payload = b'{"event":"ready","job":"ignored"}'
            value = hmac.new(SECRET.encode(), f"{stamp}.{delivery}.".encode() + payload, hashlib.sha256).hexdigest()
            headers = {"X-MIA-Timestamp": stamp, "X-MIA-Delivery": delivery, "X-MIA-Signature": value}
            assert request(webhook_port, "/webhooks/example", payload, headers)[0] == 202
            await_result(gateway, lambda: request(webhook_port, "/webhooks/example", payload, headers),
                         lambda result: result[0] == 200 and result[1]["status"] == "completed")
            outbox = workspace / "state" / "scheduled-notices" / "pending"
            await_result(gateway, lambda: list(outbox.glob("*.json")), bool)
            notices = list(outbox.glob("*.json"))
            assert len(notices) == 1
            notice = json.loads(notices[0].read_text(encoding="utf-8"))
            assert notice["job_id"] == job["id"] and "Gateway test notification" in notice["text"]
            print("PASS: authenticated webhook reached its bound native schedule and persisted one notice", flush=True)
        except Exception:
            print(log.read_text(encoding="utf-8", errors="replace")[-10000:], file=sys.stderr)
            raise
        finally:
            if gateway.poll() is None:
                subprocess.run(["taskkill", "/pid", str(gateway.pid), "/t", "/f"],
                               capture_output=True, timeout=10)
            gateway.wait(timeout=10)


if __name__ == "__main__":
    with tempfile.TemporaryDirectory(prefix="mia-gateway-integrations-", dir=RUNTIME) as directory:
        smoke(Path(directory))
