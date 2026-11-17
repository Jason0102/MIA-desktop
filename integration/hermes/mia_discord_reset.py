"""Clear Discord history bound to the desktop identity; leave delivered files in place."""
import json
import os
from pathlib import Path
import sqlite3


def reset_discord_memory():
    root_setting = os.environ.get("MIA_HERMES_PROFILE_ROOT")
    registry_setting = os.environ.get("MIA_HERMES_USER_REGISTRY")
    if not root_setting or not registry_setting:
        return
    from hermes_state import SessionDB
    from mia_discord import atomic_write, read_registry
    from mia_shared_memory import FILES

    root = Path(root_setting)
    registry = read_registry(Path(registry_setting))
    desktop = next((b for b in registry["Bindings"]
                    if b["Provider"] == "desktop" and b["Subject"] == "local"), None)
    if not desktop:
        return
    owner = desktop["UserId"]
    subjects = [b["Subject"] for b in registry["Bindings"]
                if b["Provider"] == "discord" and b["UserId"] == owner]

    for subject in subjects:
        profile = root / "profiles" / ("mia-discord-" + subject)
        state = profile / "state.db"
        if state.is_file():
            db = SessionDB(db_path=state)
            try:
                while True:
                    rows = db.list_sessions_rich(source="discord", limit=200, include_children=True,
                                                 include_archived=True, include_hidden=True, include_subagents=True)
                    if not rows:
                        break
                    skipped = []
                    removed = db.delete_sessions([row["id"] for row in rows], sessions_dir=profile / "sessions",
                                                 exclude_active_write_guards=True, skipped_ids=skipped,
                                                 include_compression_chain=True)
                    if skipped or removed == 0:
                        raise RuntimeError("A Discord session is still in use")
            finally:
                db.close()
        for name in FILES:
            (profile / "memories" / name).unlink(missing_ok=True)

    state = root.parent / "mia"
    routes = state / "discord-tasks.sqlite"
    if subjects and routes.is_file():
        with sqlite3.connect(routes) as db:
            db.executemany("DELETE FROM routes WHERE user=?", [(subject,) for subject in subjects])

    for path in (state / "discord-work").glob("*.json"):
        data = json.loads(path.read_text(encoding="utf-8-sig"))
        if data.get("UserId") != owner:
            continue
        if not data.get("Files"):
            path.unlink()
        else:
            data.update(Request="", Reply="")
            atomic_write(path, json.dumps(data, ensure_ascii=False))

    for name in FILES:
        (root / "memories" / name).unlink(missing_ok=True)
