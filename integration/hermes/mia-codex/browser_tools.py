"""Expose browser actions without exposing the browser harness's Python interpreter."""

import hashlib
import json
import os
from pathlib import Path
import time
from urllib.parse import urlsplit
from uuid import uuid4
from .permissions import add_operation_fields, check_operation


def _text(value, label, limit=2048, *, empty=False):
    if not isinstance(value, str) or len(value) > limit or (not empty and not value.strip()):
        raise ValueError(f"Invalid {label}")
    return value


def _download_root():
    from .bridge import local_c_path
    root = Path(os.environ["MIA_CODEX_WORKSPACE"])
    return local_c_path(str(root / "downloads"))


_INSPECT = """JSON.stringify({
  text: document.body.innerText.slice(0, 14000),
  controls: [...document.querySelectorAll('a,button,input,textarea,select,[role=button],[role=combobox]')]
    .filter(el => el.getClientRects().length).slice(0, 100).map(el => ({
      tag: el.localName, id: el.id, name: el.getAttribute('name'),
      label: el.getAttribute('aria-label') || el.getAttribute('placeholder') ||
        (el.innerText || '').trim().slice(0, 120)
    }))
})"""


def _build_action(args):
    action = args.get("action")
    if action == "open":
        url = _text(args.get("url"), "URL")
        parsed = urlsplit(url)
        if parsed.scheme not in {"https", "http"} or not parsed.hostname or parsed.username is not None:
            raise ValueError("Use an HTTP(S) URL without embedded credentials")
        return f"new_tab({url!r})\nwait_for_load()\nprint(page_info())"
    if action == "inspect":
        return f"print(page_info())\nprint(js({_INSPECT!r}))"
    if action == "fill":
        selector = _text(args.get("selector"), "selector", 512)
        value = _text(args.get("text"), "input", 4096, empty=True)
        return f"fill_input({selector!r}, {value!r})\nprint(page_info())"
    if action in {"click", "download"}:
        selector = _text(args.get("selector"), "selector", 512)
        # The selector is a JS string literal, and the complete expression is a Python string literal.
        expression = (
            "(() => {const el=document.querySelector(" + json.dumps(selector) +
            "); if(!el || !el.getClientRects().length) throw Error('Visible target not found');"
            " el.click(); return true;})()"
        )
        return f"print(js({expression!r}))"
    if action == "press":
        key = args.get("key")
        if key not in {"Enter", "Escape", "Tab", "ArrowDown", "ArrowUp"}:
            raise ValueError("Unsupported key")
        return f"press_key({key!r})\nprint(page_info())"
    raise ValueError("Unknown browser action")


def handle(args, **kwargs):
    from tools.browser_use_cli import browser_exec
    from tools.interrupt import is_interrupted
    from tools.registry import tool_error

    try:
        error = check_operation("mia_browser", args)
        if error:
            return tool_error(error)
        scope = kwargs.get("task_id")
        if not scope:
            return tool_error("Browser actions require a MIA conversation")
        session = "mia-" + hashlib.sha256(str(scope).encode()).hexdigest()[:24]
        code = _build_action(args)
        if is_interrupted():
            return tool_error("Browser action cancelled")
        if args["action"] != "download":
            return browser_exec(code, session=session, task_id=scope, timeout_s=25)

        folder = _download_root() / uuid4().hex
        folder.mkdir(parents=True)
        code = (
            f'cdp("Browser.setDownloadBehavior", behavior="allow", downloadPath={str(folder)!r})\n'
            + code
        )
        result = json.loads(browser_exec(code, session=session, task_id=scope, timeout_s=25))
        if not result.get("success"):
            return json.dumps(result, ensure_ascii=False)
        end = time.monotonic() + 20
        while time.monotonic() < end and not is_interrupted():
            paths = list(folder.iterdir())
            files = [p for p in paths if p.is_file() and not p.is_symlink()]
            if files and not any(p.suffix.lower() in {".tmp", ".crdownload"} for p in paths):
                return json.dumps({"success": True, "files": [str(p) for p in files]}, ensure_ascii=False)
            time.sleep(0.2)
        return tool_error(f"Download cancelled or not confirmed complete: {folder}")
    except (ValueError, OSError, KeyError) as exc:
        return tool_error(str(exc))


def registration():
    result = {
        "name": "mia_browser", "toolset": "mia_codex", "handler": handle,
        "schema": {
            "name": "mia_browser",
            "description": "Operate visible browser pages for MIA. Open an HTTP(S) site, inspect text and interactive selectors, click, fill, press a key, or download by clicking a visible download control. Reuse the same browser within a task. Never set passwords or submit sensitive forms unless the user requests that action.",
            "parameters": {
                "type": "object",
                "properties": {
                    "action": {"enum": ["open", "inspect", "click", "fill", "press", "download"]},
                    "url": {"type": "string"}, "selector": {"type": "string"},
                    "text": {"type": "string"}, "key": {"type": "string"},
                },
                "required": ["action"], "additionalProperties": False,
            },
        },
    }
    add_operation_fields(result["schema"])
    return result
