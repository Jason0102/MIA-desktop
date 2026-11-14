"""Install and verify the one extension shipped and approved with MIA."""

import json
import os
from pathlib import Path
import shutil
import subprocess
import sys
import tempfile
import threading


PLUGIN_ID = "mia-install-check"
TOOL_NAME = "mia_install_check"
EXPECTED = {"status": "ok", "value": "MIA-LOCAL-PLUGIN-42"}
INSTALL_LOCK = threading.Lock()

FIXTURE_MANIFEST = 'name: mia-install-check\nversion: "1.0.0"\ndescription: MIA local installation diagnostic\n'
FIXTURE_PLUGIN = '''import json

def check(_params, **_kwargs):
    return json.dumps({"status": "ok", "value": "MIA-LOCAL-PLUGIN-42"})

def register(ctx):
    ctx.register_tool(
        name="mia_install_check", toolset="mia_codex",
        schema={
            "name": "mia_install_check", "description": "Return a constant MIA diagnostic marker.",
            "parameters": {"type": "object", "properties": {}, "additionalProperties": False},
        }, handler=check,
    )
'''


def extension_registration():
    name = "mia_install_extension"
    return {
        "name": name, "toolset": "mia_codex", "handler": handle_install,
        "schema": {
            "name": name,
            "description": (
                "When the user asks to install or verify a Hermes extension, install the bundled "
                "MIA installation diagnostic and verify that Hermes can invoke it. This is a local, "
                "no-dependency test extension. No other extension is accepted by this tool. Report the returned marker."
            ),
            "parameters": {"type": "object", "properties": {}, "additionalProperties": False},
        },
    }


def handle_install(params, **_kwargs):
    if params != {}:
        return json.dumps({"status": "failed", "error": "Only the bundled MIA diagnostic can be installed by this tool."})
    if not INSTALL_LOCK.acquire(blocking=False):
        return json.dumps({"status": "busy", "error": "The MIA diagnostic installation is already running."})
    try:
        home = os.environ.get("HERMES_HOME")
        if not home:
            return json.dumps({"status": "failed", "error": "Launch Hermes through the MIA launcher."})
        path = Path(home).resolve()
        return json.dumps(install_diagnostic(path / "hermes-agent", path, log_dir=path / "logs"), ensure_ascii=False)
    finally:
        INSTALL_LOCK.release()


def install_diagnostic(hermes_source, hermes_home, *, log_dir):
    if not (hermes_source / "hermes_cli" / "main.py").is_file():
        return {"status": "failed", "error": "The MIA Hermes installation was not found."}
    env = dict(os.environ, HERMES_HOME=str(hermes_home), PYTHONDONTWRITEBYTECODE="1", PYTHONIOENCODING="utf-8")
    git = shutil.which("git")
    if not git and os.name == "nt":
        package_store = hermes_source.parent / "tools"
        git = next((str(path) for path in sorted(package_store.glob("git-*-win32-*/cmd/git.exe"))), None)
        if git:
            env["PATH"] = str(Path(git).parent) + os.pathsep + os.environ.get("PATH", "")
    if not git:
        return {"status": "failed", "error": "Git is required to install the local diagnostic extension."}
    installed = hermes_home / "plugins" / PLUGIN_ID
    log_path = None
    try:
        log_dir.mkdir(parents=True, exist_ok=True)
        with tempfile.TemporaryDirectory(prefix="mia-extension-", dir=log_dir) as temp:
            source = Path(temp).resolve()
            with tempfile.NamedTemporaryFile(prefix="mia-extension-", suffix=".log", dir=log_dir, delete=False) as log:
                log_path = log.name
                def run(args, cwd):
                    process = subprocess.run(
                        args, cwd=cwd, env=env, stdin=subprocess.DEVNULL, stdout=subprocess.PIPE,
                        stderr=subprocess.STDOUT, check=False, timeout=90,
                        creationflags=subprocess.CREATE_NO_WINDOW if os.name == "nt" else 0,
                    )
                    log.write(process.stdout)
                    log.flush()
                    process.check_returncode()
                    return process.stdout.decode("utf-8", errors="replace")

                (source / "plugin.yaml").write_text(FIXTURE_MANIFEST, encoding="utf-8")
                (source / "__init__.py").write_text(FIXTURE_PLUGIN, encoding="utf-8")
                # Execute the same hands-off commands as the CLI using the installed Python
                # environment. CLI startup can bootstrap an unnecessary second environment
                # for the isolated diagnostic data directory.
                cli = [sys.executable, "-B", "-c"]
                if installed.exists():
                    for name, content in (("plugin.yaml", FIXTURE_MANIFEST), ("__init__.py", FIXTURE_PLUGIN)):
                        file = installed / name
                        if not file.is_file() or file.read_text(encoding="utf-8") != content:
                            return {"status": "failed", "error": "An existing extension uses the diagnostic's ID with different content."}
                    run(cli + ["import sys; from hermes_cli.plugins_cmd import cmd_enable; cmd_enable(sys.argv[1])", PLUGIN_ID], hermes_source)
                else:
                    run([git, "init", "--quiet"], source)
                    run([git, "add", "--", "plugin.yaml", "__init__.py"], source)
                    run([git, "-c", "user.name=MIA Diagnostic", "-c", "user.email=mia-test@example.invalid",
                         "-c", "commit.gpgSign=false", "-c", "core.hooksPath=.git/mia-no-hooks", "commit", "--quiet",
                         "-m", "Add local MIA diagnostic extension"], source)
                    revision = run([git, "rev-parse", "HEAD"], source).strip()
                    install = "import sys; from hermes_cli.plugins_cmd import cmd_install; cmd_install(sys.argv[1], ref=sys.argv[2], enable=True)"
                    run(cli + [install, source.as_uri(), revision], hermes_source)
                if not (installed / "plugin.yaml").is_file():
                    return {"status": "failed", "error": "Hermes did not install the diagnostic extension.", "log_path": log_path}
                dispatch = (
                    "import json; from hermes_cli.plugins import discover_plugins; "
                    "from tools.registry import registry; discover_plugins(); "
                    f"result = registry.dispatch({TOOL_NAME!r}, {{}}); "
                    "result = json.loads(result) if isinstance(result, str) else result; "
                    f"sys.exit(0 if result == {EXPECTED!r} else 1)"
                )
                run([sys.executable, "-B", "-c", "import sys; " + dispatch], hermes_source)
        return {"status": "completed", "extension": PLUGIN_ID, "marker": EXPECTED["value"], "log_path": log_path}
    except (OSError, subprocess.SubprocessError):
        return {
            "status": "failed", "extension": PLUGIN_ID,
            "error": "Hermes could not install, enable, or invoke the diagnostic extension. See the local log.",
            "log_path": log_path,
        }
