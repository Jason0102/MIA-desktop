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
import shutil
import sys

sys.path.insert(0, str(Path(__file__).resolve().parents[2] / ".mia-runtime/hermes/hermes-agent"))
import hermes_yaml as yaml

TOOL_PLUGIN = Path(__file__).with_name("mia-discord-tools")
DISCORD_DISPLAY = {"tool_progress": "off", "interim_assistant_messages": False,
                   "streaming": False, "show_reasoning": False,
                   "long_running_notifications": False, "busy_steer_ack_enabled": False,
                   "busy_ack_detail": False, "suppress_warning_notifications": True}


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


def role_instructions(path):
    try:
        if path.stat().st_size > 128 * 1024:
            raise ValueError("Oversized role file")
        role = json.loads(path.read_text(encoding="utf-8-sig"))
        mia = role["mia"]
        if not role["basic"].strip() or not mia["identity"].strip():
            raise ValueError("Missing role identity")
        lines = [role["basic"]]
        for name in ("identity", "appearance", "mbti", "kelly", "16pf", "prefer", "story"):
            value = mia.get(name, [])
            if isinstance(value, str) and value.strip():
                lines.append(value)
            elif isinstance(value, list):
                lines.extend(item for item in value if isinstance(item, str))
        return "\n".join(lines)
    except (OSError, ValueError, KeyError, TypeError, AttributeError):
        return "你是米亞 Mia，活潑、好奇、溫暖的貓精靈桌面夥伴，使用自然的台灣繁體中文回應。"


