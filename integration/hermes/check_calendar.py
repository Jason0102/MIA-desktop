"""Exercise the calendar's real stdio MCP endpoint in a temporary workspace."""

import asyncio
from contextlib import closing
import json
import sqlite3
from pathlib import Path
import sys
import tempfile

from mcp import ClientSession, StdioServerParameters
from mcp.client.stdio import stdio_client


WINDOW = {"window_start": "2026-11-15T00:00:00+08:00", "window_end": "2026-11-21T00:00:00+08:00"}


def event(title, start, end, **fields):
    return dict(title=title, start=f"2026-11-16T{start}:00", end=f"2026-11-16T{end}:00",
                timezone="Asia/Taipei", **fields)


async def call(session, action, error=False, **params):
    answer = await session.call_tool("mia_calendar", dict(action=action, **params))
    result = json.loads(answer.content[0].text)
    assert bool(answer.is_error) == error, result
    return result


async def verify(workspace):
    server = StdioServerParameters(command=sys.executable, args=[
        "-I", "-B", str(Path(__file__).with_name("mia_calendar_mcp.py").resolve())],
        env={"MIA_CODEX_WORKSPACE": str(workspace)})
    async with stdio_client(server) as (read, write), ClientSession(read, write, read_timeout_seconds=10) as session:
        await session.initialize()
        tools = await session.list_tools()
        assert [tool.name for tool in tools.tools] == ["mia_calendar"]
        first = event("上午會議", "09:00", "10:00", location="台北車站")
        notes = "攜帶報告與筆電。\n會議連結 https://example.org/meeting\n  第二行保留縮排。"
        first["description"] = notes
        origin = (await call(session, "create", event=first, request_key="first"))["event"]
        assert origin["description"] == notes
        assert "description" in tools.tools[0].input_schema["properties"]["event"]["properties"]
        retry = await call(session, "create", event=first, request_key="first")
        assert retry["event"]["id"] == origin["id"]
        await call(session, "create", event=event("不佔用", "10:05", "12:30", busy=False), request_key="free")
        await call(session, "create", event=event("下午行程", "11:30", "12:00", location="國父紀念館"), request_key="after")
        await call(session, "create", event={"title": "全天", "timezone": "Asia/Taipei",
                   "start": "2026-11-17", "end": "2026-11-18", "all_day": True}, request_key="day")
        overlap = await call(session, "preview", event=event("重疊", "09:30", "10:30"), **WINDOW)
        assert overlap["items"][0]["overlap_count"] == 1
        visit = await call(session, "preview", event=event("面談", "10:00", "11:00"), **WINDOW)
        assert visit["items"][0]["overlap_count"] == 0
        assert visit["items"][0]["previous"]["id"] == origin["id"]
        assert visit["items"][0]["previous"]["description"] == notes
        assert visit["items"][0]["following"]["title"] == "下午行程"
        expected = await call(session, "assess_trip", previous_end="2026-11-16T10:00:00+08:00",
                              next_start="2026-11-16T10:30:00+08:00", travel_minutes=25,
                              prep_minutes=4, buffer_minutes=6)
        assert expected["status"] == "conflict" and expected["slack_minutes"] == -5
        after = await call(session, "assess_trip", previous_end="2026-11-16T11:00:00+08:00",
                           next_start="2026-11-16T11:30:00+08:00", arrival="2026-11-16T11:20:00+08:00",
                           buffer_minutes=10)
        assert after["status"] == "fits" and after["slack_minutes"] == 0
        repeated = event("回診", "16:00", "17:00", description="攜帶健保卡", recurrence={"frequency": "daily", "count": 3})
        series = (await call(session, "create", event=repeated, request_key="series"))["event"]
        moved = await call(session, "update", event_id=series["id"], original_start="2026-11-17T16:00:00+08:00",
                           revision=0, event={"start": "2026-11-17T17:00:00", "end": "2026-11-17T18:00:00"})
        assert moved["event"]["series_id"] == series["id"]
        assert moved["event"]["description"] == "攜帶健保卡"
        await call(session, "cancel", event_id=series["id"], original_start="2026-11-18T16:00:00+08:00", revision=0)
        updated = await call(session, "update", event_id=origin["id"], revision=1, event={"title": "已更新"})
        assert updated["event"]["description"] == notes
        await call(session, "update", error=True, event_id=origin["id"], revision=2, event={"description": None})
        await call(session, "update", error=True, event_id=origin["id"], revision=2, event={"description": "x" * 10001})
        await call(session, "update", error=True, event_id=origin["id"], revision=1, event={"title": "舊版"})
        dst = {"title": "時鐘切換", "timezone": "America/New_York", "start": "2026-03-07T09:00:00",
               "end": "2026-03-07T10:00:00", "recurrence": {"frequency": "daily", "count": 2}}
        preview = await call(session, "preview", event=dst, window_start="2026-03-07T00:00:00-05:00",
                             window_end="2026-03-10T00:00:00-04:00")
        assert [item["candidate"]["start"] for item in preview["items"]] == [
            "2026-03-07T09:00:00-05:00", "2026-03-08T09:00:00-04:00"]
    async with stdio_client(server) as (read, write), ClientSession(read, write, read_timeout_seconds=10) as session:
        await session.initialize()
        items = (await call(session, "list", **WINDOW))["items"]
        repeats = [item["start"] for item in items if item["title"] == "回診"]
        assert repeats == ["2026-11-16T16:00:00+08:00", "2026-11-17T17:00:00+08:00"], repeats
        assert any(item["title"] == "已更新" for item in items)
        assert next(item for item in items if item["id"] == origin["id"])["description"] == notes
        changed = await call(session, "update", event_id=origin["id"], revision=2, event={"description": "改帶修訂版"})
        assert changed["event"]["description"] == "改帶修訂版"
        cleared = await call(session, "update", event_id=origin["id"], revision=3, event={"description": ""})
        assert cleared["event"]["description"] == ""

    # Existing databases contain event bodies without the new field.
    with closing(sqlite3.connect(workspace / "state/mia/calendar.sqlite3")) as db, db:
        body = json.loads(db.execute("SELECT body FROM events WHERE id = ?", (origin["id"],)).fetchone()[0])
        body.pop("description")
        db.execute("UPDATE events SET body = ? WHERE id = ?", (json.dumps(body), origin["id"]))
    async with stdio_client(server) as (read, write), ClientSession(read, write, read_timeout_seconds=10) as session:
        await session.initialize()
        legacy = await call(session, "get", event_id=origin["id"])
        assert legacy["event"]["description"] == ""
        updated = await call(session, "update", event_id=origin["id"], revision=4, event={"description": "舊活動的新備註"})
        assert updated["event"]["description"] == "舊活動的新備註"


if __name__ == "__main__":
    with tempfile.TemporaryDirectory(prefix="mia-calendar-check-",
                                     dir=Path(__file__).resolve().parents[2] / ".mia-runtime") as scratch:
        asyncio.run(verify(Path(scratch)))
    print("PASS: calendar MCP discovery, notes read/update/clear/persistence/legacy compatibility, recurrence/instance edits, DST, overlap and trip arithmetic")
