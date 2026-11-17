"""Check that the Windows desktop and Discord runtime share one interaction store."""

from datetime import datetime, timezone
import json
from pathlib import Path
import subprocess
import sys
import tempfile

from mia_interaction import InteractionMemory


def check(command):
    with tempfile.TemporaryDirectory(prefix="mia-interaction-interop-") as folder:
        state = Path(folder)
        identity = "interop-測試"
        discord = InteractionMemory(state, identity)
        another_user = InteractionMemory(state, "other-user")

        def desktop(operation):
            return subprocess.check_output(
                [*command, "--interaction-interop", str(state), identity, operation],
                encoding="utf-8", timeout=20,
            )

        def event(identifier):
            return {"Id": identifier, "At": datetime.now(timezone.utc).isoformat(),
                    "Channel": "Discord", "Kind": "chat", "Topics": ["混合話題", "遠端測試"]}

        generation = discord.generation()
        assert generation, "Discord could not initialize its store"
        desktop("record")
        assert [item["Id"] for item in discord.read()] == ["windows-fixture"]
        discord.record(event("discord-fixture"), generation)
        discord.record(event("discord-fixture"), generation)
        another_user.record(event("other-fixture"), another_user.generation())
        shared = json.loads(desktop("read"))
        assert [(item["Id"], item["Channel"]) for item in shared] == [
            ("windows-fixture", "PC"), ("discord-fixture", "Discord"),
        ], shared

        desktop("exclude")
        discord.record(event("remote-after-exclusion"), generation)
        assert all("混合話題" not in item["Topics"] for item in discord.read())
        assert all("混合話題" not in item["Topics"] for item in json.loads(desktop("read")))

        desktop("pause")
        discord.record(event("while-paused"), generation)
        assert "while-paused" not in {item["Id"] for item in discord.read()}
        desktop("resume")

        generation, enabled = discord.begin("live-turn")
        assert enabled
        try:
            desktop("reset")
        except subprocess.CalledProcessError as error:
            assert error.returncode == 3 and discord.generation() == generation
        else:
            raise AssertionError("Desktop reset must wait for an active Discord turn")
        discord.end("live-turn", generation)
        desktop("reset")
        assert discord.generation() != generation
        discord.record(event("old-in-flight-turn"), generation)
        assert json.loads(desktop("read")) == []
        discord.record(event("new-turn"), discord.generation())
        assert [item["Id"] for item in json.loads(desktop("read"))] == ["new-turn"]
        assert [item["Id"] for item in another_user.read()] == ["other-fixture"]


if __name__ == "__main__":
    if len(sys.argv) < 2:
        raise SystemExit("Pass the built Mia.Checks executable or dotnet plus its DLL.")
    check(sys.argv[1:])
    print("PASS: PC/Discord exchange, deduplication, topic deletion, pause, reset fencing and identity isolation.")
