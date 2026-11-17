"""Record only categorized Discord interactions for transport-bound identities."""
from dataclasses import dataclass
import logging
import os
from pathlib import Path

from mia_interaction import InteractionMemory, feedback, has_chat_lead, is_category, looks_like_task, now, topic_labels


LOG = logging.getLogger(__name__)


@dataclass
class Turn:
    memory: InteractionMemory
    generation: str
    identity: str
    event_id: str
    at: str
    task: str
    continuation: bool
    enabled: bool
    kind: str | None = None
    topics: tuple[str, ...] = ()

    def record(self, kind, *, status=None, topics=None, confidence=None, outcome_at=None):
        if not self.enabled:
            return
        self.memory.record({
            "Id": self.event_id + ":" + kind, "At": outcome_at or self.at, "Channel": "Discord",
            "Kind": kind, "Group": self.task, "Status": status, "Topics": topics, "TopicConfidence": confidence,
        }, self.generation)


class DiscordInteractions:
    def __init__(self, home):
        self.state = Path(os.environ.get("MIA_DATA_DIR") or home.parent / "mia")
        self.registry = Path(os.environ.get("MIA_HERMES_USER_REGISTRY") or home.parent / "mia" / "users.json")
        self._active = {}

    @staticmethod
    def key(event):
        return (event.source.user_id, event.source.chat_id, event.message_id)

    def begin(self, event, task, continuation, command):
        from mia_discord import read_registry
        from mia_interaction import ResetInProgress, StaleInteraction
        key = self.key(event)
        if key in self._active:
            return False
        try:
            if not self.registry.is_file():
                return True
            registry = read_registry(self.registry)
            subject = event.source.user_id
            owners = [b["UserId"] for b in registry["Bindings"]
                      if b["Provider"] == "discord" and b["Subject"] == subject]
            if len(owners) != 1:
                return True
            memory = InteractionMemory(self.state, owners[0])
            event_id = "discord:" + ":".join(str(part) for part in key)
            received_at = getattr(getattr(event, "raw_message", None), "created_at", None)
            generation, enabled = memory.begin(event_id, received_at)
            turn = Turn(memory, generation, owners[0], event_id, now().isoformat(), task, continuation, enabled)
            self._active[key] = turn
            if not command:
                turn.record("message", status="accepted")
                if status := feedback(event.text or ""):
                    turn.record("feedback", status=status)
            return True
        except (ResetInProgress, StaleInteraction):
            return False
        except (OSError, KeyError, TypeError, ValueError):
            LOG.exception("Could not record Discord message admission")
            return True

    def classify(self, event, kind, topics):
        if turn := self._active.get(self.key(event)):
            turn.kind = kind
            turn.topics = tuple(dict.fromkeys(topic.strip() for topic in topics if is_category(topic.strip())))

    def end(self, event, outcome=None, command=False):
        turn = self._active.pop(self.key(event), None)
        if turn is None:
            return
        try:
            if outcome is None or command or event.get_command():
                return
            text = event.text or ""
            kind = turn.kind or ("task" if turn.continuation or looks_like_task(text) or not text else "chat")
            if turn.topics or kind == "chat" or has_chat_lead(text):
                turn.record("chat", status=outcome, topics=list(turn.topics) or topic_labels(text),
                            confidence="classified" if turn.topics else "keyword")
            if kind == "task":
                turn.record("task_resume" if turn.continuation else "task_new", status="accepted")
                turn.record("task_result", status=outcome, outcome_at=now().isoformat())
            turn.record("message_result", status=outcome)
        except (OSError, KeyError, TypeError, ValueError):
            LOG.exception("Could not record Discord message completion")
        finally:
            try:
                turn.memory.end(turn.event_id, turn.generation)
            except (OSError, ValueError):
                LOG.exception("Could not release Discord interaction lease")
