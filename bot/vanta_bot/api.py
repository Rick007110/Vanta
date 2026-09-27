"""Client for the Worker's /bot/* API, with retries and backoff."""
from __future__ import annotations

import asyncio
import logging
import random
from typing import Any, Dict, Optional

import aiohttp

log = logging.getLogger("vanta.api")


class ApiError(Exception):
    def __init__(self, status: int, code: str, message: str = ""):
        super().__init__(f"{status} {code} {message}".strip())
        self.status, self.code, self.message = status, code, message


class VantaApi:
    def __init__(self, base_url: str, secret: str, session: Optional[aiohttp.ClientSession] = None,
                 attempts: int = 4, backoff: float = 1.0, timeout: float = 15.0):
        self.base = base_url.rstrip("/")
        self._secret = secret
        self._session = session
        self._own_session = session is None
        self.attempts, self.backoff = attempts, backoff
        self.timeout = aiohttp.ClientTimeout(total=timeout)

    async def __aenter__(self) -> "VantaApi":
        return self

    async def __aexit__(self, *exc: Any) -> None:
        await self.close()

    @property
    def session(self) -> aiohttp.ClientSession:
        if self._session is None or self._session.closed:
            self._session = aiohttp.ClientSession(headers={"User-Agent": "vanta-bot"})
            self._own_session = True
        return self._session

    async def close(self) -> None:
        if self._own_session and self._session and not self._session.closed:
            await self._session.close()

    async def request(self, method: str, path: str, *, params: Optional[Dict[str, Any]] = None,
                      json: Optional[Dict[str, Any]] = None) -> Dict[str, Any]:
        """Retries network errors, 5xx and 429 (honouring Retry-After). 4xx errors raise immediately."""
        headers = {"Authorization": f"Bearer {self._secret}"}
        clean = {k: str(v) for k, v in (params or {}).items() if v is not None}
        last: Exception = ApiError(0, "unreachable")
        for attempt in range(1, self.attempts + 1):
            delay = self.backoff * (2 ** (attempt - 1)) * (0.8 + 0.4 * random.random())
            try:
                async with self.session.request(method, self.base + path, params=clean, json=json,
                                                headers=headers, timeout=self.timeout) as r:
                    try:
                        data = await r.json(content_type=None)
                    except ValueError:
                        data = {}
                    if not isinstance(data, dict):
                        data = {}
                    if r.status < 400:
                        return data
                    err = ApiError(r.status, str(data.get("error") or "http_error"), str(data.get("message") or ""))
                    if r.status == 429:
                        try:
                            delay = max(delay, float(r.headers.get("Retry-After", "1")))
                        except ValueError:
                            pass
                    elif r.status < 500:
                        raise err
                    last = err
            except (aiohttp.ClientError, asyncio.TimeoutError) as e:
                last = ApiError(0, "unreachable", type(e).__name__)
            if attempt < self.attempts:
                log.warning("API %s %s failed (%s), retry %d/%d in %.1fs", method, path, last, attempt, self.attempts - 1, delay)
                await asyncio.sleep(min(delay, 60))
        raise last

    # --- endpoints -------------------------------------------------------------------------------
    async def events(self, after: int, limit: int = 50) -> Dict[str, Any]:
        return await self.request("GET", "/bot/events", params={"after": after, "limit": limit})

    async def state(self, state_id: int) -> Dict[str, Any]:
        return (await self.request("GET", f"/bot/state/{int(state_id)}"))["state"]

    async def set_message(self, state_id: int, message_id: Optional[int]) -> None:
        await self.request("POST", "/bot/message", json={"state_id": state_id, "message_id": str(message_id) if message_id else None})

    async def set_status(self, status: str, *, state_id: Optional[int] = None, game_id: Optional[str] = None,
                         cheat_id: Optional[str] = None, fingerprint: Optional[str] = None,
                         fixed_in_version: Optional[str] = None) -> Dict[str, Any]:
        body: Dict[str, Any] = {"status": status, "fixed_in_version": fixed_in_version}
        if state_id is not None:
            body["state_id"] = state_id
        else:
            body.update(game_id=game_id, cheat_id=cheat_id, fingerprint=fingerprint)
        return await self.request("POST", "/bot/status", json=body)

    async def ban(self, discord_id: int, banned: bool = True) -> Dict[str, Any]:
        return await self.request("POST", "/bot/ban", json={"discord_id": str(discord_id), "banned": banned})

    async def top(self, game: Optional[str] = None, limit: int = 10) -> Dict[str, Any]:
        return await self.request("GET", "/bot/top", params={"game": game, "limit": limit})

    async def cheat(self, game: str, cheat: str) -> Dict[str, Any]:
        return await self.request("GET", "/bot/cheat", params={"game": game, "cheat": cheat})

    async def game(self, game: str) -> Dict[str, Any]:
        return await self.request("GET", "/bot/game", params={"game": game})

    async def stats(self) -> Dict[str, Any]:
        return await self.request("GET", "/bot/stats")

    async def digest(self, days: int = 7) -> Dict[str, Any]:
        return await self.request("GET", "/bot/digest", params={"days": days})