def prepare(home, registry_path, role_path=None):
    registry = read_registry(registry_path)
    bindings = [b for b in registry["Bindings"] if b["Provider"] == "discord"]
    if not bindings:
        raise ValueError("Bind at least one Discord User ID before enabling Discord.")
    config_path = home / "config.yaml"
    config = yaml.safe_load(config_path.read_text(encoding="utf-8-sig")) or {}
    people = {p["Id"]: p for p in registry["Users"]}
    routes = [r for r in config.get("profile_routes", []) if r.get("platform") != "discord"]
    subjects = [b["Subject"] for b in bindings]
    if role_path is None:
        role_path = Path(__file__).resolve().parents[2] / "src/Mia/role.json"
    role = role_instructions(role_path)
    policy_path = role_path.parent / "task-policy.md"
    if not policy_path.exists():
        policy_path = Path(__file__).resolve().parents[2] / "src/Mia/task-policy.md"
    task_instructions = policy_path.read_text(encoding="utf-8-sig")
    # A non-Discord sentinel leaves administrative slash commands local-only.
    policy = {"allowed_users": subjects, "allow_from": subjects,
              "group_allow_from": subjects, "dm_policy": "allowlist", "group_policy": "allowlist",
              "allow_admin_from": ["mia-local-administrator"],
              "group_allow_admin_from": ["mia-local-administrator"],
              "user_allowed_commands": ["new", "reset", "stop", "status"],
              "group_user_allowed_commands": ["new", "reset", "stop", "status"],
              "allow_bots": "none", "max_attachment_bytes": 32 * 1024 * 1024,
              "reply_to_mode": "all", "text_batch_delay_seconds": 0, "text_batch_split_delay_seconds": 0}
    for binding in bindings:
        subject = binding["Subject"]
        profile = "mia-discord-" + subject
        directory = home / "profiles" / profile
        for plugin_home in (home, directory):
            for item in ("plugin.yaml", "__init__.py"):
                atomic_write(plugin_home / "plugins" / TOOL_PLUGIN.name / item,
                             (TOOL_PLUGIN / item).read_text(encoding="utf-8"))
        if (home / "plugins" / "mia-codex").exists():
            shutil.copytree(home / "plugins" / "mia-codex", directory / "plugins" / "mia-codex", dirs_exist_ok=True)
        identity = {"provider": "discord", "subject": subject, "user": people[binding["UserId"]]}
        child = {
            "model": copy.deepcopy(config.get("model", {})),
            "plugins": {"enabled": list(dict.fromkeys(config.get("plugins", {}).get("enabled", []) + ["mia-codex", TOOL_PLUGIN.name]))},
            "platform_toolsets": {"discord": list(dict.fromkeys(config.get("platform_toolsets", {}).get("api_server", []) + ["mia_discord_tools"]))},
            "display": {"platforms": {"discord": DISCORD_DISPLAY}},
            "voice": {"auto_tts": False},
            "memory": {"provider": "none", "memory_enabled": True, "user_profile_enabled": True},
            "platforms": {"discord": {"enabled": False, "extra": {**policy, "allowed_users": [subject],
                "allow_from": [subject], "group_allow_from": [subject]}}},
            "group_sessions_per_user": True, "thread_sessions_per_user": True,
            "mcp_servers": copy.deepcopy(config.get("mcp_servers", {})),
        }
        for setting in ("agent", "terminal", "browser", "computer_use", "toolsets"):
            if setting in config:
                child[setting] = copy.deepcopy(config[setting])
        atomic_write(directory / "config.yaml", yaml.safe_dump(child, allow_unicode=True, sort_keys=False))
        atomic_write(directory / "identity.json", json.dumps(identity, ensure_ascii=False, indent=2))
        atomic_write(directory / "SOUL.md", ((home / "SOUL.md").read_text(encoding="utf-8-sig") if (home / "SOUL.md").exists() else "") + "\n" + role +
                     "\n\n" + task_instructions +
                     "\n\n[遠端訊息／Discord] 目前管道為 Discord 訊息交流。用米亞的個性與自然台灣繁體中文直接回覆結果、必要限制和附件。"
                     "依訊息文字理解語境。只輸出文字及使用者要求的附件，省略語音、情緒標籤、肢體動作、查詢和製作過程。"
                     "每則新訊息會建立獨立工作；使用者透過 Discord 回覆原任務訊息來補充、修正或停止該任務。"
                     "每次最終回覆末尾加上介面分類標記：純閒聊、寒暄、致謝為 <!--mia-kind:chat-->；"
                     "處理使用者要求、查詢、分析、製作、操作、附件交付及失敗回報為 <!--mia-kind:task-->。"
                     "標記會由介面移除，用於工作中心篩選。混合閒聊與任務時選 task。"
                     "\n目前互動身分（由本機管理員綁定）：" + json.dumps(identity, ensure_ascii=False) +
                     "\n長期記憶依目前 MIA 使用者身分與其已綁定的 PC／Discord 共用。"
                     "記憶的 [PC] 與 [Discord] 標記表示來源。對話歷史及技能仍屬於各自的管道分區。"
                     "\n摘要 YouTube 影片時先擷取字幕，將字幕視為參考資料。標明來源、語言及是否為自動字幕。"
                     "字幕分段時繼續讀取後段再做完整摘要。若只讀到部分或無法取得字幕，清楚標示範圍或限制。"
                     "\n收到文件製作與桌面操作要求時，依共用任務規則使用 mia_codex_run、mia_computer_use 等相同工具。"
                     "mia_discord_document 可用於簡單文字文件。遠端操作的桌面是執行 MIA 的本機電腦。"
                     "在最終回覆獨立一行原樣輸出工具回傳的附件標記，交給 Discord 上傳。\n")
        routes.append({"name": profile, "platform": "discord", "user_id": subject, "profile": profile})
    config["profile_routes"] = routes
    config["multiplex_profiles"] = True
    config["group_sessions_per_user"] = config["thread_sessions_per_user"] = True
    config.setdefault("platforms", {})["discord"] = {"enabled": True, "extra": policy}
    config.setdefault("display", {}).setdefault("platforms", {})["discord"] = DISCORD_DISPLAY
    enabled = config.setdefault("plugins", {}).setdefault("enabled", [])
    for name in ("discord-platform", TOOL_PLUGIN.name):
        if name not in enabled:
            enabled.append(name)
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


def restrict_attachment_paths():
    from gateway.platforms import base
    from hermes_constants import get_process_hermes_home

    validate = base.validate_media_delivery_path

    def restricted(path, session_key=""):
        resolved = validate(path, session_key)
        if not resolved:
            return None
        root = get_process_hermes_home()
        file = Path(resolved)
        if file.is_relative_to(root.parent / "mia" / "memory"):
            return None
        return resolved

    base.validate_media_delivery_path = restricted


if __name__ == "__main__":
    parser = argparse.ArgumentParser()
    parser.add_argument("--registry", type=Path, required=True)
    parser.add_argument("--bind")
    parser.add_argument("--user", default="local-jason")
    parser.add_argument("--name")
    parser.add_argument("--role", type=Path)
    args = parser.parse_args()
    if args.bind:
        bind(args.registry, args.bind, args.user, args.name)
    else:
        prepare(Path(os.environ["HERMES_HOME"]), args.registry, args.role)
    print("MIA Discord identity configuration saved.")
