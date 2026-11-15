"""Exercise pinned Hermes persistence and the complete task archive in a disposable profile."""
import importlib.util
import json
import os
from pathlib import Path
import subprocess
import sys
import tempfile

here = Path(__file__).parent
source = here.parents[1] / ".mia-runtime/hermes/hermes-agent"
sys.path.insert(0, str(source))

def load(name, path):
    spec = importlib.util.spec_from_file_location(name, path)
    module = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(module)
    return module

history = load("mia_history_check", here / "mia-codex/history.py")
scratch = (here.parents[1] / ".mia-runtime").resolve()
with tempfile.TemporaryDirectory(prefix="mia-memory-check-", dir=scratch) as temporary:
    root = Path(temporary)
    assert root.resolve().is_relative_to(scratch)
    os.environ["HERMES_HOME"] = str(root / "state/hermes")
    os.environ["MIA_HERMES_SOURCE"] = str(source)
    os.environ["MIA_CODEX_WORKSPACE"] = str(root)
    os.environ["HERMES_DISABLE_LAZY_INSTALLS"] = "1"
    Path(os.environ["HERMES_HOME"]).mkdir(parents=True)

    def write(content, old_text=""):
        # Every operation is a NEW process, just like the desktop helper.
        proc = subprocess.run([sys.executable, "-I", "-B", str(here / "mia_memory.py")],
            input=json.dumps({"content": content, "old_text": old_text}),
            text=True, encoding="utf-8", capture_output=True, timeout=60)
        assert proc.returncode == 0, proc.stderr
        return json.loads(proc.stdout)["verified"]

    assert write("使用者偏好繁體中文。")
    profile = Path(os.environ["HERMES_HOME"]) / "memories/USER.md"
    before = profile.read_text(encoding="utf-8")
    assert "使用者偏好繁體中文。" in before
    assert write("使用者偏好繁體中文。")
    assert profile.read_text(encoding="utf-8") == before, "duplicate write changed the profile"
    assert write("使用者偏好精簡的繁體中文。", "使用者偏好繁體中文。")
    assert "使用者偏好精簡的繁體中文。" in profile.read_text(encoding="utf-8")
    assert not write("changed", "nonexistent preference"), "failed patch must not be confirmed"
    assert not write("x" * 5000), "capacity failure must not be confirmed"
    from tools.memory_tool import load_on_disk_store
    assert "使用者偏好精簡的繁體中文。" in load_on_disk_store().user_entries

    from hermes_state import SessionDB
    from tools.session_search_tool import session_search
    db = SessionDB()
    try:
        db.create_session("old-chat", "api_server")
        db.append_message("old-chat", "user", "ArchiveProbe: 上次提案採用藍色封面。")
        db.append_message("old-chat", "assistant", "已確認封面配色。")
        found = session_search(query="ArchiveProbe", db=db, current_session_id="new-chat")
        assert "old-chat" in found and "藍色封面" in found
        assert "藍色封面" in session_search(session_id="old-chat", db=db)
        phrases = json.loads(history.search_conversations({"query": "藍色封面"}, current_session_id="new-chat"))
        assert phrases["messages"][0]["session_id"] == "old-chat"
        assert "藍色封面" in phrases["messages"][0]["text"]
        assert not json.loads(history.search_conversations({"query": "藍色封面"}, current_session_id="old-chat"))["messages"]
        db.append_message("old-chat", "user", "請回想我們以前討論的封面。")
        assert json.loads(history.search_conversations({"query": "藍色封面"}, current_session_id="old-chat"))["messages"]
        assert json.loads(history.search_conversations({"session_id": "old-chat"}))["messages"]
    finally:
        db.close()

    index = root / "state/mia/task-results.json"
    index.parent.mkdir(parents=True)
    tasks = [{"Id": f"{i:032x}", "Title": f"歷史任務 {i}", "Request": f"請修改提案 {i}",
              "Reply": f"原上下文 {i}", "Files": [f"jobs/{i:032x}/output/提案.txt"],
              "UpdatedAt": f"2026-11-15T00:{i:02d}:00Z"} for i in range(75)]
    index.write_text(json.dumps(tasks, ensure_ascii=False), encoding="utf-8")
    result = json.loads(history.search_tasks({"id": tasks[0]["Id"]}))
    assert result["tasks"][0]["Reply"] == "原上下文 0"
    assert any(e["Id"] == tasks[0]["Id"] for e in json.loads(history.search_tasks({"query": "提案 0"}))["tasks"])
    ids, offset = [], 0
    while offset is not None:
        page = json.loads(history.search_tasks({"offset": offset}))
        ids.extend(e["Id"] for e in page["tasks"])
        offset = page["next_offset"]
    assert len(set(ids)) == 75, "archive pagination dropped older tasks"
    assert not json.loads(history.search_tasks({"query": "不存在的成果"}))["tasks"]
print("PASS: durable preference add/update/dedup/reload/failures; conversation search/read; 75-task lookup and pagination")
