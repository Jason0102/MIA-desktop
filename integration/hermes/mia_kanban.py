"""Local MIA work-center bridge to the native Hermes Kanban database."""

import json
import os
import re
import sys
import uuid

BOARD = "mia"
ACTOR = "MIA"
OWNER_PATTERN = re.compile(r"mia-[0-9a-f]{32}")


def card(kb, conn, task):
    comments = kb.list_comments(conn, task.id)[-8:]
    events = kb.list_events(conn, task.id)
    handoff = ""
    for event in reversed(events):
        if event.kind in {"review_requested", "blocked", "block_loop_detected"}:
            data = event.payload or {}
            handoff = str(data.get("summary") or data.get("reason") or "")
            if handoff:
                break
    return {
        "id": task.id, "title": task.title, "status": task.status,
        "body": task.body or "", "result": task.result or handoff,
        "parents": kb.parent_ids(conn, task.id),
        "comments": [comment.body for comment in comments],
        "session": task.session_id,
    }


def owned_task(kb, conn, args):
    task = kb.get_task(conn, args["id"])
    if (task is None or task.status != "running"
            or task.claim_lock != args["owner"]
            or task.current_run_id != args["run"]):
        raise ValueError("此輪任務已結束或由其他執行程序接手。")
    return task


def advance(kb, conn, owner):
    if not OWNER_PATTERN.fullmatch(owner):
        raise ValueError("工作中心執行識別碼無效。")
    if any(OWNER_PATTERN.fullmatch(task.claim_lock or "")
           for task in kb.list_tasks(conn, status="running")):
        return None
    kb.recompute_ready(conn)
    for task in kb.list_tasks(conn, status="ready"):
        if task.assignee is not None:
            continue
        claim = kb.claim_task(conn, task.id, ttl_seconds=3600, claimer=owner)
        if claim is not None:
            result = card(kb, conn, claim)
            result["run"] = claim.current_run_id
            result["context"] = kb.build_worker_context(conn, claim.id)
            return result
    return None


def recover(kb, conn, owner):
    if not OWNER_PATTERN.fullmatch(owner):
        raise ValueError("工作中心執行識別碼無效。")
    for task in kb.list_tasks(conn, status="running"):
        if OWNER_PATTERN.fullmatch(task.claim_lock or "") and task.claim_lock != owner:
            kb.block_task(conn, task.id, kind="needs_input",
                          reason="先前執行已中斷。請確認已完成的動作，再解除阻塞。",
                          expected_run_id=task.current_run_id)


def mutate(kb, conn, args):
    action = args["action"]
    if action == "create":
        title = args["title"].strip()
        body = args["body"].strip()
        key = uuid.UUID(args["key"]).hex
        parents = list(dict.fromkeys(args.get("parents", [])))
        if not 1 <= len(title) <= 160 or not 1 <= len(body) <= 8000 or len(parents) > 20:
            raise ValueError("請輸入有效的標題、任務內容與前置卡片。")
        for parent in parents:
            task = kb.get_task(conn, parent)
            if task is None or task.status == "archived":
                raise ValueError(f"找不到前置卡片 {parent}。")
        return kb.create_task(conn, title=title, body=body, parents=parents,
                              created_by=ACTOR, idempotency_key=key,
                              workspace_kind="scratch", session_id=key, board=BOARD)

    task = kb.get_task(conn, args["id"])
    if task is None:
        raise ValueError("找不到這張卡片。")
    note = args.get("note", "").strip()[:16000]
    if action == "review":
        task = owned_task(kb, conn, args)
        # MIA records artifact paths in its result archive. Native Kanban
        # scratch attachments are not needed for MIA's workspace outputs.
        ok = kb.request_review(conn, task.id, summary=note, expected_run_id=task.current_run_id)
    elif action == "fail":
        task = owned_task(kb, conn, args)
        ok = kb.block_task(conn, task.id, reason=note, kind="needs_input",
                           expected_run_id=task.current_run_id)
    elif action == "block" and task.status == "ready":
        ok = kb.block_task(conn, task.id, reason=note or "使用者暫停此卡片", kind="needs_input")
    elif action == "unblock" and task.status == "blocked":
        if note:
            kb.add_comment(conn, task.id, ACTOR, note)
        ok = kb.unblock_task(conn, task.id)
    elif action == "unblock" and task.status == "triage" and note:
        kb.add_comment(conn, task.id, ACTOR, note)
        ok = kb.specify_triage_task(conn, task.id)
    elif action == "approve" and task.status == "review":
        if note:
            kb.add_comment(conn, task.id, ACTOR, note)
        ok = kb.complete_task(conn, task.id, result=task.result or recent_handoff(kb, conn, task.id))
    elif action == "changes" and task.status == "review" and note:
        kb.add_comment(conn, task.id, ACTOR, note)
        ok = kb.reopen_review_task(conn, task.id)
    else:
        raise ValueError("卡片狀態已變更，或缺少退回原因。")
    if not ok:
        raise ValueError("未能更新卡片，請重新整理後查看狀態。")
    return task.id


def recent_handoff(kb, conn, task_id):
    return card(kb, conn, kb.get_task(conn, task_id))["result"]


def run(args):
    from hermes_cli import kanban_db as kb
    from hermes_cli.kanban_db_connect import connect_closing

    action = args["action"]
    if not kb.board_metadata_path(BOARD).exists():
        if action in {"list", "advance", "recover"}:
            return {"cards": [], "claim": None}
        if action != "create":
            raise ValueError("尚未建立米亞看板。")
        kb.create_board(BOARD, name="MIA 工作中心")
    with connect_closing(board=BOARD) as conn:
        changed = None
        if action == "recover":
            recover(kb, conn, args["owner"])
        elif action not in {"list", "advance"}:
            changed = mutate(kb, conn, args)
        claim = advance(kb, conn, args["owner"]) if action == "advance" else None
        tasks = kb.list_tasks(conn, include_archived=False)
        return {"cards": [card(kb, conn, task) for task in tasks], "claim": claim, "changed": changed}


def main():
    sys.path.insert(0, os.environ["MIA_HERMES_SOURCE"])
    os.environ["HERMES_KANBAN_HOME"] = os.environ["HERMES_HOME"]
    os.environ["HERMES_DISABLE_LAZY_INSTALLS"] = "1"
    sys.stdout.reconfigure(encoding="utf-8")
    try:
        request = json.loads(sys.stdin.buffer.read().decode("utf-8"))
        response = run(request)
    except ValueError as error:
        response = {"error": str(error)}
    print(json.dumps(response, ensure_ascii=False))


if __name__ == "__main__":
    main()
