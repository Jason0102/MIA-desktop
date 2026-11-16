"""Verify memory reset against real Hermes retirement and a disposable session database."""

import asyncio
import json
import os
from pathlib import Path
import sys
import tempfile
from types import SimpleNamespace

from aiohttp import web

HERE = Path(__file__).resolve().parent
RUNTIME = HERE.parents[1] / ".mia-runtime"
sys.path.insert(0, str(RUNTIME / "hermes/hermes-agent"))
sys.path.append(str(HERE))
os.environ["HERMES_DISABLE_LAZY_INSTALLS"] = "1"

from gateway.platforms.api_server import APIServerAdapter
from gateway.session import Platform, SessionSource
from gateway.session_authority import LiveSession, SessionAuthority
from hermes_state import SessionDB
import hermes_state_runtime as runtime
from mia_session_cleanup import install


async def verify(profile):
    os.environ["HERMES_HOME"] = str(profile)
    with SessionDB(profile / "state.db") as db:
        epoch = runtime.begin_runtime_epoch(db, instance_id="old-gateway")
        for sid in ("interrupted", "queued"):
            db.create_session(sid, source="api_server")
            db.append_message(sid, "user", "Synthetic conversation")
            runtime.admit_session_input(db, epoch=epoch, principal_id="api",
                                        session_id=sid, request_id=f"run-{sid}", payload={})
            runtime.claim_session_input(db, epoch=epoch, session_id=sid)
        runtime.admit_session_input(db, epoch=epoch, principal_id="api",
                                    session_id="queued", request_id="run-follower", payload={})
        epoch = runtime.begin_runtime_epoch(db, instance_id="new-gateway")
        assert runtime.recover_session_inputs(db, epoch=epoch) == 2
        runner = SimpleNamespace(_draining=False)
        authority = SessionAuthority(runner, profile_id=str(profile), instance_id="new-gateway",
                                     db=db, epoch=epoch)
        runner.session_authority = authority
        source = SessionSource(platform=Platform.API_SERVER, chat_id="interrupted")
        authority.sessions["interrupted"] = LiveSession(source, "test")

        class Adapter:
            gateway_runner = runner

            async def _ensure_session_db_async(self):
                return db

            async def _get_existing_session_or_404(self, sid):
                row = db.get_session(sid)
                return row, None if row else web.Response(status=404)

            def _check_auth(self, request):
                if request.headers.get("Authorization") != "Bearer synthetic":
                    return web.Response(status=401)

        async def delete(sid, *, reset=False, authenticated=True):
            headers = {"Authorization": "Bearer synthetic"} if authenticated else {}
            if reset:
                headers["X-MIA-Memory-Reset"] = "1"
            request = SimpleNamespace(headers=headers, match_info={"session_id": sid}, app={})
            return await APIServerAdapter._handle_delete_session(Adapter(), request)

        install()
        assert (await delete("interrupted", reset=True, authenticated=False)).status == 401
        assert (await delete("interrupted")).status == 409
        result = await delete("interrupted", reset=True)
        assert result.status == 200 and json.loads(result.text)["deleted"], result.text
        assert db.get_session("interrupted") is None and db.get_messages("interrupted") == []
        assert (await delete("queued", reset=True)).status == 409
        assert [row["status"] for row in runtime.list_session_admissions(db, session_id="queued")] == ["unknown", "queued"]


if __name__ == "__main__":
    with tempfile.TemporaryDirectory(prefix="mia-session-cleanup-", dir=RUNTIME) as temp:
        asyncio.run(asyncio.wait_for(verify(Path(temp)), timeout=15))
    print("PASS: reset retires the interrupted API run, clears its history, and preserves queued work.")
