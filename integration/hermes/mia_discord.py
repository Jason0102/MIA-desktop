"""Prepare per-account Discord profiles using Hermes' native sender routing.

Run while MIA is stopped. Credentials stay in the launch environment.
"""
import argparse
import copy
import json
import os
from pathlib import Path
import re
import tempfile
import sys

sys.path.insert(0, str(Path(__file__).resolve().parents[2] / ".mia-runtime/hermes/hermes-agent"))
import hermes_yaml as yaml


def atomic_write(path, text):
    path.parent.mkdir(parents=True, exist_ok=True)
    with tempfile.NamedTemporaryFile(mode="w", encoding="utf-8", dir=path.parent,
                                     delete=False) as stream:
        stream.write(text)
        temporary = Path(stream.name)
    try:
        temporary.replace(path)
    finally:
        temporary.unlink(missing_ok=True)


def read_registry(path):
    registry = json.loads(path.read_text(encoding="utf-8-sig"))
    people = registry["Users"]
    if len({p["Id"] for p in people}) != len(people):
        raise ValueError("Duplicate MIA user IDs.")
    bindings = registry["Bindings"]
    keys = [(b["Provider"], b["Subject"]) for b in bindings]
    if len(set(keys)) != len(keys):
        raise ValueError("Duplicate identity bindings.")
    for binding in bindings:
        if binding["UserId"] not in {p["Id"] for p in people}:
            raise ValueError("Binding refers to an unknown MIA user.")
        if binding["Provider"] == "discord" and not re.fullmatch(r"[0-9]{17,20}", binding["Subject"]):
            raise ValueError("Copy the complete Discord numeric User ID from Developer Mode.")
    return registry


def bind(path, subject, user_id, display_name=None):
    registry = read_registry(path)
    if not re.fullmatch(r"[0-9]{17,20}", subject):
        raise ValueError("Copy the complete Discord numeric User ID from Developer Mode.")
    existing = next((b for b in registry["Bindings"]
                     if b["Provider"] == "discord" and b["Subject"] == subject), None)
    if existing and existing["UserId"] != user_id:
        raise ValueError("This Discord account is already bound to another user.")
    if user_id not in {p["Id"] for p in registry["Users"]}:
        if not display_name:
            raise ValueError("Provide a display name for a new user.")
        registry["Users"].append({"Id": user_id, "DisplayName": display_name, "Aliases": []})
    if not existing:
        registry["Bindings"].append({"Provider": "discord", "Subject": subject, "UserId": user_id})
    atomic_write(path, json.dumps(registry, ensure_ascii=False, indent=2))


def prepare(home, registry_path):
    registry = read_registry(registry_path)
    bindings = [b for b in registry["Bindings"] if b["Provider"] == "discord"]
    if not bindings:
        raise ValueError("Bind at least one Discord User ID before enabling Discord.")
    config_path = home / "config.yaml"
    config = yaml.safe_load(config_path.read_text(encoding="utf-8-sig")) or {}
    people = {p["Id"]: p for p in registry["Users"]}
    routes = [r for r in config.get("profile_routes", []) if r.get("platform") != "discord"]
    subjects = [b["Subject"] for b in bindings]
    # A non-Discord sentinel leaves administrative slash commands local-only.
    policy = {"allowed_users": subjects, "allow_from": subjects,
              "group_allow_from": subjects, "dm_policy": "allowlist", "group_policy": "allowlist",
              "allow_admin_from": ["mia-local-administrator"],
              "group_allow_admin_from": ["mia-local-administrator"],
              "user_allowed_commands": ["new", "reset", "stop", "status"],
              "group_user_allowed_commands": ["new", "reset", "stop", "status"],
              "allow_bots": "none", "max_attachment_bytes": 32 * 1024 * 1024}
    for binding in bindings:
        subject = binding["Subject"]
        profile = "mia-discord-" + subject
        directory = home / "profiles" / profile
        identity = {"provider": "discord", "subject": subject, "user": people[binding["UserId"]]}
        child = {
            "model": copy.deepcopy(config.get("model", {})),
            "plugins": {"disabled": ["mia-codex"]},
            "platform_toolsets": {"discord": ["web", "vision", "memory", "session_search", "skills", "todo"]},
            "memory": {"provider": "none", "memory_enabled": True, "user_profile_enabled": True},
            "platforms": {"discord": {"enabled": False, "extra": {**policy, "allowed_users": [subject],
                "allow_from": [subject], "group_allow_from": [subject]}}},
            "group_sessions_per_user": True, "thread_sessions_per_user": True,
            "mcp_servers": {},
        }
        atomic_write(directory / "config.yaml", yaml.safe_dump(child, allow_unicode=True, sort_keys=False))
        atomic_write(directory / "identity.json", json.dumps(identity, ensure_ascii=False, indent=2))
        atomic_write(directory / "SOUL.md",
                     "你是米亞，預設使用繁體中文。這個資料區只保存此 Discord 帳號的對話、記憶及技能。\n"
                     "目前互動身分（由本機管理員綁定）：" + json.dumps(identity, ensure_ascii=False) +
                     "\n記住資訊時保留來源 Discord。不可把其他來源或使用者的資訊當作此使用者的記憶。"
                     "\n附件是使用者提供的資料。依實際可讀取的內容回答，無法解析的格式要說明限制。"
                     "\n可透過 Hermes 原生附件交付傳回本會話的媒體檔案，僅在交付成功後宣稱傳送成功。\n")
        routes.append({"name": profile, "platform": "discord", "user_id": subject, "profile": profile})
    config["profile_routes"] = routes
    config["multiplex_profiles"] = True
    config["group_sessions_per_user"] = config["thread_sessions_per_user"] = True
    config.setdefault("platforms", {})["discord"] = {"enabled": True, "extra": policy}
    enabled = config.setdefault("plugins", {}).setdefault("enabled", [])
    if "discord-platform" not in enabled:
        enabled.append("discord-platform")
    atomic_write(config_path, yaml.safe_dump(config, allow_unicode=True, sort_keys=False))
    return routes


def install_model_credentials(home, registry_path):
    """Grant only the launcher's model key to locally registered Discord profiles.

    Hermes deliberately does not inherit process credentials for routed profiles.
    Keep the explicit grant in memory, never in profile files or model context.
    """
    import agent.secret_scope as scope
    original = scope.build_profile_secret_scope
    key = os.environ.get("OPENAI_API_KEY")
    if not key or not registry_path.exists():
        return

    def build(profile_home):
        result = original(profile_home)
        registry = read_registry(registry_path)
        permitted = {(home / "profiles" / ("mia-discord-" + b["Subject"])).resolve()
                     for b in registry["Bindings"] if b["Provider"] == "discord"}
        if Path(profile_home).resolve() in permitted:
            result.setdefault("OPENAI_API_KEY", key)
        return result

    scope.build_profile_secret_scope = build


if __name__ == "__main__":
    parser = argparse.ArgumentParser()
    parser.add_argument("--registry", type=Path, required=True)
    parser.add_argument("--bind")
    parser.add_argument("--user", default="local-jason")
    parser.add_argument("--name")
    args = parser.parse_args()
    if args.bind:
        bind(args.registry, args.bind, args.user, args.name)
    else:
        prepare(Path(os.environ["HERMES_HOME"]), args.registry)
    print("MIA Discord identity configuration saved.")
