"""Recover an interrupted MIA API run during an explicit memory reset."""

import asyncio

from gateway.platforms.api_server import APIServerAdapter, _error_response, _require_auth
from gateway.platforms.api_server_authority_runs import _authority, resolve_unknown_run
from hermes_state_runtime import RuntimeStoreError, list_session_admissions


def install():
    delete_session = APIServerAdapter._handle_delete_session

    @_require_auth
    async def delete_with_recovery(adapter, request):
        try:
            return await delete_session(adapter, request)
        except RuntimeStoreError as exc:
            if exc.reason not in {"unknown_execution", "session_busy"}:
                raise
            if exc.reason != "unknown_execution" or request.headers.get("X-MIA-Memory-Reset") != "1":
                return _error_response(exc.reason, 409, code=exc.reason)

        session_id = request.match_info["session_id"]
        authority = _authority(adapter)
        if authority is None:
            return _error_response("unknown_execution", 409, code="unknown_execution")

        session = await asyncio.to_thread(authority.db.get_session, session_id)
        pending = await asyncio.to_thread(list_session_admissions, authority.db, session_id=session_id)
        # Resolving a lost head can resume its queue. Leave sessions with other pending
        # operations alone and only use the API's own fenced resolution on an API run.
        if not session or session["source"] != "api_server" or len(pending) != 1:
            return _error_response("unknown_execution", 409, code="unknown_execution")
        run = pending[0]
        if run["status"] != "unknown" or run["principal_id"] != "api":
            return _error_response("unknown_execution", 409, code="unknown_execution")

        try:
            await resolve_unknown_run(adapter, run["request_id"], {
                "admission_id": run["admission_id"],
                "execution_generation": run["generation"],
            })
            return await delete_session(adapter, request)
        except RuntimeStoreError as exc:
            return _error_response(exc.reason, 409, code=exc.reason)

    APIServerAdapter._handle_delete_session = delete_with_recovery
