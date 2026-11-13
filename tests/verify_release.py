"""Check a public release archive without requiring any API credentials."""
import hashlib
from pathlib import Path
import sys
import zipfile

archive = Path(sys.argv[1])
forbidden = {"api_key", "voice-profile.json", "settings.json", "conversation.jsonl"}
with zipfile.ZipFile(archive) as package:
    assert package.testzip() is None, "Archive integrity failure"
    for entry in package.infolist():
        path = Path(entry.filename)
        assert path.name.lower() not in forbidden, entry.filename
        assert not any(part.startswith(".test") for part in path.parts), entry.filename
        if path.name == "api_key.example":
            import configparser
            config = configparser.ConfigParser()
            config.read_string(package.read(entry).decode("utf-8-sig"))
            assert all(not value for section in config.sections() for value in config[section].values())
print("PASS: archive integrity and excluded private filenames")
print("SHA256:", hashlib.sha256(archive.read_bytes()).hexdigest())
