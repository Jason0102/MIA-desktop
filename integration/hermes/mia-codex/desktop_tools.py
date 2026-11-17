"""Host desktop operations, separate from the Codex file sandbox."""
import copy
import json
import os
from pathlib import Path
import subprocess
import shutil
import threading
import uuid

from .bridge import local_c_path
from .permissions import add_operation_fields, check_operation

_OFFICE_LOCK = threading.Lock()


def computer_registration():
    from tools.computer_use import cua_backend_parse
    from tools.computer_use.schema import COMPUTER_USE_SCHEMA
    from tools.computer_use.tool import handle_computer_use
    # The pinned Hermes parser only lists macOS/Linux modifiers.
    cua_backend_parse._MODIFIER_NAMES |= {"win"}
    cua_backend_parse._KEY_ALIASES.update(windows="win", super="win", meta="win")
    schema = copy.deepcopy(COMPUTER_USE_SCHEMA)
    schema["name"] = "mia_computer_use"
    schema["parameters"]["properties"]["action"]["enum"].extend(["draw_path", "launch_app"])
    schema["parameters"]["properties"]["app"]["description"] += " For launch_app use an installed .exe path or mspaint.exe (小畫家). After launch, list windows and capture the target before input."
    schema["parameters"]["properties"]["points"] = {
        "type": "array", "minItems": 2, "maxItems": 128,
        "items": {"type": "array", "items": {"type": "integer"}, "minItems": 2, "maxItems": 2},
        "description": "Observed canvas coordinates forming a pencil stroke. Select the pencil first. Each adjacent pair is dragged, keeping the drawing in the actual application."
    }
    add_operation_fields(schema)

    def handle(args, **kwargs):
        error = check_operation("mia_computer_use", args)
        if error:
            return json.dumps({"error": error}, ensure_ascii=False)
        if args.get("action") == "launch_app":
            app = args.get("app", "")
            if app in ("小畫家", "Paint", "paint"):
                app = "mspaint.exe"
            executable = shutil.which(app) if isinstance(app, str) else None
            if not executable or Path(executable).suffix.lower() != ".exe":
                return json.dumps({"error": "請指定已安裝的應用程式 exe 路徑。"}, ensure_ascii=False)
            try:
                process = subprocess.Popen([executable])
                return json.dumps({"status": "launch_requested", "pid": process.pid,
                                   "next": "List windows and capture the application before input."})
            except OSError as error:
                return json.dumps({"error": str(error)})
        if args.get("action") == "draw_path":
            points = args.get("points")
            if not isinstance(points, list) or not 2 <= len(points) <= 128 or any(
                not isinstance(p, list) or len(p) != 2 or any(type(v) is not int or v < 0 for v in p)
                for p in points):
                return json.dumps({"error": "Supply 2–128 canvas coordinate pairs."})
            base = {k: v for k, v in args.items() if k not in {"points", "from_element", "to_element"}}
            for start, end in zip(points, points[1:]):
                result = handle_computer_use(normalize_computer_args({
                    **base, "action": "drag", "from_coordinate": start, "to_coordinate": end,
                    "capture_after": False}), **kwargs)
                if isinstance(result, str) and json.loads(result).get("error"):
                    return result
            return handle_computer_use({"action": "capture", "mode": "vision", "app": args.get("app")}, **kwargs)
        return handle_computer_use(normalize_computer_args(args), **kwargs)

    return {"name": schema["name"], "toolset": "mia_codex", "schema": schema, "handler": handle}


def normalize_computer_args(args):
    args = {key: value for key, value in args.items() if key not in {"operation_kind", "operation_target"}}
    if args.get("action") == "key" and args.get("modifiers"):
        parts = str(args.get("keys", "")).split("+")
        modifiers = [value for value in args.pop("modifiers") if value.casefold() not in {part.casefold() for part in parts}]
        args["keys"] = "+".join([*modifiers, *parts])
    return args


def desktop_note(args, **_kwargs):
    import ctypes

    text = args.get("text")
    if not isinstance(text, str) or not text.strip() or len(text) > 4000:
        return json.dumps({"error": "Supply 1–4000 characters of note text."})
    try:
        desktop = ctypes.create_unicode_buffer(32768)
        if ctypes.windll.shell32.SHGetFolderPathW(None, 0x10, None, 0, desktop) != 0:
            raise OSError("Windows did not return the Desktop folder")
        folder = local_c_path(desktop.value)
        path = folder / ("米亞的貼心提醒-" + uuid.uuid4().hex[:12] + ".txt")
        with path.open("x", encoding="utf-8-sig") as output:
            output.write(text.strip() + "\n")
        if path.read_text(encoding="utf-8-sig") != text.strip() + "\n":
            raise OSError("Note verification failed")
        notepad = Path(os.environ["SystemRoot"]) / "System32/notepad.exe"
        subprocess.Popen([str(notepad), str(path)], creationflags=subprocess.CREATE_NO_WINDOW)
        return json.dumps({"status": "saved_open_requested", "path": str(path),
                           "next": "Capture Notepad with mia_computer_use and verify that the note is visible."}, ensure_ascii=False)
    except (ValueError, OSError) as error:
        return json.dumps({"error": str(error)}, ensure_ascii=False)


def note_registration():
    return {
        "name": "mia_desktop_note", "toolset": "mia_codex", "handler": desktop_note,
        "schema": {
            "name": "mia_desktop_note",
            "description": "Create a new UTF-8 text note on the Windows Desktop and open it in Notepad. Use when Sticky Notes is unavailable or cannot be operated after two attempts. Verify visibility with computer use before reporting completion.",
            "parameters": {"type": "object", "properties": {"text": {"type": "string", "maxLength": 4000}},
                           "required": ["text"], "additionalProperties": False},
        },
    }


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
