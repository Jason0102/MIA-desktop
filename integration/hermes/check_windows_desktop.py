"""Exercise the installed Hermes CUA backend on a test-owned Windows Form."""

import ctypes
from ctypes import wintypes
import json
import os
from pathlib import Path
import shutil
import subprocess
import sys
import tempfile
import time
import uuid


FORM_SCRIPT = r"""
param([string]$Title, [string]$ReadyFile, [string]$ResultFile, [string]$Marker)
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Windows.Forms
Add-Type -AssemblyName System.Drawing
$form = [Windows.Forms.Form]::new()
$form.Text = $Title
$form.ClientSize = [Drawing.Size]::new(340, 120)
$form.StartPosition = 'CenterScreen'
$button = [Windows.Forms.Button]::new()
$button.Text = 'Write MIA result'
$button.AccessibleName = 'Write MIA result'
$button.SetBounds(72, 35, 196, 48)
$button.Add_Click({
    [IO.File]::WriteAllText($ResultFile, $Marker)
    $button.Text = 'Result written'
})
$form.Controls.Add($button)
$form.Add_Shown({
    [IO.File]::WriteAllText($ReadyFile, "$PID $($form.Handle.ToInt64())")
})
$expiry = [Windows.Forms.Timer]::new()
$expiry.Interval = 110000
$expiry.Add_Tick({ $form.Close() })
$expiry.Start()
try { [Windows.Forms.Application]::Run($form) } finally { $expiry.Dispose(); $form.Dispose() }
"""


def wait_for(callback, timeout):
    deadline = time.monotonic() + timeout
    while time.monotonic() < deadline:
        if value := callback():
            return value
        time.sleep(0.1)
    return None


def assert_test_window(pid, handle, title):
    user32 = ctypes.WinDLL("user32", use_last_error=True)
    user32.GetWindowThreadProcessId.argtypes = [wintypes.HWND, ctypes.POINTER(wintypes.DWORD)]
    user32.GetWindowTextW.argtypes = [wintypes.HWND, wintypes.LPWSTR, ctypes.c_int]
    owner = wintypes.DWORD()
    name = ctypes.create_unicode_buffer(256)
    user32.GetWindowThreadProcessId(handle, ctypes.byref(owner))
    user32.GetWindowTextW(handle, name, len(name))
    if owner.value != pid or name.value != title:
        raise RuntimeError("The test window no longer matches its expected process and title.")


def smoke_test(home, hermes_source, check_dir):
    os.environ["HERMES_HOME"] = str(home)
    sys.path.insert(0, str(hermes_source))
    from tools.computer_use.cua_backend import CuaDriverBackend, cua_driver_runtime_contract_status

    driver = cua_driver_runtime_contract_status()
    if not driver["ready"]:
        raise RuntimeError(f"Hermes Computer Use is unavailable: {driver['reason']}")

    token = uuid.uuid4().hex
    title = f"MIA 3.2 desktop check {token[:10]}"
    marker = f"MIA-CUA-{token}"
    form_script = check_dir / "test-window.ps1"
    ready_file = check_dir / "window-ready.txt"
    result_file = check_dir / "click-result.txt"
    form_script.write_text(FORM_SCRIPT, encoding="utf-8-sig")
    powershell = shutil.which("pwsh.exe")
    if not powershell:
        raise RuntimeError("PowerShell 7 is required to launch the test fixture.")
    args = [powershell, "-NoProfile", "-STA", "-File", str(form_script), title,
            str(ready_file), str(result_file), marker]
    with (check_dir / "test-window.log").open("wb") as log:
        form = subprocess.Popen(args, stdin=subprocess.DEVNULL, stdout=log, stderr=subprocess.STDOUT,
                                creationflags=subprocess.CREATE_NO_WINDOW)
        try:
            def ready_record():
                if form.poll() is not None:
                    raise RuntimeError("The isolated Windows Form exited. Check test-window.log.")
                if ready_file.is_file():
                    return ready_file.read_text(encoding="utf-8").strip()
                return None

            ready = wait_for(ready_record, timeout=12)
            if ready is None:
                raise RuntimeError("The isolated Windows Form did not start. Check test-window.log.")
            pid, handle = map(int, ready.split())
            if pid != form.pid:
                raise RuntimeError("The test window reported an unexpected process ID.")
            assert_test_window(pid, handle, title)

            backend = CuaDriverBackend(permission_mode="standard")
            try:
                backend.start()
                capture = backend.capture(mode="som", pid=pid, window_id=handle)
                buttons = [element for element in capture.elements
                           if "Write MIA result" in element.label and "button" in element.role.lower()]
                if len(buttons) != 1:
                    raise RuntimeError("Hermes did not find the test button in the window accessibility tree.")
                assert_test_window(pid, handle, title)
                result = backend.click(element=buttons[0].index, delivery_mode="background")
                if not result.ok:
                    raise RuntimeError(f"Hermes click failed: {result.message or result.code or 'unknown error'}")
                if not wait_for(lambda: result_file.is_file() and result_file.read_text(encoding="utf-8") == marker,
                                timeout=5):
                    raise RuntimeError("The driver returned success, but the test window did not write its marker.")
                return {"status": "completed", "driver_version": driver["version"], "window": title,
                        "action": "background_click", "driver_effect": result.effect,
                        "marker_verified": True, "marker_file": str(result_file)}
            finally:
                backend.stop()
        finally:
            if form.poll() is None:
                form.terminate()
                form.wait(timeout=5)


def main():
    if sys.platform != "win32":
        raise SystemExit("The Hermes desktop check requires an interactive Windows session.")
    runtime = Path(__file__).resolve().parents[2] / ".mia-runtime"
    home = runtime / "hermes"
    source = home / "hermes-agent"
    if not (source / "tools/computer_use/cua_backend.py").is_file():
        raise SystemExit("Install Hermes into .mia-runtime first.")
    check_dir = Path(tempfile.mkdtemp(prefix="desktop-check-", dir=runtime))
    try:
        result = smoke_test(home, source, check_dir)
    except Exception as exc:
        result = {"status": "failed", "error": str(exc), "directory": str(check_dir)}
    print(json.dumps(result, ensure_ascii=True, indent=2))
    return 0 if result["status"] == "completed" else 1


if __name__ == "__main__":
    raise SystemExit(main())
