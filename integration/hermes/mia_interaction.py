"""Cross-channel interaction records, using the same files and byte lock as the desktop."""
from contextlib import contextmanager
from datetime import datetime, timedelta, timezone
import hashlib
import json
import math
import os
from pathlib import Path
import re
import tempfile
import threading
import time
import uuid


_CATEGORIES = (
    ("生活日常", "今天|昨天|最近|心情|生活|起床|睡覺"),
    ("飲食", "午餐|晚餐|早餐|咖啡|飲料|食物|餐廳|美食"),
    ("電影與戲劇", "電影|影集|電視劇|動畫片|劇情"),
    ("音樂", "音樂|歌曲|歌手|演唱會|樂團"),
    ("閱讀", "閱讀|小說|讀書|書籍|漫畫"),
    ("遊戲", "遊戲|電玩|桌遊|玩球"),
    ("旅行", "旅行|旅遊|度假|出國"),
    ("科技", "科技|人工智慧|模型|程式|電腦"),
    ("寵物", "桌寵|貓咪|狗狗|寵物"),
    ("天氣", "天氣|雨天|下雨|晴天|颱風"),
)
_GATE = threading.RLock()


def digest(value):
    return hashlib.sha256(value.encode("utf-8")).hexdigest()


def now():
    return datetime.now(timezone.utc)


def topic_labels(text):
    return [name for name, pattern in _CATEGORIES if re.search(pattern, text)] or ["其他閒聊"]


def is_category(name):
    return name == "其他閒聊" or any(name == category for category, _ in _CATEGORIES)


def has_chat_lead(text):
    return bool(re.search(r"^(?:(?:米亞|MIA)[，！、,:!\s]+)?(?:你好|早安|午安|晚安|謝謝|嗨|哈囉|今天[我你])[^。！？!?\n]{0,80}[。！？!?\n]", text, re.I))


def looks_like_task(text):
    return bool(re.search(r"請(?:你|幫|先|將|把|查|寫|列|做|執行)|幫我|替我|可否(?:查|寫|做)|(?:開啟|生成|建立|整理|搜尋|查詢|設計|實作|推送|更新|修改|上傳|下載).+", text))


def feedback(text):
    normalized = text.strip().rstrip("。！! ～")
    if re.fullmatch("我(?:很)?喜歡這樣|這樣很好|做得好|做得很棒|這個語氣我喜歡", normalized):
        return "positive"
    if re.fullmatch("請安靜|請安靜一下|現在不要打擾我|不用再邀請我|不要再問了", normalized):
        return "quiet"
    if re.fullmatch("不是這樣|請換個語氣|請修正這個回答", normalized):
        return "correction"
    return None


def _read(path, fallback=None):
    try:
        return json.loads(path.read_text(encoding="utf-8-sig"))
    except (FileNotFoundError, ValueError):
        return fallback


def _write(path, value):
    with tempfile.NamedTemporaryFile(mode="w", encoding="utf-8", dir=path.parent, delete=False) as stream:
        temporary = Path(stream.name)
        try:
            json.dump(value, stream, ensure_ascii=False, allow_nan=False)
        except BaseException:
            stream.close()
            temporary.unlink(missing_ok=True)
            raise
    try:
        temporary.replace(path)
    finally:
        temporary.unlink(missing_ok=True)


def _alive(pid):
    if not isinstance(pid, int) or pid <= 0:
        return False
    if os.name == "nt":
        import ctypes
        from ctypes import wintypes
        kernel = ctypes.WinDLL("kernel32", use_last_error=True)
        kernel.OpenProcess.argtypes = (wintypes.DWORD, wintypes.BOOL, wintypes.DWORD)
        kernel.OpenProcess.restype = wintypes.HANDLE
        kernel.CloseHandle.argtypes = (wintypes.HANDLE,)
        kernel.WaitForSingleObject.argtypes = (wintypes.HANDLE, wintypes.DWORD)
        kernel.WaitForSingleObject.restype = wintypes.DWORD
        handle = kernel.OpenProcess(0x00100000, False, pid)
        if not handle:
            return ctypes.get_last_error() == 5
        try:
            return kernel.WaitForSingleObject(handle, 0) == 258
        finally:
            kernel.CloseHandle(handle)
    try:
        os.kill(pid, 0)
        return True
    except ProcessLookupError:
        return False
    except PermissionError:
        return True


class ResetInProgress(RuntimeError):
    pass


class StaleInteraction(RuntimeError):
    pass


