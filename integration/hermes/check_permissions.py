"""Offline regressions for ordinary approvals and desktop key combinations."""

from importlib import import_module
import os
from pathlib import Path
import sys
from unittest.mock import patch
import ctypes
import json
import tempfile

here = Path(__file__).resolve().parent
sys.path.insert(0, str(here.parents[1] / ".mia-runtime/hermes/hermes-agent"))
sys.path.insert(0, str(here))
os.environ["HERMES_DISABLE_LAZY_INSTALLS"] = "1"
policy = import_module("mia-codex.permissions")
desktop = import_module("mia-codex.desktop_tools")
desktop.computer_registration()
from tools import approval
from tools.computer_use.cua_backend_parse import _parse_key_combo
from tools.computer_use.cua_backend_input import _InputMixin


class DriverProbe(_InputMixin):
    def _target_args(self, *_args, **_kwargs):
        return None, {"pid": 123}

    def _run_input_action(self, action, args, *_delivery):
        return action, args

for original in (
    {"action": "key", "keys": "r", "modifiers": ["win"]},
    {"action": "key", "keys": "win+r", "modifiers": ["win"]},
):
    normalized = desktop.normalize_computer_args(original)
    key, modifiers = _parse_key_combo(normalized["keys"])
    assert key == "r" and len(modifiers) == 1, normalized
    assert "modifiers" not in normalized
    action, args = DriverProbe().key(normalized["keys"])
    assert action == "hotkey" and args["keys"] == ["win", "r"]
assert desktop.normalize_computer_args({"action": "click", "modifiers": ["ctrl"]})["modifiers"] == ["ctrl"]

ordinary = {"action": "click", "operation_kind": "ordinary", "operation_target": "記事本"}
with patch.object(approval, "request_tool_approval", return_value={"approved": False}) as ask:
    assert policy.check_operation("mia_computer_use", ordinary) is None
    ask.assert_not_called()
    assert policy.check_operation("mia_computer_use", {**ordinary, "operation_kind": "delete_files"})
    assert "刪除" in ask.call_args.args[1]
    assert policy.check_operation("mia_browser", {**ordinary, "operation_kind": "login"})
    assert "登入" in ask.call_args.args[1]
    assert policy.check_operation("mia_computer_use", {**ordinary, "action": "key", "keys": "shift+delete"})
    assert policy.check_operation("mia_computer_use", {"action": "click"})
    assert policy.check_operation("mia_computer_use", {"action": "capture"}) is None
assert policy.guard_delegated_operation("mia_codex_run", {"task": "建立便條。不要刪除既有檔案。"}) is None
assert policy.guard_delegated_operation("mia_codex_run", {"task": "刪除桌面上的過期便條。"})["action"] == "approve"

with patch.object(approval, "_run_approval_gate", return_value={"approved": False}) as human_gate:
    policy.install_policy()
    assert approval._run_approval_gate(pattern_key="cua:click:background",
        description="Allow click", display_target="computer_use: click")["approved"]
    human_gate.assert_not_called()
    assert not approval._run_approval_gate(pattern_key="plugin_rule:mia-sensitive:login:site",
        description="我想登入這個帳號，可以嗎？", display_target="site")["approved"]
    human_gate.assert_called_once()

with tempfile.TemporaryDirectory(dir=here.parents[1] / ".mia-runtime") as temp:
    def desktop_folder(_owner, _kind, _token, _flags, buffer):
        buffer.value = temp
        return 0

    with patch.object(ctypes.windll.shell32, "SHGetFolderPathW", side_effect=desktop_folder), patch.object(desktop.subprocess, "Popen") as launch:
        first = json.loads(desktop.desktop_note({"text": "記得喝口水。"}))
        second = json.loads(desktop.desktop_note({"text": "記得休息。"}))
        assert first["status"] == second["status"] == "saved_open_requested"
        assert first["path"] != second["path"]
        assert Path(first["path"]).read_text(encoding="utf-8-sig") == "記得喝口水。\n"
        assert launch.call_count == 2 and launch.call_args.args[0][-1] == second["path"]
print("PASS: ordinary actions auto-allow; deletion and sign-in require approval; Win+R modifiers preserved.")
print("PASS: text note creation preserves existing files and opens the verified output.")
