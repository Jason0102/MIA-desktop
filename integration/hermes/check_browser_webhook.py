"""Exercise MIA browser actions on Chromium and the webhook receiver over HTTP."""
import asyncio
from contextlib import contextmanager
import hashlib
import json
from pathlib import Path
import os
import sys
import tempfile
import threading
import time
import re
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer

here = Path(__file__).resolve().parent
runtime = here.parents[1] / ".mia-runtime"
sys.path.insert(0, str(runtime / "hermes/hermes-agent"))
sys.path.insert(0, str(here))
os.environ.update(HERMES_DISABLE_LAZY_INSTALLS="1", HERMES_RUNTIME_DIR=str(runtime / "hermes/tools"))


class Site(BaseHTTPRequestHandler):
    def do_GET(self):
        if self.path == "/file":
            self.send_response(200)
            self.send_header("Content-Disposition", 'attachment; filename="fixture.txt"')
            body = b"browser fixture download"
        else:
            self.send_response(200)
            self.send_header("Content-Type", "text/html")
            body = b"""<title>MIA browser fixture</title><input id="name">
            <button id="submit" onclick="document.getElementById('result').innerText =
            document.getElementById('name').value">Apply</button>
            <p id="result"></p><a id="download" href="/file">Download</a>"""
        self.end_headers()
        self.wfile.write(body)

    def log_message(self, *_args):
        pass


@contextmanager
def web_fixture():
    with ThreadingHTTPServer(("127.0.0.1", 0), Site) as server:
        thread = threading.Thread(target=server.serve_forever, daemon=True)
        thread.start()
        try:
            yield f"http://127.0.0.1:{server.server_port}/"
        finally:
            server.shutdown()
            thread.join()


def browser_check():
    from importlib import import_module
    from tools.browser_session_lifecycle import emergency_cleanup_all_sessions
    from tools.browser_use_cli import stop_harness_daemons
    from tools.interrupt import set_interrupt

    handle = import_module("mia-codex.browser_tools").handle
    task = "mia-browser-fixture"

    def action(kind, **args):
        value = json.loads(handle({"action": kind, **args}, task_id=task))
        assert value.get("success"), value
        return value

    try:
        with web_fixture() as url:
            assert "MIA browser fixture" in action("open", url=url)["output"]
            assert "Apply" in action("inspect")["output"]
            action("fill", selector="#name", text="MIA-browser-form-ok")
            action("click", selector="#submit")
            assert "MIA-browser-form-ok" in action("inspect")["output"]
            files = action("download", selector="#download")["files"]
            assert Path(files[0]).read_bytes() == b"browser fixture download"
            set_interrupt(True)
            try:
                error = json.loads(handle({"action": "inspect"}, task_id=task))
                assert "cancelled" in error["error"]
            finally:
                set_interrupt(False)
        if "--maps" in sys.argv:
            action("open", url="https://www.google.com/maps/dir/Taipei+Main+Station/Taipei+101/")
            for _ in range(8):
                visible = action("inspect")["output"]
                if re.search(r"\d+\s*(?:min\b|分鐘)", visible):
                    return
                time.sleep(1)
            raise AssertionError("Google Maps did not display a route duration")
    finally:
        stop_harness_daemons()
        emergency_cleanup_all_sessions()


async def webhook_check(root):
    from aiohttp import web
    from aiohttp.test_utils import TestClient, TestServer
    from importlib import import_module

    webhook = import_module("mia-codex.webhook_events")
    queue = webhook.Queue(root / "events.db")
    secret = "test-only-" + "a" * 32
    os.environ["MIA_WEBHOOK_SECRET_TEST"] = secret
    rules = {"example": {"event": "ready", "job": "scheduled-id", "secret_env": "MIA_WEBHOOK_SECRET_TEST"}}
    app = web.Application()

    async def receive(request):
        return await webhook._receive(request, rules, queue)

    app.router.add_post("/webhooks/{route}", receive)
    body = b'{"event":"ready","job":"payload-cannot-choose","instructions":"ignored"}'
    now = str(int(time.time()))
    headers = {"X-MIA-Timestamp": now, "X-MIA-Delivery": "fixture-001"}
    async with TestClient(TestServer(app)) as client:
        assert (await client.post("/webhooks/example", data=body, headers=headers)).status == 401
        headers["X-MIA-Signature"] = webhook._signature(secret, now, headers["X-MIA-Delivery"], body)
        assert (await client.post("/webhooks/example", data=body, headers=headers)).status == 202
        duplicate = await client.post("/webhooks/example", data=body, headers=headers)
        assert duplicate.status == 200 and (await duplicate.json())["duplicate"]
        item = queue.claim()
        assert item[2] == "scheduled-id" and queue.claim() is None
        await webhook._dispatch(queue, item, rules, lambda *_args: {"success": True})
        finished = await client.post("/webhooks/example", data=body, headers=headers)
        assert (await finished.json())["status"] == "completed"
    queue.admit("example", "failed", hashlib.sha256(body).hexdigest(), "scheduled-id")
    await webhook._dispatch(queue, queue.claim(), rules, lambda *_args: {"success": False})
    assert queue.admit("example", "failed", hashlib.sha256(body).hexdigest(), "scheduled-id")[1]["status"] == "failed"
    queue.admit("example", "busy", hashlib.sha256(body).hexdigest(), "scheduled-id")
    await webhook._dispatch(queue, queue.claim(), rules, lambda *_args: {"claimed": False, "error": "Job is already running"})
    assert queue.admit("example", "busy", hashlib.sha256(body).hexdigest(), "scheduled-id")[1]["status"] == "queued"
    queue.admit("example", "interrupted", hashlib.sha256(body).hexdigest(), "scheduled-id")
    assert queue.claim()[1] == "interrupted"
    reopened = webhook.Queue(root / "events.db")
    assert reopened.admit("example", "interrupted", hashlib.sha256(body).hexdigest(), "scheduled-id")[1]["status"] == "uncertain"


if __name__ == "__main__":
    with tempfile.TemporaryDirectory(prefix="mia-integrations-", dir=runtime) as directory:
        root = Path(directory)
        os.environ.update(HERMES_HOME=str(root / "profile"), MIA_CODEX_WORKSPACE=str(root),
                          TEMP=str(root), TMP=str(root))
        (root / "downloads").mkdir()
        asyncio.run(webhook_check(root))
        print("PASS: webhook HTTP authentication, deduplication, job binding and failure history", flush=True)
        if "--webhook-only" not in sys.argv:
            browser_check()
            print("PASS: Chromium navigation, extraction, form input, click and download")
