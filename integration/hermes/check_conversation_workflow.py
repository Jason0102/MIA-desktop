"""Exercise conversational authorization, persistent templates and actual card DB."""
import importlib.util
import json
import os
from pathlib import Path
import subprocess
import sys
import tempfile
import types
import uuid

HERE = Path(__file__).resolve().parent
package = types.ModuleType("mia_test_plugin")
package.__path__ = [str(HERE / "mia-codex")]
sys.modules[package.__name__] = package
from mia_test_plugin import work_cards, templates, bridge

with tempfile.TemporaryDirectory(dir=HERE) as temporary:
    root = Path(temporary).resolve()
    profile = root / "state/hermes"
    profile.mkdir(parents=True)
    main_session = uuid.uuid4().hex
    (root / "hermes-session.txt").write_text(main_session, encoding="utf-8")
    os.environ.update(MIA_CODEX_WORKSPACE=str(root), MIA_DATA_DIR=str(root),
                      HERMES_HOME=str(profile), MIA_HERMES_EXECUTABLE=sys.executable,
                      MIA_HERMES_KANBAN_ENTRY=str(HERE / "mia_kanban.py"),
                      MIA_HERMES_SOURCE=str(HERE.parents[1] / ".mia-runtime/hermes/hermes-agent"))

    def card(action, session=main_session, **fields):
        return json.loads(work_cards.handle(dict(action=action, **fields), session_id=session))

    assert "error" in card("create", session="worker", title="Unauthorised", body="No")
    key = uuid.uuid4().hex
    first = card("create", title="Draft", body="Prepare fixture", key=key)["changed"]
    assert card("create", title="Draft", body="Prepare fixture", key=key)["changed"] == first
    second = card("create", title="Publish draft", body="Next step", parents=[first])["changed"]
    assert len(card("list")["cards"]) == 2
    assert "error" in card("approve", session="worker", id=first)
    assert "error" in card("approve", id=first) # Must be in review.
    owner = "mia-" + uuid.uuid4().hex

    def worker(action, **fields):
        result = subprocess.run([sys.executable, "-I", "-B", str(HERE / "mia_kanban.py")],
                                input=json.dumps(dict(action=action, owner=owner, **fields)),
                                text=True, encoding="utf-8", capture_output=True, check=True, timeout=25)
        return json.loads(result.stdout)

    claim = worker("advance")["claim"]
    assert claim["id"] == first
    worker("review", id=first, run=claim["run"], note="Draft produced")
    assert worker("advance")["claim"] is None
    assert "error" not in card("changes", id=first, note="Add sources")
    revised = worker("advance")["claim"]
    assert "Add sources" in revised["context"]
    worker("review", id=first, run=revised["run"], note="Revised with sources")
    assert "error" not in card("approve", id=first)
    assert worker("advance")["claim"]["id"] == second

    source = root / "reusable.custom"
    source.write_bytes(b"fixture template")
    saved = json.loads(templates.handle({"action": "save", "path": str(source), "name": "Test", "description": "Reusable style"}))
    source.unlink()
    listed = json.loads(templates.handle({"action": "list"}))["templates"]
    assert listed[0]["path"] == saved["path"] and Path(saved["path"]).read_bytes() == b"fixture template"
    collision = root / "template.json"
    collision.write_text("original template data", encoding="utf-8")
    other = json.loads(templates.handle({"action": "save", "path": str(collision), "name": "JSON template"}))
    assert Path(other["path"]).read_text(encoding="utf-8") == "original template data"
    def runner(command, prompt, job, *_):
        assert "Reusable style" in prompt and saved["id"] in prompt
        Path(command[command.index("--output-last-message") + 1]).write_text("done", encoding="utf-8")
        return "completed", 0, ""
    env = dict(os.environ, MIA_CODEX_EXECUTABLE=sys.executable)
    assert bridge.run_task({"task": "Use my style"}, env, lambda: False, runner)["status"] == "completed"
    large = root / "large.custom"
    with large.open("wb") as file:
        file.truncate(bridge.MAX_INPUT_BYTES + 1)
    large_job = root / "large-job"
    large_job.mkdir()
    assert bridge.stage_inputs(root, large_job, [bridge.c_path_syntax(str(large))]) == [str(large)]
    assert not (large_job / "input").exists()
print("PASS: main-conversation-only mutations, review/return dependency gates, persisted template discovery in new jobs.")
