from __future__ import annotations

import os
from dataclasses import dataclass, field
from pathlib import Path
from typing import FrozenSet, List, Mapping, Optional

BOT_DIR = Path(__file__).resolve().parent.parent


class ConfigError(Exception):
    def __init__(self, problems: List[str]):
        super().__init__("; ".join(problems))
        self.problems = problems


def _snowflake(v: str) -> Optional[int]:
    v = v.strip()
    return int(v) if v.isdigit() and 5 <= len(v) <= 25 else None


@dataclass(frozen=True)
class Config:
    token: str
    channel_id: int
    admin_ids: FrozenSet[int]
    guild_id: Optional[int]
    api_url: str
    api_secret: str
    poll_seconds: int = 15
    digest_weekday: int = 0  # 0 = maandag
    digest_hour: int = 10
    timezone: str = "Europe/Amsterdam"
    state_file: Path = field(default_factory=lambda: BOT_DIR / "state.json")
    log_level: str = "INFO"

    @classmethod
    def from_env(cls, env: Mapping[str, str] = os.environ, require_discord: bool = True) -> "Config":
        g = lambda k, d="": (env.get(k) or d).strip()  # noqa: E731
        problems: List[str] = []
        token = g("DISCORD_BOT_TOKEN")
        if require_discord and not token:
            problems.append("DISCORD_BOT_TOKEN ontbreekt")
        channel = _snowflake(g("DISCORD_CHANNEL_ID"))
        if require_discord and channel is None:
            problems.append("DISCORD_CHANNEL_ID ontbreekt of is geen id")
        admins = set()
        for part in g("ADMIN_IDS").replace(";", ",").split(","):
            if part.strip():
                sid = _snowflake(part)
                if sid is None:
                    problems.append(f"ADMIN_IDS bevat een ongeldig id: {part.strip()!r}")
                else:
                    admins.add(sid)
        if require_discord and not admins:
            problems.append("ADMIN_IDS ontbreekt (je eigen Discord user id)")
        guild_raw = g("DISCORD_GUILD_ID")
        guild = _snowflake(guild_raw) if guild_raw else None
        if guild_raw and guild is None:
            problems.append("DISCORD_GUILD_ID is geen geldig id")
        api = g("VANTA_API_URL").rstrip("/")
        if not api.startswith(("https://", "http://127.0.0.1", "http://localhost")):
            problems.append("VANTA_API_URL ontbreekt of is geen https-adres")
        secret = g("BOT_API_SECRET")
        if len(secret) < 32:
            problems.append("BOT_API_SECRET ontbreekt of is korter dan 32 tekens")

        def num(k: str, d: int, lo: int, hi: int) -> int:
            raw = g(k, str(d))
            try:
                n = int(raw)
            except ValueError:
                problems.append(f"{k} is geen getal")
                return d
            if not lo <= n <= hi:
                problems.append(f"{k} moet tussen {lo} en {hi} liggen")
                return d
            return n

        poll = num("POLL_SECONDS", 15, 5, 3600)
        wd = num("DIGEST_WEEKDAY", 0, 0, 6)
        hour = num("DIGEST_HOUR", 10, 0, 23)
        tz = g("TIMEZONE", "Europe/Amsterdam")
        try:
            from zoneinfo import ZoneInfo

            ZoneInfo(tz)
        except Exception:
            problems.append(f"TIMEZONE {tz!r} is onbekend")
        state = g("STATE_FILE")
        if problems:
            raise ConfigError(problems)
        return cls(
            token=token, channel_id=channel or 0, admin_ids=frozenset(admins), guild_id=guild, api_url=api,
            api_secret=secret, poll_seconds=poll, digest_weekday=wd, digest_hour=hour, timezone=tz,
            state_file=Path(state) if state else BOT_DIR / "state.json", log_level=g("LOG_LEVEL", "INFO").upper(),
        )

    def is_admin(self, user_id: int) -> bool:
        return user_id in self.admin_ids
