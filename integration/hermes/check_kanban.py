"""Verify durable MIA Kanban behavior against the installed Hermes database."""

import json
import os
from pathlib import Path
import subprocess
import sys
import tempfile
import uuid


HERE = Path(__file__).resolve().parent
RUNTIME = HERE.parents[1] / ".mia-runtime"


def check(profile):
    owner = "mia-" + uuid.uuid4().hex
    next_owner = "mia-" + uuid.uuid4().hex
    env = dict(os.environ, HERMES_HOME=profile,
               MIA_HERMES_SOURCE=str(RUNTIME / "hermes/hermes-agent"))

    def call(action, **fields):
        result = subprocess.run([sys.executable, "-I", "-B", str(HERE / "mia_kanban.py")],
                                input=json.dumps(dict(action=action, **fields)), encoding="utf-8",
                                env=env, capture_output=True, check=True, timeout=20)
        return json.loads(result.stdout)

    def status(response, task_id):
        return next(item for item in response["cards"] if item["id"] == task_id)["status"]

    assert call("list")["cards"] == []
    card_key = uuid.uuid4().hex
    first = call("create", title="First", body="Prepare the brief", key=card_key)["changed"]
    duplicate = call("create", title="First", body="Prepare the brief", key=card_key)
    assert duplicate["changed"] == first and len(duplicate["cards"]) == 1
    dependent = call("create", title="Second", body="Summarize the brief", key=uuid.uuid4().hex,
                     parents=[first])
    second = dependent["changed"]
    assert status(dependent, second) == "todo"

    run = call("advance", owner=owner)["claim"]
    assert run["id"] == first and run["session"] == card_key and "Prepare the brief" in run["context"]
    assert call("advance", owner=owner)["claim"] is None
    assert call("advance", owner=next_owner)["claim"] is None
    assert "error" in call("review", id=first, owner=next_owner, run=run["run"], note="Invalid owner")
    review = call("review", id=first, owner=owner, run=run["run"], note="First draft")
    assert status(review, first) == "review" and status(review, second) == "todo"
    returned = call("changes", id=first, note="Clarify the supporting numbers")
    assert status(returned, first) == "ready"
    retry = call("advance", owner=owner)["claim"]
    assert retry["id"] == first and retry["run"] != run["run"] and retry["session"] == card_key
    assert "Clarify the supporting numbers" in retry["context"]
    assert "error" in call("review", id=first, owner=owner, run=run["run"], note="Stale worker")
    call("review", id=first, owner=owner, run=retry["run"], note="Final brief with supporting numbers")
    approved = call("approve", id=first)
    assert status(approved, first) == "done"
    resumed = call("advance", owner=owner)["claim"]
    assert resumed["id"] == second and "Final brief with supporting numbers" in resumed["context"]

    after_restart = call("recover", owner=next_owner)
    assert status(after_restart, second) == "blocked"
    assert call("advance", owner=next_owner)["claim"] is None
    assert status(call("unblock", id=second, note="Verified prior actions"), second) == "ready"
    resumed = call("advance", owner=next_owner)["claim"]
    assert resumed["id"] == second and "Verified prior actions" in resumed["context"]
    blocked = call("fail", id=second, owner=next_owner, run=resumed["run"], note="Unable to access input")
    assert status(blocked, second) == "triage"
    assert "error" in call("unblock", id=second)
    assert status(call("unblock", id=second, note="Added the missing input"), second) == "ready"


if __name__ == "__main__":
    with tempfile.TemporaryDirectory(prefix="mia-kanban-check-", dir=RUNTIME) as temporary:
        check(temporary)
    print("PASS: persisted cards, dependency gates, duplicate prevention, review/return, handoff, block and restart.")
