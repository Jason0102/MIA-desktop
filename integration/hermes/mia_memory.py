"""Apply an explicit user preference through Hermes, then verify its durable store."""
import json
import os
import sys


def remember(payload):
    from tools.memory_tool import load_on_disk_store, memory_tool
    content = payload.get("content", "").strip()
    old_text = payload.get("old_text", "").strip()
    if not content:
        return {"verified": False}
    store = load_on_disk_store()
    # An identical preference is already durable; do not duplicate it.
    if content not in store.user_entries:
        result = json.loads(memory_tool(action="patch" if old_text else "add", target="user",
                                      content=content, old_text=old_text or None, store=store))
        if not result.get("success") or result.get("staged"):
            return {"verified": False, "reason": result.get("error", result.get("message", "Write failed"))}
    fresh = load_on_disk_store()
    return {"verified": any(content in entry for entry in fresh.user_entries)}


if __name__ == "__main__":
    sys.path.insert(0, os.environ["MIA_HERMES_SOURCE"])
    sys.path.append(os.path.dirname(os.path.abspath(__file__)))
    os.environ["HERMES_DISABLE_LAZY_INSTALLS"] = "1"
    sys.stdin.reconfigure(encoding="utf-8")
    sys.stdout.reconfigure(encoding="utf-8")
    try:
        from mia_shared_memory import install_from_environment
        install_from_environment()
        result = remember(json.load(sys.stdin))
    except Exception:
        result = {"verified": False}
    print(json.dumps(result, ensure_ascii=False))
