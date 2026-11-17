"""Share Hermes built-in memory by MIA identity, keeping sessions profile-local."""

from hashlib import sha256
from pathlib import Path
import os


FILES = ("MEMORY.md", "USER.md")


def install(root, registry_path, *, migrate=False):
    from hermes_constants import get_hermes_home
    from mia_discord import atomic_write, read_registry
    from tools import memory_tool
    from tools.memory_tool_store import ENTRY_DELIMITER, MemoryStore

    root = root.resolve()
    bindings = read_registry(registry_path)["Bindings"]
    homes = {}
    for binding in bindings:
        provider, subject = binding["Provider"], binding["Subject"]
        if provider == "desktop" and subject == "local":
            profile, channel = root, "PC"
        elif provider == "discord":
            profile, channel = root / "profiles" / f"mia-discord-{subject}", "Discord"
        else:
            continue
        identity_key = sha256(binding["UserId"].encode("utf-8")).hexdigest()
        homes[profile] = (root.parent / "mia" / "memory" / identity_key, channel)

    def current():
        return homes.get(get_hermes_home().resolve())

    original_dir = memory_tool.get_memory_dir

    def memory_dir():
        binding = current()
        return binding[0] if binding else original_dir()

    memory_tool.get_memory_dir = memory_dir

    def with_source(text, channel):
        text = text.strip()
        if not text or text.startswith(("[PC] ", "[Discord] ")):
            return text
        return f"[{channel}] {text}"

    if migrate:
        for profile, (target, channel) in homes.items():
            legacy = profile / "memories"
            marker = legacy / ".mia-identity-memory-migrated"
            if not legacy.is_dir() or marker.exists():
                continue
            target.mkdir(parents=True, exist_ok=True)
            for name in FILES:
                source, destination = legacy / name, target / name
                if not source.is_file():
                    continue
                with MemoryStore._file_lock(destination):
                    entries = destination.read_text(encoding="utf-8").split(ENTRY_DELIMITER) if destination.exists() else []
                    for entry in source.read_text(encoding="utf-8-sig").split(ENTRY_DELIMITER):
                        entry = with_source(entry, channel)
                        if entry and entry not in entries:
                            entries.append(entry)
                    atomic_write(destination, ENTRY_DELIMITER.join(entries))
            atomic_write(marker, "Migrated to MIA identity memory.\n")

    original_add = MemoryStore.add
    original_replace = MemoryStore.replace
    original_batch = MemoryStore.apply_batch

    def stamp(text):
        binding = current()
        return with_source(text, binding[1]) if binding and isinstance(text, str) else text

    def add(store, target, content):
        return original_add(store, target, stamp(content))

    def replace(store, target, old_text, new_content, matched_entry=None):
        return original_replace(store, target, old_text, stamp(new_content), matched_entry)

    def batch(store, target, operations):
        return original_batch(store, target, [
            {**op, "content": stamp(op.get("content"))}
            if isinstance(op, dict) and op.get("action") in ("add", "replace") else op for op in operations
        ])

    MemoryStore.add = add
    MemoryStore.replace = replace
    MemoryStore.apply_batch = batch

    return current


def refresh_prompts(current):
    from agent import conversation_loop
    from agent.system_prompt import invalidate_system_prompt
    from hermes_constants import get_hermes_home

    build_context = conversation_loop.build_turn_context
    matches_runtime = conversation_loop._stored_prompt_matches_runtime

    def build(agent, *args, **kwargs):
        binding = current()
        if binding and agent._memory_store:
            home, _channel = binding
            from mia_learning import context
            learning = context(home / "learning.sqlite", _channel, args[0] if args else "")
            digest = sha256()
            digest.update(learning.encode("utf-8"))
            for path in [*(home / name for name in FILES), get_hermes_home() / "SOUL.md",
                         Path(__file__), Path(__file__).with_name("mia_discord.py")]:
                try:
                    digest.update(path.read_bytes())
                except FileNotFoundError:
                    digest.update(b"\0")
                digest.update(b"\0")
            # Hermes also restores prompt snapshots from the session DB. Mark the
            # shared memory version so existing sessions adopt new writes on their next turn.
            revision = f"[MIA memory revision {digest.hexdigest()}]"
            if revision not in (agent._cached_system_prompt or ""):
                invalidate_system_prompt(agent)
            agent._mia_memory_revision = revision
            positional = list(args)
            positional[1] = (positional[1] or "") + "\n" + revision + learning
            args = tuple(positional)
        return build_context(agent, *args, **kwargs)

    def matches(agent, stored_prompt):
        revision = getattr(agent, "_mia_memory_revision", None)
        return (not revision or revision in stored_prompt) and matches_runtime(agent, stored_prompt)

    conversation_loop.build_turn_context = build
    conversation_loop._stored_prompt_matches_runtime = matches


def install_from_environment(*, migrate=False):
    root = os.environ.get("MIA_HERMES_PROFILE_ROOT")
    registry = os.environ.get("MIA_HERMES_USER_REGISTRY")
    if root and registry:
        return install(Path(root), Path(registry), migrate=migrate)


def reset_current():
    from tools.memory_tool import get_memory_dir, MemoryStore

    for name in FILES:
        path = get_memory_dir() / name
        if path.parent.is_dir():
            with MemoryStore._file_lock(path):
                path.unlink(missing_ok=True)
    path = get_memory_dir() / "learning.sqlite"
    if path.exists():
        from mia_learning import database
        with database(path) as db:
            db.execute("DELETE FROM turns")
            db.execute("DELETE FROM revisions")
