"""Read the same interaction signals as the desktop when choosing Discord text tone."""

from collections import Counter, defaultdict
from datetime import datetime, timedelta, timezone
from functools import lru_cache
import json
import math
from pathlib import Path


@lru_cache(maxsize=1)
def rules():
    path = Path(__file__).resolve().parents[2] / "src" / "Mia" / "interaction-mood.json"
    return json.loads(path.read_text(encoding="utf-8"))


def calculate(events, now):
    config = rules()
    values = list(config["baseline"])
    signals = config["signals"]
    reasons = defaultdict(float)
    used = defaultdict(float)
    recent = []
    for event in events:
        try:
            kind = event["Kind"]
            at = datetime.fromisoformat(event["At"])
            if at.tzinfo is None or not timedelta(0) <= now - at <= timedelta(days=config["windowDays"]):
                continue
            if kind not in signals or (kind == "task_result" and event.get("Status") not in ("completed", "succeeded", "success")):
                continue
            intensity = max(0, min(2, event.get("Seconds", 0) / 60)) if kind == "ball_active" else 1
            if math.isfinite(intensity) and intensity > 0:
                recent.append((at, kind, intensity))
        except (KeyError, TypeError, ValueError, OverflowError):
            continue

    for at, kind, intensity in sorted(recent, key=lambda event: event[0], reverse=True):
        signal = signals[kind]
        key = kind, at.astimezone().date()
        if used[key] >= signal["dailyCap"]:
            continue
        weight = min(signal["dailyCap"] - used[key], intensity / (1 + used[key]))
        used[key] += weight
        hours = (now - at).total_seconds() / 3600
        immediate = weight * .5 ** (hours / config["recentHalfLifeHours"])
        affinity = weight * .5 ** (hours / config["affinityHalfLifeHours"])
        effects = signal["effects"]
        for dimension, effect in enumerate(effects):
            values[dimension] += effect * (affinity if dimension == 2 else immediate)
        reasons[signal["label"]] += immediate * (effects[0] + effects[1] + effects[3]) + affinity * effects[2] * .3

    state = next((rule["label"] for rule in config["states"] if values[rule["dimension"]] >= rule["threshold"]),
                 config["defaultState"])
    strongest = [label for label, score in sorted(reasons.items(), key=lambda pair: (-pair[1], pair[0])) if score >= .5][:3]
    return [round(max(0, min(100, value)), 1) for value in values], state, strongest or [config["defaultReason"]]


def context(state_directory, identity_hash, request=""):
    try:
        from mia_interaction import preferences_hashed, read_hashed, topic_labels
        now = datetime.now(timezone.utc)
        events = read_hashed(state_directory, identity_hash)
        values, state, reasons = calculate(events, now)
        preferences = preferences_hashed(state_directory, identity_hash)
    except (OSError, ValueError, ImportError, KeyError, TypeError):
        return ""
    pleasantness, energy, affinity, playfulness = values
    selected = [f"{name}「{preferences[name]}」" for name, allowed in
                (("回答長度", ("簡短", "平衡", "詳細")), ("互動風格", ("安靜", "適中", "活潑")))
                if preferences.get(name) in allowed]
    preference_text = "\n使用者已設定的偏好優先於米亞的預設表達：" + "、".join(selected) + "。" if selected else ""
    relevant = set(topic_labels(request)) - {"其他閒聊"} if isinstance(request, str) else set()
    matches = Counter()
    if relevant:
        for item in events:
            if item.get("Kind") == "chat" and timedelta(0) <= now - datetime.fromisoformat(item["At"]) <= timedelta(days=30):
                matches.update(topic for topic in item.get("Topics") or [] if isinstance(topic, str) and topic in relevant)
    topic_text = ("\n本次相關的近 30 天話題分類：「" + "、".join(topic for topic, _ in matches.most_common(2)) +
                  "」。分類只供語氣與話題參考，不能據此編造曾經聊過的細節。") if matches else ""
    return (f"\n米亞的互動心情快照（0–100）：{state}。愉悅度 {pleasantness:.0f}、活力 {energy:.0f}、"
            f"親近感 {affinity:.0f}、玩心 {playfulness:.0f}。最近影響心情的互動：{'、'.join(reasons)}。"
            "這是米亞的狀態。依當下的聊天內容取捨，嚴肅或需要說明時維持清楚、平穩的文字表達。" + topic_text + preference_text)
