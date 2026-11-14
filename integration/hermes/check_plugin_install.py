"""Run the MIA extension check in an isolated Hermes home, without a model."""

import importlib.util
import json
from pathlib import Path
import tempfile


folder = Path(__file__).resolve().parent
runtime = folder.parents[1] / ".mia-runtime"
source = runtime / "hermes" / "hermes-agent"
if not source.is_dir():
    raise SystemExit("Install Hermes into .mia-runtime first.")

spec = importlib.util.spec_from_file_location("mia_extensions", folder / "mia-codex" / "extensions.py")
extensions = importlib.util.module_from_spec(spec)
spec.loader.exec_module(extensions)

check_home = Path(tempfile.mkdtemp(prefix="extension-check-", dir=runtime)).resolve()
result = extensions.install_diagnostic(source, check_home, log_dir=check_home / "logs")
print(json.dumps(result, ensure_ascii=False, indent=2))
raise SystemExit(0 if result.get("status") == "completed" else 1)
