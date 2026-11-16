"""Exercise MIA's gate and pinned Hermes file RPC without requiring Docker."""

import base64
import importlib.util
import json
import os
from pathlib import Path
import shlex
import shutil
import subprocess
import sys
import tempfile
import threading
import time
from unittest.mock import patch


HERE = Path(__file__).resolve().parent
RUNTIME = HERE.parents[1] / ".mia-runtime"
sys.path.insert(0, str(RUNTIME / "hermes/hermes-agent"))


class FileTransportBackend:
    """Replace only Docker I/O; pinned Hermes still generates and polls real RPC files."""

    def __init__(self, root):
        self.root = root

    def get_temp_dir(self):
        return "/mia-code"

    def path(self, path):
        return self.root / Path(path).relative_to("/mia-code")

    @staticmethod
    def result(output="", returncode=0):
        return {"output": output, "returncode": returncode}

    def execute(self, command, *, cwd=None, timeout=10, stdin_data=None):
        if command.startswith("command -v python3"):
            return self.result("OK")
        args = shlex.split(command)
        if args[:5] == ["umask", "077", "&&", "mkdir", "-p"]:
            self.path(args[5]).mkdir(parents=True, exist_ok=True)
        elif args[:5] == ["umask", "077", "&&", "base64", "-d"]:
            target = self.path(args[6])
            target.write_bytes(base64.b64decode(stdin_data))
            if "mv" in args:
                target.replace(self.path(args[-1]))
        elif args[:2] == ["ls", "-1"]:
            parent = self.path(args[2]).parent
            return self.result("\n".join(f"/mia-code/{p.relative_to(self.root).as_posix()}"
                                         for p in parent.glob("req_*")))
        elif args[:1] == ["cat"]:
            return self.result(self.path(args[1]).read_text(encoding="utf-8"))
        elif args[:2] == ["rm", "-f"]:
            self.path(args[2]).unlink(missing_ok=True)
        elif args[:2] == ["rm", "-rf"]:
            shutil.rmtree(self.path(args[2]))
        elif args[0] == "cd" and args[-4:] == ["exec", "python3", "script.py", ")"]:
            return self.run_script(self.path(args[1]), timeout)
        else:
            raise AssertionError(f"unexpected Hermes command: {command}")
        return self.result()

    def run_script(self, directory, timeout):
        from tools.interrupt import is_interrupted

        env = {name: os.environ[name] for name in ("SYSTEMROOT", "WINDIR") if name in os.environ}
        for line in (directory / "sandbox.env").read_text(encoding="utf-8").splitlines():
            name, value = line.split("=", 1)
            env[name] = shlex.split(value)[0]
        env["HERMES_RPC_DIR"] = str(self.path(env["HERMES_RPC_DIR"]))
        with subprocess.Popen([sys.executable, "-I", "-B", "-c",
                               "import runpy,sys; sys.path.insert(0,'.'); runpy.run_path('script.py',run_name='__main__')"],
                              cwd=directory, env=env, stdout=subprocess.PIPE, stderr=subprocess.STDOUT,
                              text=True, encoding="utf-8") as process:
            deadline = time.monotonic() + timeout
            while True:
                cancelled = is_interrupted()
                if cancelled or time.monotonic() >= deadline:
                    process.kill()
                    output, _ = process.communicate()
                    return self.result(output, 130 if cancelled else 124)
                try:
                    output, _ = process.communicate(timeout=0.05)
                    return self.result(output, process.returncode)
                except subprocess.TimeoutExpired:
                    pass


