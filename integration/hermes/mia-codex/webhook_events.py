"""Authenticated, durable events that can only trigger a preconfigured MIA cron job."""

import asyncio
import hashlib
import hmac
import json
import logging
import os
from pathlib import Path
import re
import sqlite3
import time
from contextlib import closing

PLATFORM = "miawebhook"
TOKEN = re.compile(r"[A-Za-z0-9_.:-]{1,128}", re.ASCII)
LOG = logging.getLogger(__name__)


def _enabled():
    return bool(os.environ.get("MIA_WEBHOOK_RULES"))


def _rules():
    path = Path(os.environ["MIA_WEBHOOK_RULES"])
    config = json.loads(path.read_text(encoding="utf-8-sig"))
    if not isinstance(config, dict) or not config:
        raise ValueError("At least one webhook rule is required")
    for route, rule in config.items():
        if not TOKEN.fullmatch(route) or not isinstance(rule, dict) or set(rule) != {"event", "job", "secret_env"}:
            raise ValueError("Webhook rules require an event, job and secret_env")
        if not all(isinstance(v, str) and TOKEN.fullmatch(v) for v in rule.values()):
            raise ValueError("Invalid webhook rule value")
        secret_env = rule["secret_env"]
        if not secret_env.startswith("MIA_WEBHOOK_SECRET_") or len(os.environ.get(secret_env, "")) < 32:
            raise ValueError("Set the rule's MIA_WEBHOOK_SECRET_* environment variable (at least 32 characters)")
    return config


def _signature(secret, stamp, delivery, body):
    message = f"{stamp}.{delivery}.".encode() + body
    return hmac.new(secret.encode(), message, hashlib.sha256).hexdigest()


class Queue:
    def __init__(self, path):
        self.path = path
        with closing(self.open()) as db, db:
            db.execute("""CREATE TABLE IF NOT EXISTS events (
                route TEXT, id TEXT, digest TEXT, job TEXT, state TEXT, tries INT, ready REAL, detail TEXT,
                PRIMARY KEY(route,id))""")
            db.execute("""UPDATE events SET state='uncertain', detail='Gateway stopped during dispatch'
                          WHERE state='running'""")

    def open(self):
        return sqlite3.connect(self.path)

    def admit(self, route, delivery, digest, job):
        with closing(self.open()) as db, db:
            db.execute("BEGIN IMMEDIATE")
            row = db.execute("SELECT digest,state,detail FROM events WHERE route=? AND id=?", (route, delivery)).fetchone()
            if row:
                if row[0] != digest:
                    return 409, {"error": "Delivery ID already used for a different body"}
                return 200, {"status": row[1], "detail": row[2], "duplicate": True}
            count = db.execute("SELECT count(*) FROM events WHERE state IN ('queued','running')").fetchone()[0]
            if count >= 100:
                return 503, {"error": "Queue full; retry with the same delivery ID"}
            db.execute("DELETE FROM events WHERE ready < ? AND state NOT IN ('queued','running')",
                       (time.time() - 30 * 86400,))
            db.execute("INSERT INTO events VALUES (?,?,?,?,'queued',0,?,'')",
                       (route, delivery, digest, job, time.time()))
            return 202, {"status": "queued"}

    def claim(self):
        with closing(self.open()) as db, db:
            db.execute("BEGIN IMMEDIATE")
            row = db.execute("""SELECT route,id,job,tries FROM events WHERE state='queued' AND ready<=?
                                ORDER BY ready LIMIT 1""", (time.time(),)).fetchone()
            if row:
                db.execute("UPDATE events SET state='running',tries=tries+1 WHERE route=? AND id=?", row[:2])
            return row

    def finish(self, item, state, detail="", delay=0):
        with closing(self.open()) as db, db:
            db.execute("UPDATE events SET state=?,detail=?,ready=? WHERE route=? AND id=?",
                       (state, detail, time.time() + delay, *item[:2]))


