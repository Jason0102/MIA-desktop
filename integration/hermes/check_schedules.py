"""Exercise MIA reminders through Hermes' native job store, runner and delivery."""

import asyncio
import json
import os
from pathlib import Path
import shutil
import sys
import tempfile

root = Path(__file__).resolve().parents[2]
runtime = root / ".mia-runtime"
sys.path.insert(0, str(runtime / "hermes" / "hermes-agent"))

with tempfile.TemporaryDirectory(prefix="mia-schedule-check-", dir=runtime) as temp:
    workspace = Path(temp)
    home = workspace / "state" / "hermes"
    plugin = home / "plugins" / "mia-schedules-check"
    plugin.mkdir(parents=True)
    (plugin / "plugin.yaml").write_text(
        'name: mia-schedules-check\nversion: "1.0"\ndescription: Local cron diagnostic\n'
        'provides_tools:\n  - mia_schedule_manage\n',
        encoding="utf-8")
    (plugin / "__init__.py").write_text(
        "from .scheduled_tasks import register_scheduled_tasks as register\n", encoding="utf-8")
    shutil.copyfile(root / "integration/hermes/mia-codex/scheduled_tasks.py", plugin / "scheduled_tasks.py")
    (home / "config.yaml").write_text("plugins:\n  enabled: [mia-schedules-check]\n", encoding="utf-8")
    os.environ.update(
        HERMES_HOME=str(home), HERMES_DISABLE_LAZY_INSTALLS="1", MIA_CODEX_WORKSPACE=str(workspace),
        MIA_CRON_DELIVERY_ENABLED="true", MIA_CRON_HOME_CHANNEL="desktop")

    from cron import jobs
    from cron.scheduler import tick
    from gateway.config import Platform, load_gateway_config
    from gateway.platform_registry import platform_registry
    from hermes_cli.plugins import discover_plugins
    from tools.registry import registry

    discover_plugins()
    platform = Platform("miacron")
    config = load_gateway_config()
    assert config.platforms[platform].enabled
    entry = platform_registry.get("miacron")
    adapter = entry.adapter_factory(config.platforms[platform])

    def act(action, **params):
        return json.loads(registry.dispatch("mia_schedule_manage", {"action": action, **params}))

    reminder = '喝水與休息：\'引號\'\n第二行'
    assert "error" in act("create", kind="reminder", name="舊排程", text=reminder,
                          schedule="2001-01-01T09:00:00+08:00")
    assert "error" in act("create", kind="reminder", name="缺時區", text=reminder,
                          schedule="2099-01-01T09:00:00")
    row = act("create", kind="reminder", name="休息", text="原訊息", schedule="every 30m")["job"]
    job_id = row["id"]
    assert act("list")["jobs"][0]["id"] == job_id
    assert act("pause", id=job_id)["job"]["state"] == "paused"
    assert act("resume", id=job_id)["job"]["state"] == "scheduled"
    assert act("update", id=job_id, text=reminder)["job"]["id"] == job_id
    assert act("run", id=job_id)["job"]["id"] == job_id
    assert jobs.get_job(job_id)["no_agent"], "A literal reminder must not make a model request."
    assert jobs.get_job(job_id)["deliver"] == "miacron:desktop"
    assert "error" in act("delete", id=jobs.create_job(prompt="unmanaged", schedule="1h")["id"])

    async def fire():
        assert await adapter.connect()
        assert not (await adapter.send("elsewhere", reminder, metadata={"job_id": job_id})).success
        loop = asyncio.get_running_loop()
        try:
            await asyncio.to_thread(tick, False, {platform: adapter}, loop)
        finally:
            await adapter.disconnect()

    asyncio.run(fire())
    assert jobs.get_job(job_id)["last_status"] == "ok", jobs.get_job(job_id)
    files = list((workspace / "state/scheduled-notices/pending").glob("*.json"))
    assert len(files) == 1, files
    notice = json.loads(files[0].read_text(encoding="utf-8"))
    assert notice["title"] == "休息" and notice["kind"] == "reminder"
    assert reminder in notice["text"], notice
    assert act("delete", id=job_id)["deleted"] == job_id
    assert act("list")["jobs"] == []
    task = act("create", kind="task", name="每週摘要", text="整理公開公告",
               schedule="0 9 * * MON", timezone="Asia/Taipei")["job"]
    record = jobs.get_job(task["id"])
    assert task["timezone"] == "Asia/Taipei" and not record["no_agent"]
    assert "no_mcp" in record["enabled_toolsets"] and "terminal" not in record["enabled_toolsets"]

print("PASS: native Hermes schedule CRUD, script-only dispatch and durable MIA delivery")
