"""Generate a deliverable using the same ElevenLabs voice as the MIA desktop."""
import json
import os
from pathlib import Path
import subprocess
import uuid

from .bridge import local_c_path


def handle(args, **_kwargs):
    text = args.get("text")
    if not isinstance(text, str) or not text.strip() or len(text.encode("utf-16-le", errors="surrogatepass")) > 10000:
        return json.dumps({"error": "請提供 1 至 5000 字的語音稿。"}, ensure_ascii=False)
    output = None
    try:
        from hermes_constants import get_hermes_home, get_routing_process_hermes_home

        executable = Path(os.environ["MIA_VOICE_EXPORT_EXE"])
        if not executable.is_file():
            return json.dumps({"error": "請先建置並透過正式啟動器啟動米亞。"}, ensure_ascii=False)
        home = get_hermes_home()
        local = home == get_routing_process_hermes_home()
        # Desktop indexes its jobs directory. Keep routed profiles' files separate.
        jobs = (local_c_path(os.environ["MIA_CODEX_WORKSPACE"]) / "jobs" if local
                else local_c_path(str(home)) / "cache" / "mia-voice")
        jobs.mkdir(parents=True, exist_ok=True)
        if jobs.resolve() != jobs:
            raise ValueError("語音成果目錄不能是連結。")
        identifier = uuid.uuid4().hex
        job = jobs / identifier
        job.mkdir()
        output = job / "output"
        output.mkdir()
        result = subprocess.run(
            [str(executable), "--export-voice", identifier],
            input=text.encode("utf-8"), capture_output=True, timeout=130,
            creationflags=subprocess.CREATE_NO_WINDOW,
            env={**os.environ, "MIA_VOICE_EXPORT_ROOT": str(jobs)},
        )
        file = output / "MIA.mp3"
        if result.returncode != 0 or not file.is_file():
            try:
                error = json.loads(result.stdout).get("error")
            except (ValueError, AttributeError):
                error = None
            return json.dumps({"error": error or "米亞語音生成失敗。"}, ensure_ascii=False)
        if local:
            (job / "mia-delivery.json").write_text(
                json.dumps({"Files": ["output/MIA.mp3"]}), encoding="utf-8")
        return json.dumps({
            "status": "generated", "path": str(file),
            "artifacts": [{"path": str(file), "size_bytes": file.stat().st_size}],
            "discord_media": f'MEDIA:"{file.as_posix()}"',
        }, ensure_ascii=False)
    except subprocess.TimeoutExpired:
        return json.dumps({"error": "米亞語音生成逾時。"}, ensure_ascii=False)
    except (OSError, ValueError, KeyError):
        return json.dumps({"error": "無法啟動米亞語音產生器或寫入成果。"}, ensure_ascii=False)
    finally:
        if output is not None:
            try:
                output.rmdir()
                output.parent.rmdir()
            except OSError:
                pass


def registration():
    name = "mia_voice_generate"
    return {"name": name, "toolset": "mia_codex", "handler": handle, "schema": {
        "name": name,
        "description": "Generate an MP3 in MIA's configured voice from a finished script when an audio file is requested. Uses the configured ElevenLabs account. Does not play audio. Include the returned file path in desktop work results, or the returned discord_media as its own line to attach the MP3 on Discord.",
        "parameters": {"type": "object", "properties": {
            "text": {"type": "string", "description": "The exact script to speak.", "minLength": 1, "maxLength": 5000}
        }, "required": ["text"], "additionalProperties": False},
    }}
