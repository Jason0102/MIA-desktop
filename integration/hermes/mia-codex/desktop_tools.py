"""Host desktop operations, separate from the Codex file sandbox."""
import copy
import json
import os
from pathlib import Path
import subprocess
import threading
import uuid

from .bridge import local_c_path

_OFFICE_LOCK = threading.Lock()


def computer_registration():
    from tools.computer_use.schema import COMPUTER_USE_SCHEMA
    from tools.computer_use.tool import handle_computer_use
    schema = copy.deepcopy(COMPUTER_USE_SCHEMA)
    schema["name"] = "mia_computer_use"

    def handle(args, **kwargs):
        # Keep native session isolation, interrupt handling and approval events.
        return handle_computer_use(args, **kwargs)

    return {"name": schema["name"], "toolset": "mia_codex", "schema": schema, "handler": handle}


def powerpoint(args, **_kwargs):
    with _OFFICE_LOCK:
        return _powerpoint(args)


def _powerpoint(args):
    try:
        source = local_c_path(args["path"])
        if source.suffix.lower() not in {".pptx", ".ppt", ".ppsx", ".potx"} or not source.is_file():
            raise ValueError("Supply an existing C:/ PowerPoint presentation.")
        root = local_c_path(os.environ["MIA_CODEX_WORKSPACE"])
        output = root / "checks" / ("powerpoint-" + uuid.uuid4().hex)
        output.mkdir(parents=True)
        script = Path(__file__).with_name("powerpoint.ps1")
        shell = Path(os.environ["SystemRoot"]) / "System32/WindowsPowerShell/v1.0/powershell.exe"
        result = subprocess.run([str(shell), "-NoProfile", "-STA", "-ExecutionPolicy", "Bypass", "-File", str(script),
                                 "-Source", str(source), "-OutputDirectory", str(output)],
                                capture_output=True, timeout=120, creationflags=subprocess.CREATE_NO_WINDOW)
        receipt = output / "result.json"
        if not receipt.is_file():
            raise ValueError("PowerPoint did not produce a verification receipt; rendering is not confirmed.")
        return receipt.read_text(encoding="utf-8-sig")
    except (ValueError, OSError, KeyError, subprocess.SubprocessError) as error:
        return json.dumps({"status": "failed", "error": str(error)}, ensure_ascii=False)


def powerpoint_registration():
    name = "mia_powerpoint_check"
    return {"name": name, "toolset": "mia_codex", "handler": powerpoint, "schema": {
        "name": name, "description": "Open a presentation read-only in host PowerPoint and export slide PNGs for visual inspection. Use after mia_codex_run; COM inside the Codex sandbox cannot control Office. A completed receipt confirms rendering, not visual quality: inspect the returned images before claiming visual validation.",
        "parameters": {"type": "object", "properties": {"path": {"type": "string"}},
                       "required": ["path"], "additionalProperties": False}}}