class InteractionMemory:
    def __init__(self, state, identity, *, hashed=False):
        hashed_identity = identity if hashed else digest(identity)
        if not re.fullmatch(r"[0-9a-f]{64}", hashed_identity):
            raise ValueError("Invalid identity hash.")
        self.root = Path(state) / "interaction-memory" / hashed_identity

    @contextmanager
    def _locked(self):
        with _GATE:
            self.root.mkdir(parents=True, exist_ok=True)
            with (self.root / ".lock").open("a+b") as lock:
                if os.name == "nt":
                    import msvcrt
                    def acquire():
                        lock.seek(0)
                        msvcrt.locking(lock.fileno(), msvcrt.LK_NBLCK, 1)
                    def release():
                        lock.seek(0)
                        msvcrt.locking(lock.fileno(), msvcrt.LK_UNLCK, 1)
                else:
                    import fcntl
                    def acquire():
                        fcntl.lockf(lock, fcntl.LOCK_EX | fcntl.LOCK_NB, 1, 0)
                    def release():
                        fcntl.lockf(lock, fcntl.LOCK_UN, 1, 0)
                for attempt in range(40):
                    try:
                        acquire()
                        break
                    except OSError:
                        if attempt == 39:
                            raise
                        time.sleep(0.01)
                try:
                    yield
                finally:
                    release()

    def _current(self):
        marker = self.root / "generation.json"
        generation = _read(marker)
        if generation is None and not marker.exists():
            generation = uuid.uuid4().hex
            (self.root / generation / "events").mkdir(parents=True)
            _write(marker, generation)
        if not isinstance(generation, str) or not re.fullmatch(r"[0-9a-f]{32}", generation):
            raise ValueError("Invalid interaction memory generation.")
        return generation

    def generation(self):
        with self._locked():
            return self._current()

    def begin(self, event_id, received_at=None):
        """Pin a Discord turn against memory reset until Hermes finishes writing it."""
        with self._locked():
            if self.reset_in_progress():
                raise ResetInProgress()
            cutoff = _read(self.root / "reset-cutoff.json")
            if cutoff and received_at and received_at < datetime.fromisoformat(cutoff):
                raise StaleInteraction()
            generation = self._current()
            enabled = self._policy()["Enabled"]
            _write(self.root / generation / ("active-" + digest(event_id) + ".json"), os.getpid())
            return generation, enabled

    def end(self, event_id, generation):
        with self._locked():
            if generation == self._current():
                (self.root / generation / ("active-" + digest(event_id) + ".json")).unlink(missing_ok=True)

    def reset_in_progress(self):
        return _alive(_read(self.root / "resetting.json"))

    def _policy(self):
        policy = _read(self.root / "policy.json")
        if policy is None:
            if (self.root / "policy.json").exists():
                raise ValueError("Invalid interaction memory policy.")
            return {"Enabled": True, "RetentionDays": 90}
        if not 1 <= policy["RetentionDays"] <= 365:
            raise ValueError("Invalid interaction memory retention.")
        return policy

    def _prune(self, epoch, cutoff):
        stamp = epoch / "pruned.json"
        today = now().date().isoformat()
        if _read(stamp) == today:
            return
        for path in (epoch / "events").glob("*.json"):
            item = _read(path)
            try:
                if item and datetime.fromisoformat(item["At"]) + timedelta(seconds=item.get("Seconds", 0)) < cutoff:
                    path.unlink(missing_ok=True)
            except (ValueError, KeyError, TypeError):
                continue
        _write(stamp, today)

    def _apply_topics(self, item, epoch):
        if item.get("Topics") is None:
            return item
        metadata = _read(epoch / "metadata.json", {}) or {}
        excluded = {topic.casefold() for topic in metadata.get("Excluded", [])}
        aliases = {label.casefold(): corrected for label, corrected in (metadata.get("Aliases") or {}).items()}
        topics = [aliases.get(topic.casefold(), topic) for topic in item["Topics"] if topic.casefold() not in excluded]
        return dict(item, Topics=list(dict.fromkeys(topic for topic in topics if topic.casefold() not in excluded)))

    def record(self, item, generation=None):
        with self._locked():
            current = self._current()
            policy = self._policy()
            if ((generation is not None and generation != current)
                    or not policy["Enabled"] or self.reset_in_progress()):
                return False
            epoch = self.root / current
            cutoff = now() - timedelta(days=policy["RetentionDays"])
            at = datetime.fromisoformat(item["At"])
            seconds = item.get("Seconds", 0)
            if (not item["Id"] or item["Channel"] not in {"PC", "Discord"} or not 0 <= seconds <= 86400
                    or at + timedelta(seconds=seconds) < cutoff):
                return False
            self._prune(epoch, cutoff)
            path = epoch / "events" / (digest(item["Id"]) + ".json")
            if path.exists():
                return False
            _write(path, self._apply_topics(item, epoch))
            return True

    def read(self):
        with self._locked():
            generation = self._current()
            epoch = self.root / generation
            cutoff = now() - timedelta(days=self._policy()["RetentionDays"])
            self._prune(epoch, cutoff)
            paths = list((epoch / "events").glob("*.json"))
        events = []
        for path in paths:
            item = _read(path)
            try:
                at = datetime.fromisoformat(item["At"])
                seconds = item.get("Seconds", 0)
                if 0 <= seconds <= 86400 and math.isfinite(seconds):
                    events.append((at, seconds, item))
            except (KeyError, TypeError, ValueError):
                continue
        with self._locked():
            if self._current() != generation or self.reset_in_progress():
                return []
            cutoff = now() - timedelta(days=self._policy()["RetentionDays"])
            result = []
            for at, seconds, item in sorted(events, key=lambda entry: entry[0]):
                end = at + timedelta(seconds=seconds)
                if end >= cutoff:
                    if at < cutoff:
                        item = dict(item, At=cutoff.isoformat(), Seconds=(end - cutoff).total_seconds())
                    result.append(self._apply_topics(item, epoch))
            return result


def read_hashed(state, identity_hash):
    try:
        return InteractionMemory(state, identity_hash, hashed=True).read()
    except (OSError, KeyError, TypeError, ValueError):
        return []


def preferences_hashed(state, identity_hash):
    try:
        store = InteractionMemory(state, identity_hash, hashed=True)
        with store._locked():
            return (_read(store.root / store._current() / "metadata.json", {}) or {}).get("Preferences", {})
    except (OSError, KeyError, TypeError, ValueError):
        return {}
