"""Being a good guest: when to speak, how often, and what to remember."""

from __future__ import annotations

import re
import time
from collections import deque
from dataclasses import dataclass


class SpeechLimiter:
    """A token bucket for the greeter's own chat, much stricter than the
    server's limit (3 per second, burst 10). Defaults: one line every four
    seconds on average, at most three in a row."""

    def __init__(self, per_second: float = 0.25, burst: float = 3.0, now: float | None = None) -> None:
        self.per_second = per_second
        self.burst = burst
        self.tokens = burst
        self.last = time.monotonic() if now is None else now

    def allow(self, now: float | None = None) -> bool:
        now = time.monotonic() if now is None else now
        self.tokens = min(self.burst, self.tokens + (now - self.last) * self.per_second)
        self.last = now
        if self.tokens >= 1.0:
            self.tokens -= 1.0
            return True
        return False


def is_addressed(text: str, my_name: str) -> bool:
    """True if `text` mentions `my_name` as a word (case-insensitive),
    with or without a leading '@'."""
    if not my_name:
        return False
    pattern = r"(?<![\w])@?" + re.escape(my_name.lower()) + r"(?![\w])"
    return re.search(pattern, text.lower()) is not None


@dataclass
class Heard:
    """One chat line as the reply policy sees it."""

    sender_user_id: str
    sender_name: str
    sender_is_agent: bool
    channel: str  # "CHAT_CHANNEL_LOCAL" or "CHAT_CHANNEL_WORLD"
    text: str
    distance: float  # metres from the greeter; inf if unknown


class ReplyPolicy:
    """Decides whether to answer a chat line.

    * Never answer yourself.
    * Humans: answer when addressed by name (any channel), or when they speak
      in local chat within `hear_radius` metres.
    * Agents: answer only when addressed by name, and at most
      `agent_replies` times per agent within `agent_window` seconds. Two
      agents that both answer every message would otherwise talk forever.
    """

    def __init__(self, my_user_id: str = "", my_name: str = "", hear_radius: float = 10.0,
                 agent_replies: int = 2, agent_window: float = 300.0) -> None:
        self.my_user_id = my_user_id
        self.my_name = my_name
        self.hear_radius = hear_radius
        self.agent_replies = agent_replies
        self.agent_window = agent_window
        self._agent_log: dict[str, deque[float]] = {}

    def should_reply(self, heard: Heard, now: float | None = None) -> bool:
        now = time.monotonic() if now is None else now
        if heard.sender_user_id and heard.sender_user_id == self.my_user_id:
            return False
        addressed = is_addressed(heard.text, self.my_name)
        if heard.sender_is_agent:
            if not addressed:
                return False
            log = self._agent_log.setdefault(heard.sender_user_id, deque())
            while log and now - log[0] > self.agent_window:
                log.popleft()
            if len(log) >= self.agent_replies:
                return False
            log.append(now)
            return True
        if addressed:
            return True
        return heard.channel == "CHAT_CHANNEL_LOCAL" and heard.distance <= self.hear_radius


class Memory:
    """A short rolling transcript of what the greeter heard, said and saw."""

    def __init__(self, max_lines: int = 40) -> None:
        self.lines: deque[str] = deque(maxlen=max_lines)

    def add(self, line: str) -> None:
        self.lines.append(line.replace("\n", " ").strip())

    def transcript(self) -> str:
        return "\n".join(self.lines) if self.lines else "(nothing yet)"


def fit_chat(text: str, max_chars: int = 500) -> str:
    """Collapses whitespace and cuts `text` to the server's chat limit."""
    text = " ".join(text.split())
    if len(text) <= max_chars:
        return text
    return text[: max_chars - 1].rstrip() + "…"