def verify(root):
    from tools import code_execution_rpc, code_execution_tool, code_kernel_remote, terminal_tool
    from tools.environments.docker import DockerEnvironment
    from tools.interrupt import set_interrupt

    spec = importlib.util.spec_from_file_location("mia_isolated_code", HERE / "mia-codex/isolated_code.py")
    isolated = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(isolated)
    secure_terminal = {
        "TERMINAL_ENV": "docker", "TERMINAL_DOCKER_NETWORK": "false",
        "TERMINAL_DOCKER_MOUNT_CWD_TO_WORKSPACE": "false", "TERMINAL_DOCKER_VOLUMES": "[]",
        "TERMINAL_DOCKER_ENV": "{}", "TERMINAL_DOCKER_EXTRA_ARGS": "[]",
        "TERMINAL_DOCKER_FORWARD_ENV": "[]", "TERMINAL_CONTAINER_PERSISTENT": "false",
        "TERMINAL_DOCKER_PERSIST_ACROSS_PROCESSES": "false", "TERMINAL_DOCKER_SHARED_CONTAINER_KEY": "",
    }
    profile = {"code_execution": {"mode": "strict", "timeout": 5, "max_tool_calls": 2}, "proxy": {"enabled": False}}
    backend = FileTransportBackend(root)
    observed = []

    def dispatch(name, args):
        observed.append(name)
        assert name in {"web_search", "web_extract"}, "a forbidden tool was dispatched"
        return json.dumps({"result": args["query"]} if name == "web_search" else {"error": "fixture unavailable"})

    def call(code):
        return json.loads(isolated.handle({"code": code}, task_id="mia-isolated-check"))

    with patch.dict(os.environ, secure_terminal), patch.object(terminal_tool, "_ensure_terminal_env_bridged"), \
         patch("hermes_cli.config.load_config_readonly", return_value=profile), \
         patch.object(code_execution_tool, "_get_or_create_env", return_value=(backend, "docker")), \
         patch.object(code_kernel_remote, "execute_in_remote_kernel", return_value=None), \
         patch.object(code_execution_rpc, "_default_dispatch", return_value=dispatch):
        os.environ.pop("MIA_ISOLATED_CODE", None)
        assert "error" in call("print('disabled')")
        os.environ["MIA_ISOLATED_CODE"] = "1"
        assert isolated.block_native_code("execute_code")["action"] == "block"
        assert isolated.block_native_code("web_search") is None
        for key, unsafe in (
            ("TERMINAL_ENV", "local"), ("TERMINAL_DOCKER_NETWORK", "true"),
            ("TERMINAL_DOCKER_FORWARD_ENV", '["SECRET"]'), ("TERMINAL_DOCKER_EXTRA_ARGS", '["--privileged"]'),
            ("TERMINAL_DOCKER_MOUNT_CWD_TO_WORKSPACE", "true"), ("TERMINAL_DOCKER_VOLUMES", '["host:guest"]'),
            ("TERMINAL_CONTAINER_PERSISTENT", "true"), ("TERMINAL_DOCKER_PERSIST_ACROSS_PROCESSES", "true"),
            ("TERMINAL_DOCKER_SHARED_CONTAINER_KEY", "shared"),
        ):
            with patch.dict(os.environ, {key: unsafe}):
                assert "error" in call("print('must be refused')"), key
        profile["proxy"]["enabled"] = True
        assert "error" in call("print('no proxy')")
        profile["proxy"]["enabled"] = False
        with patch("tools.env_passthrough.get_all_passthrough", return_value={"LOCAL_TEST_SECRET"}):
            assert "error" in call("print('no implicit passthrough')")

        config = terminal_tool._get_env_config()
        docker = object.__new__(DockerEnvironment)
        docker._persistent = False
        with patch("tools.environments.docker._cgroup_limits_available", return_value=True):
            flags = docker._resource_args("fixture", 1, 128, 0, config["docker_network"], "1g", [])
        assert "--network=none" in flags
        cwd = terminal_tool._resolve_task_host_cwd(config, "mia-isolated-check")
        assert cwd is None
        volumes, writable = docker._mount_args([], cwd, False, "mia-isolated-check")
        assert not volumes and "-v" not in writable and "--tmpfs" in writable
        result = call("""
from hermes_tools import web_search, web_extract, _call
print(web_search(query='fixture')['result'])
print(web_extract(urls=['https://example.invalid'])['error'])
print(_call('terminal', {})['error'])
print(web_search(query='over budget')['error'])
""")
        assert result["status"] == "success" and result["exit_code"] == 0, result
        assert result["tool_calls_made"] == 2 and result["tool_errors"][0]["tool"] == "web_extract", result
        assert "not available" in result["output"] and "limit reached" in result["output"], result
        assert observed == ["web_search", "web_extract"], observed

        profile["code_execution"]["timeout"] = 1
        timed = call("import time\ntime.sleep(4)")
        assert timed["status"] == "timeout", timed
        profile["code_execution"]["timeout"] = 5
        tid = threading.get_ident()
        timer = threading.Timer(0.5, set_interrupt, args=(True, tid))
        timer.start()
        try:
            stopped = call("import time\ntime.sleep(4)")
            assert stopped["status"] == "interrupted", stopped
        finally:
            timer.join()
            set_interrupt(False, tid)
    assert not list(root.iterdir()), "remote scratch paths were not cleaned up"


def verify_plugin(profile):
    plugin = profile / "plugins" / "mia-codex"
    shutil.copytree(HERE / "mia-codex", plugin, ignore=shutil.ignore_patterns("__pycache__"))
    (profile / "config.yaml").write_text("plugins:\n  enabled: [mia-codex]\n", encoding="utf-8")
    code = """
import json, os, sys
sys.path.insert(0, os.environ["MIA_HERMES_SOURCE"])
from hermes_cli.plugins import discover_plugins
from model_tools import handle_function_call
from tools.registry import registry
discover_plugins()
enabled = os.environ["MIA_ISOLATED_CODE"] == "1"
assert ("mia_execute_code" in registry.get_all_tool_names()) == enabled
native = json.loads(handle_function_call("execute_code", {"code":"raise RuntimeError('ran natively')"}, task_id="fixture"))
assert "mia_execute_code" in native["error"], native
if enabled:
    wrapper = json.loads(handle_function_call("mia_execute_code", {"code":"raise RuntimeError('ran locally')"}, task_id="fixture"))
    assert "refused to execute" in wrapper["error"], wrapper
"""
    for enabled in ("0", "1"):
        env = dict(os.environ, HERMES_HOME=str(profile), TERMINAL_ENV="local",
                   HERMES_DISABLE_LAZY_INSTALLS="1", MIA_ISOLATED_CODE=enabled,
                   MIA_HERMES_SOURCE=str(RUNTIME / "hermes/hermes-agent"))
        result = subprocess.run([sys.executable, "-I", "-B", "-c", code], env=env,
                                encoding="utf-8", errors="replace", capture_output=True, timeout=35)
        assert result.returncode == 0, (result.stdout, result.stderr)


if __name__ == "__main__":
    with tempfile.TemporaryDirectory(prefix="mia-isolated-exec-", dir=RUNTIME) as directory:
        profile = Path(directory) / "profile"
        os.environ["HERMES_HOME"] = str(profile)
        scratch = Path(directory) / "sandbox"
        scratch.mkdir()
        verify(scratch)
        verify_plugin(profile)
    print("PASS: plugin opt-in, isolation refusals, Docker flags, file RPC, partial failures, budgets, timeout and stop.")
