"""Offline checks for sender routing and durable profile isolation."""
import json
import os
from pathlib import Path
import subprocess
import sys
import tempfile

HERE = Path(__file__).resolve().parent
SOURCE = HERE.parents[1] / ".mia-runtime/hermes/hermes-agent"
sys.path.insert(0, str(HERE))
sys.path.insert(0, str(SOURCE))
import hermes_yaml as yaml
from mia_discord import bind, prepare, install_model_credentials
from gateway.profile_routing import parse_profile_routes, match_profile_route


with tempfile.TemporaryDirectory(dir=HERE.parents[1] / ".mia-runtime") as scratch:
    root = Path(scratch)
    registry = root / "users.json"
    registry.write_text((HERE.parents[1] / "src/Mia/users.json").read_text(encoding="utf-8"), encoding="utf-8")
    (root / "config.yaml").write_text("model: {provider: openai-api, default: test}\n", encoding="utf-8")
    for bad in ("98179", "yuris_98179", "../other"):
        try:
            bind(registry, bad, "local-jason")
        except ValueError:
            pass
        else:
            raise AssertionError("Accepted an invalid account ID")
    jason, guest = "123456789012345678", "234567890123456789"
    bind(registry, jason, "local-jason")
    bind(registry, guest, "guest", "Guest")
    try:
        bind(registry, jason, "guest")
    except ValueError:
        pass
    else:
        raise AssertionError("Reassigned existing account")
    prepare(root, registry)
    routes = parse_profile_routes(yaml.safe_load((root / "config.yaml").read_text())["profile_routes"])
    for subject in (jason, guest):
        route = match_profile_route(routes, "discord", user_id=subject)
        assert route.profile == "mia-discord-" + subject
    assert match_profile_route(routes, "discord", user_id="345678901234567890") is None
    assert match_profile_route(routes, "telegram", user_id=jason) is None

    # Fresh processes exercise the actual native on-disk memory store.
    def memory(profile, operation):
        code = "import sys; sys.path.insert(0, " + repr(str(SOURCE)) + "); " + operation
        result = subprocess.run([sys.executable, "-I", "-B", "-c", code],
                                capture_output=True, text=True, encoding="utf-8", timeout=30,
                                env=dict(os.environ, HERMES_HOME=str(profile), HERMES_DISABLE_LAZY_INSTALLS="1"))
        assert result.returncode == 0, result.stdout + result.stderr
        return result.stdout
    homes = [root, root / "profiles" / ("mia-discord-" + jason),
             root / "profiles" / ("mia-discord-" + guest)]
    for index, home in enumerate(homes):
        memory(home, f"from tools.memory_tool import memory_tool, load_on_disk_store; import json; "
                     f"r=json.loads(memory_tool(action='add',target='user',content='source-marker-{index}',store=load_on_disk_store())); "
                     "assert r['success'], r")
    prepare(root, registry)  # Restart must retain memories.
    for index, home in enumerate(homes):
        found = memory(home, "from tools.memory_tool import load_on_disk_store; print(load_on_disk_store().user_entries)")
        assert f"source-marker-{index}" in found
        assert all(f"source-marker-{other}" not in found for other in range(3) if other != index)
    import agent.secret_scope as secret_scope
    os.environ['OPENAI_API_KEY'] = 'test-model-key'
    os.environ['DISCORD_BOT_TOKEN'] = 'test-bot-secret'
    install_model_credentials(root, registry)
    assert secret_scope.build_profile_secret_scope(homes[1])['OPENAI_API_KEY'] == 'test-model-key'
    assert 'DISCORD_BOT_TOKEN' not in secret_scope.build_profile_secret_scope(homes[1])
    assert 'OPENAI_API_KEY' not in secret_scope.build_profile_secret_scope(root / 'profiles' / 'unknown')
    saved = json.loads(registry.read_text(encoding='utf-8'))
    saved['Bindings'] = [b for b in saved['Bindings'] if b['Subject'] != jason]
    registry.write_text(json.dumps(saved), encoding='utf-8')
    assert 'OPENAI_API_KEY' not in secret_scope.build_profile_secret_scope(homes[1])
    for home in homes[1:]:
        config = yaml.safe_load((home / "config.yaml").read_text())
        assert "mia-codex" in config["plugins"]["disabled"]
        assert config["thread_sessions_per_user"]
        assert config["mcp_servers"] == {}
        assert json.loads((home / "identity.json").read_text(encoding="utf-8"))["provider"] == "discord"
print("PASS: identity binding, sender routing, separate desktop/Discord/user memory, restart persistence.")
