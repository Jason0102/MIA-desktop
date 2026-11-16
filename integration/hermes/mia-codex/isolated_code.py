"""MIA's fail-closed entry point for Hermes Docker Python execution."""

import json
import os


_RPC_TOOLS = ("web_search", "web_extract")
_MAX_CODE_LENGTH = 24_000
_DISALLOWED_DOCKER_OPTIONS = (
    "docker_network", "docker_mount_cwd_to_workspace", "docker_volumes",
    "docker_env", "docker_extra_args", "docker_forward_env", "container_persistent",
    "docker_persist_across_processes", "docker_shared_container_key",
)


def _failure(message):
    return json.dumps({"error": message}, ensure_ascii=False)


def block_native_code(tool_name, **_kwargs):
    if tool_name == "execute_code":
        return {"action": "block", "message": "Use mia_execute_code in MIA's isolated Docker mode."}
    return None


def registration():
    name = "mia_execute_code"
    return {
        "name": name,
        "toolset": "mia_codex",
        "schema": {
            "name": name,
            "description": (
                "Run Python in MIA's isolated Docker container for computation and multi-step web research. "
                "Use 'from hermes_tools import web_search, web_extract' to call the two permitted tools. "
                "Direct network access, host files, terminal RPC and other tool RPC are unavailable. "
                "Each call has a 60-second deadline and up to 12 tool calls. Print the desired output. "
                "Check status, tool_errors, and exit_code when present before reporting success."
            ),
            "parameters": {
                "type": "object",
                "properties": {
                    "code": {"type": "string", "minLength": 1, "maxLength": _MAX_CODE_LENGTH},
                    "reset": {"type": "boolean", "description": "Start a new Python kernel for this session."},
                },
                "required": ["code"],
                "additionalProperties": False,
            },
        },
        "handler": handle,
    }


def handle(args, *, task_id=None, **_kwargs):
    if os.environ.get("MIA_ISOLATED_CODE") != "1":
        return _failure("Launch MIA with -EnableIsolatedCode and an available Docker instance.")
    code = args.get("code")
    reset = args.get("reset", False)
    if (not isinstance(code, str) or not code.strip() or len(code) > _MAX_CODE_LENGTH
            or not isinstance(reset, bool) or args.keys() - {"code", "reset"}):
        return _failure("Supply Python source in code and an optional Boolean reset.")
    if not task_id:
        return _failure("MIA could not identify an isolated execution session.")
    try:
        from hermes_cli.config import load_config_readonly
        from tools.code_execution_tool import execute_code
        from tools.env_passthrough import get_all_passthrough
        from tools.terminal_tool import _get_env_config

        terminal = _get_env_config()
        config = load_config_readonly()
        execution = config.get("code_execution") or {}
        proxy = config.get("proxy") or {}
        allowed = (
            terminal["env_type"] == "docker"
            and all(not terminal[key] for key in _DISALLOWED_DOCKER_OPTIONS)
            and not proxy.get("enabled")
            and not get_all_passthrough()
            and execution.get("mode") == "strict"
            and 0 < execution.get("timeout", 0) <= 60
            and 0 < execution.get("max_tool_calls", 0) <= 12
        )
    except Exception:
        return _failure("MIA could not verify the Docker isolation settings.")
    if not allowed:
        return _failure("MIA refused to execute code because the Docker isolation settings differ from its launch profile.")
    # Stock Hermes exposes the full sandbox allow-list when the intersection
    # with enabled tools is empty. Pass our explicit, nonempty subset even if a
    # web provider is unavailable. Its ordinary error can be handled by Python.
    return execute_code(code, task_id=task_id, enabled_tools=list(_RPC_TOOLS), reset=reset)
