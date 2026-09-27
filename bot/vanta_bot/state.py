"""Small local state file (next to bot.py). The Worker stays the source of truth
(message ids are stored there too); this file only remembers where the bot left off."""
from __future__ import annotations

import json
import logging
import os
from pathlib import Path
from typing import Any, Dict, Optional

log = logging.getLogger("vanta.state")


class LocalState:
    def __init__(self, path: Path):
        self.path = path
        self.cursor: int = 0
        self.last_digest: Optional[str] = None  # ISO week, e.g. "2026-W39"
        self.messages: Dict[str, str] = {}      # state_id -> message_id (fallback if the Worker call failed)
        self.load()

    def load(self) -> None:
        try:
            data: Dict[str, Any] = json.loads(self.path.read_text(encoding="utf-8"))
        except FileNotFoundError:
            return
        except (OSError, ValueError) as e:
            log.warning("state file unreadable (%s), starting fresh", e)
            return
        self.cursor = int(data.get("cursor") or 0)
        self.last_digest = data.get("last_digest")
        msgs = data.get("messages") or {}
        self.messages = {str(k): str(v) for k, v in msgs.items()} if isinstance(msgs, dict) else {}

    def save(self) -> None:
        tmp = self.path.with_name(self.path.name + ".tmp")
        payload = {"cursor": self.cursor, "last_digest": self.last_digest, "messages": dict(list(self.messages.items())[-2000:])}
        tmp.write_text(json.dumps(payload, indent=1), encoding="utf-8")
        os.replace(tmp, self.path)  # atomic: a crash never leaves a half-written file
