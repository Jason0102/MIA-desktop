"""Local bridge checks. No model calls and no real project input."""

import importlib.util
import os
from pathlib import Path
import sys
import tempfile
import time


spec = importlib.util.spec_from_file_location("mia_codex_bridge", Path(__file__).parent / "mia-codex" / "bridge.py")
bridge = importlib.util.module_from_spec(spec)
spec.loader.exec_module(bridge)


def test_staging(root):
    user_input = root / "input"
    user_input.mkdir()
    source = user_input / "測試資料.txt"
    source.write_text("MIA-CONTRACT-42", encoding="utf-8")
    env = {"MIA_CODEX_WORKSPACE": str(root), "MIA_CODEX_EXECUTABLE": sys.executable}

    def simulate(command, prompt, job, env, timeout, interrupted):
        assert "--ignore-user-config" in command and "--sandbox" in command and command[-1] == "-"
        assert "核對文件" in prompt and "核對文件" not in command
        staged = job / "input" / source.name
        assert staged.read_text(encoding="utf-8") == "MIA-CONTRACT-42"
        staged.write_text("copy changed", encoding="utf-8")
        (job / "output" / "檢查結果.txt").write_text("checked", encoding="utf-8")
        (job / "codex-result.txt").write_text("已核對 MIA-CONTRACT-42", encoding="utf-8")
        return "completed", 0, ""

    result = bridge.run_task({"task": "核對文件", "inputs": [source.name], "expected_artifacts": ["檢查結果.txt"]}, env, lambda: False, simulate)
    assert result.get("status") == "completed", result
    assert result["output"] == "已核對 MIA-CONTRACT-42"
    assert result["artifacts"][0]["size_bytes"] == 7
    assert Path(result["artifacts"][0]["path"]).parent == root / "jobs" / result["job_id"] / "output"
    assert source.read_text(encoding="utf-8") == "MIA-CONTRACT-42"
    result = bridge.run_task({"task": "核對文件", "inputs": [source.name], "expected_artifacts": ["missing.txt"]}, env, lambda: False, simulate)
    assert result.get("status") == "failed" and result["missing_artifacts"] == ["missing.txt"], result

    for invalid in ("../x", "C:\\some-file.txt", "/etc/path", "\\\\server\\share\\file", "file:stream"):
        result = bridge.run_task({"task": "核對文件", "inputs": [invalid]}, env, lambda: False, simulate)
        assert result.get("status") == "failed" and "job_id" not in result, result

    def empty_result(*_args):
        return "completed", 0, ""

    result = bridge.run_task({"task": "核對文件"}, env, lambda: False, empty_result)
    assert result.get("status") == "failed" and "output" not in result, result

    def interrupted_result(*_args):
        return "cancelled", None, "cancelled"

    result = bridge.run_task({"task": "核對文件"}, env, lambda: True, interrupted_result)
    assert result.get("status") == "cancelled" and "output" not in result, result


def test_process(root):
    echo = [sys.executable, "-c", "from pathlib import Path; import sys; Path('echo.result').write_bytes(sys.stdin.buffer.read())"]
    assert bridge.execute(echo, "測試資料", root, os.environ, 5, lambda: False) == ("completed", 0, "")
    assert (root / "echo.result").read_text(encoding="utf-8") == "測試資料"
    fail = [sys.executable, "-c", "import sys; sys.stderr.write('internal secret'); sys.exit(7)"]
    state, code, reason = bridge.execute(fail, "資料", root, os.environ, 5, lambda: False)
    assert (state, code) == ("failed", 7) and "internal secret" not in reason
    sleeper = [sys.executable, "-c", "import time; time.sleep(30)"]
    started = time.monotonic()
    state, _, _ = bridge.execute(sleeper, "", root, os.environ, 5, lambda: time.monotonic() - started > 0.4)
    assert state == "cancelled" and time.monotonic() - started < 10


if __name__ == "__main__":
    with tempfile.TemporaryDirectory(dir=Path(__file__).parent) as folder:
        root = Path(folder).resolve()
        test_staging(root)
        test_process(root)
    print("Codex bridge checks passed")
