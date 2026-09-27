from __future__ import annotations

import itertools
from typing import Any, Dict, List, Optional

import discord


class FakeResponse:
    status = 404
    reason = "Not Found"


class FakeMessage:
    _ids = itertools.count(900000000000000001)

    def __init__(self, channel: "FakeChannel", content: Optional[str] = None, embed: Any = None, view: Any = None, reference: Any = None):
        self.id = next(self._ids)
        self.channel, self.content, self.embed, self.view, self.reference = channel, content, embed, view, reference


class FakePartial:
    def __init__(self, channel: "FakeChannel", mid: int):
        self.channel, self.id = channel, mid

    async def edit(self, embed: Any = None, view: Any = None, **_: Any) -> None:
        msg = self.channel.messages.get(self.id)
        if msg is None:
            raise discord.NotFound(FakeResponse(), "Unknown Message")
        msg.embed, msg.view = embed, view
        self.channel.edits.append(self.id)


class FakeChannel:
    def __init__(self) -> None:
        self.messages: Dict[int, FakeMessage] = {}
        self.sent: List[FakeMessage] = []
        self.edits: List[int] = []

    async def send(self, content: Optional[str] = None, *, embed: Any = None, view: Any = None, reference: Any = None, allowed_mentions: Any = None, **_: Any) -> FakeMessage:
        assert allowed_mentions is not None and allowed_mentions.everyone is False and allowed_mentions.users is False
        m = FakeMessage(self, content, embed, view, reference)
        self.messages[m.id] = m
        self.sent.append(m)
        return m

    def get_partial_message(self, mid: int) -> FakePartial:
        return FakePartial(self, mid)


def summary(**kw: Any) -> Dict[str, Any]:
    s = {"id": 1, "game_id": "tlc", "cheat_id": "godmode", "fingerprint": "fp-1", "game_version": "0.8.5", "game_name": "The Game",
         "cheat_name": "God mode", "status": "open", "fixed_in_version": None, "message_id": None, "broken": 2, "works": 1,
         "total_broken": 2, "total_works": 1, "score": 3.5, "notes": [], "vanta_versions": ["0.3.0"], "last_report": 1800000000,
         "newest_fingerprint": True}
    s.update(kw)
    return s


class FakeApi:
    def __init__(self) -> None:
        self.pages: List[Dict[str, Any]] = []
        self.saved: List[tuple] = []
        self.fail_save = False
        self.digest_data: Dict[str, Any] = {"reports_period": 3, "broken_period": 2, "new_users": 1, "top": [summary()], "fixed": []}

    async def events(self, after: int, limit: int = 50) -> Dict[str, Any]:
        return self.pages.pop(0) if self.pages else {"cursor": after, "items": [], "more": False}

    async def set_message(self, state_id: int, mid: Optional[int]) -> None:
        if self.fail_save:
            from vanta_bot.api import ApiError
            raise ApiError(0, "unreachable")
        self.saved.append((state_id, mid))

    async def digest(self, days: int = 7) -> Dict[str, Any]:
        return self.digest_data

    async def top(self, game: Optional[str] = None, limit: int = 10) -> Dict[str, Any]:
        return {"items": []}

    async def cleanup(self) -> Dict[str, Any]:
        return {"rate_limits": 0, "events": 0}

    async def close(self) -> None:
        pass
