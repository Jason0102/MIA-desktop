"""Versioned local templates discoverable across independent jobs."""
import json
import os
from pathlib import Path
import shutil
import uuid

from .bridge import local_c_path


def catalog(root):
    entries = []
    for manifest in sorted((root / "templates").glob("*/template.json")):
        if manifest.resolve() != manifest:
            continue
        try:
            data = json.loads(manifest.read_text(encoding="utf-8"))
            asset = manifest.parent / data["file"]
            if asset.parent == manifest.parent and asset.resolve() == asset and asset.is_file():
                entries.append({"id": manifest.parent.name, "name": data["name"],
                                "description": data["description"], "path": str(asset)})
        except (OSError, ValueError, KeyError, TypeError):
            continue
    return entries


def handle(args, **_kwargs):
    try:
        root = local_c_path(os.environ["MIA_CODEX_WORKSPACE"])
        templates = root / "templates"
        templates.mkdir(exist_ok=True)
        if templates.resolve() != templates:
            raise ValueError("Template directory must not be redirected.")
        if args.get("action") == "list":
            return json.dumps({"templates": catalog(root)}, ensure_ascii=False)
        if args.get("action") != "save":
            raise ValueError("Use list or save.")
        source = local_c_path(args["path"])
        name, description = args.get("name", "").strip(), args.get("description", "").strip()
        if not source.is_file() or not 1 <= len(name) <= 160 or len(description) > 2000:
            raise ValueError("Supply a file and a short template name/description.")
        identifier = uuid.uuid4().hex
        destination = templates / identifier
        destination.mkdir()
        asset_name = "asset-" + source.name
        shutil.copy2(source, destination / asset_name)
        manifest = {"name": name, "description": description, "file": asset_name}
        (destination / "template.json").write_text(json.dumps(manifest, ensure_ascii=False), encoding="utf-8")
        return json.dumps({"status": "saved", "id": identifier,
                           "path": str(destination / asset_name)}, ensure_ascii=False)
    except (OSError, ValueError, KeyError, TypeError) as error:
        return json.dumps({"error": str(error)}, ensure_ascii=False)


def registration():
    name = "mia_templates"
    return {"name": name, "toolset": "mia_codex", "handler": handle, "schema": {
        "name": name, "description": "List or save a reusable long-term template. Save only when the user requests reuse. Each save creates a new version without overwriting. List before creating related documents; pass a matching template path to mia_codex_run inputs. The catalog is also supplied to each Codex job.",
        "parameters": {"type": "object", "properties": {
            "action": {"type": "string", "enum": ["list", "save"]},
            "path": {"type": "string"}, "name": {"type": "string"}, "description": {"type": "string"}
        }, "required": ["action"], "additionalProperties": False}}}
