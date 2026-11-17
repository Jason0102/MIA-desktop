"""Check the learning loop, feedback revisions and concurrent-write protection."""
import json
from pathlib import Path
import tempfile
import os
import sys
sys.path.insert(0, str(Path(__file__).resolve().parents[2] / ".mia-runtime/hermes/hermes-agent"))
from mia_learning import context, execute, database


with tempfile.TemporaryDirectory() as folder:
    from hermes_constants import set_hermes_home_override, reset_hermes_home_override
    from mia_learning import location
    root = Path(folder) / "hermes"
    registry = Path(folder) / "users.json"
    registry.write_text(json.dumps({
        "Users": [{"Id": "one"}, {"Id": "other"}],
        "Bindings": [{"Provider": "desktop", "Subject": "local", "UserId": "one"},
                     {"Provider": "discord", "Subject": "123456789012345678", "UserId": "one"},
                     {"Provider": "discord", "Subject": "234567890123456789", "UserId": "other"}]}))
    os.environ["MIA_HERMES_PROFILE_ROOT"] = str(root)
    os.environ["MIA_HERMES_USER_REGISTRY"] = str(registry)
    def resolve(home):
        token = set_hermes_home_override(home)
        try:
            return location()
        finally:
            reset_hermes_home_override(token)
    pc = resolve(root)
    dc = resolve(root / "profiles" / "mia-discord-123456789012345678")
    guest = resolve(root / "profiles" / "mia-discord-234567890123456789")
    assert pc[0] == dc[0] != guest[0] and pc[1] == "PC" and dc[1] == "Discord"
    path = Path(folder) / "one" / "learning.sqlite"
    def turn(request, channel="PC"):
        text = context(path, channel, request)
        return json.loads(text[text.index('{"turn_id"'):])["turn_id"]

    first = turn("Make a document")
    skill = dict(kind="skill", name="document-validation", base_version=0,
                 content="Render output, inspect pages, then deliver.",
                 evidence="Rendering identified a clipped heading.", certainty="observed")
    review = dict(action="review", turn_id=first, summary="Fixed a clipped heading.",
                  outcome="success", updates=[skill], applied=[])
    assert execute(review, path, "PC")["updates"] == 1
    assert execute(review, path, "PC")["status"] == "already_reviewed"
    second = turn("Correct the document", "Discord")
    update = {**skill, "base_version": 1, "content": "Render all pages and inspect tables before delivery."}
    assert execute(dict(action="review", turn_id=second, summary="User requested checking tables.",
                        outcome="corrected", applied=[{"name": skill["name"], "version": 1}],
                        updates=[update]), path, "Discord")["updates"] == 1
    items = execute(dict(action="inspect", kind="skill"), path, "PC")["items"]
    assert items[0]["version"] == 2 and items[0]["channel"] == "Discord"
    stale = turn("Concurrent document")
    try:
        execute({**review, "turn_id": stale}, path, "PC")
        raise AssertionError("Stale update accepted")
    except ValueError:
        pass
    with database(path) as db:
        assert db.execute("SELECT review FROM turns WHERE id=?", (stale,)).fetchone()[0] is None
        assert db.execute("SELECT COUNT(*) FROM revisions").fetchone()[0] == 2
    other = Path(folder) / "other" / "learning.sqlite"
    assert not execute(dict(action="inspect", kind="skill"), other, "Discord")["items"]
    execute(dict(action="forget", kind="skill", name=skill["name"]), path, "PC")
    assert not execute(dict(action="inspect", kind="skill"), path, "PC")["items"]
    print("PASS: task experience, skill use, feedback revision, idempotence, stale-write rollback, isolated storage and forgetting.")