async def _receive(request, rules, queue):
    from aiohttp import web

    route = request.match_info["route"]
    rule = rules.get(route)
    if not rule:
        raise web.HTTPNotFound()
    stamp = request.headers.get("X-MIA-Timestamp", "")
    delivery = request.headers.get("X-MIA-Delivery", "")
    supplied = request.headers.get("X-MIA-Signature", "")
    if not re.fullmatch(r"\d{1,12}", stamp, re.ASCII) or abs(time.time() - int(stamp)) > 300:
        raise web.HTTPUnauthorized()
    if not TOKEN.fullmatch(delivery) or not re.fullmatch("[0-9a-f]{64}", supplied):
        raise web.HTTPUnauthorized()
    body = await request.read()
    expected = _signature(os.environ[rule["secret_env"]], stamp, delivery, body)
    if not hmac.compare_digest(expected, supplied):
        raise web.HTTPUnauthorized()
    try:
        event = json.loads(body)
    except (ValueError, UnicodeError):
        raise web.HTTPBadRequest() from None
    if not isinstance(event, dict) or event.get("event") != rule["event"]:
        return web.json_response({"status": "ignored"})
    status, result = queue.admit(route, delivery, hashlib.sha256(body).hexdigest(), rule["job"])
    return web.json_response(result, status=status)


def _run_job(job, route):
    from cron.jobs import get_job
    from tools.cronjob_tools import execute_job_for_event

    job_config = get_job(job)
    if not job_config or job_config.get("deliver") != "miacron:desktop":
        return {"success": False, "error": "Rule must reference a MIA schedule ID"}
    return execute_job_for_event(job, f"External event matched local rule {route}. Run the saved task.")


async def _dispatch(queue, item, rules, run_job=_run_job):
    route, _, job, tries = item
    if route not in rules or rules[route]["job"] != job:
        queue.finish(item, "failed", "Rule was removed or changed")
        return
    try:
        result = await asyncio.to_thread(run_job, job, route)
        if result.get("success"):
            queue.finish(item, "completed")
        elif not result.get("claimed") and "already running" in str(result.get("error", "")).lower() and tries < 3:
            queue.finish(item, "queued", "Schedule is busy", delay=5 * 2**tries)
        else:
            queue.finish(item, "failed", "Schedule did not complete. Check the MIA schedule history.")
    except Exception:
        LOG.exception("MIA webhook dispatch failed for route %s", route)
        queue.finish(item, "failed", "Dispatch failed. Check the Hermes gateway log.")


def _adapter(config):
    from aiohttp import web
    from gateway.config import Platform
    from gateway.platforms.base import BasePlatformAdapter, SendResult

    class Adapter(BasePlatformAdapter):
        def __init__(self):
            super().__init__(config, Platform(PLATFORM))
            self.runner = None
            self.worker = None

        async def connect(self, *, is_reconnect=False):
            rules = _rules()
            state = Path(os.environ["MIA_CODEX_WORKSPACE"]) / "state"
            queue = Queue(state / "webhook-events.sqlite3")
            app = web.Application(client_max_size=32 * 1024)

            async def receive(request):
                return await _receive(request, rules, queue)

            async def health(_request):
                return web.json_response({"status": "ok", "service": PLATFORM})

            app.router.add_get("/health", health)
            app.router.add_post("/webhooks/{route}", receive)
            self.runner = web.AppRunner(app, access_log=None)
            try:
                await self.runner.setup()
                await web.TCPSite(self.runner, "127.0.0.1",
                                  int(os.environ.get("MIA_WEBHOOK_PORT", "8644"))).start()
            except BaseException:
                await self.runner.cleanup()
                raise

            async def drain():
                while True:
                    item = queue.claim()
                    if item:
                        await _dispatch(queue, item, rules)
                    else:
                        await asyncio.sleep(0.5)

            self.worker = asyncio.create_task(drain())
            return True

        async def disconnect(self):
            if self.runner:
                await self.runner.cleanup()
            if self.worker:
                self.worker.cancel()
                await asyncio.gather(self.worker, return_exceptions=True)

        async def send(self, chat_id, content, reply_to=None, metadata=None):
            return SendResult(success=False, error="MIA webhook is receive-only")

        async def get_chat_info(self, chat_id):
            return {"name": "MIA Webhook", "type": "private"}

    return Adapter()


def register_webhook(ctx):
    ctx.register_platform(
        name=PLATFORM, label="MIA Webhook", adapter_factory=_adapter,
        check_fn=_enabled, validate_config=lambda _config: _enabled(),
        env_enablement_fn=lambda: {"receiver": True} if _enabled() else None,
    )
