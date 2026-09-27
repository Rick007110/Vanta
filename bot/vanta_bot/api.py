"""Client for the Vanta functions in Supabase (PostgREST RPC: POST /rest/v1/rpc/vanta_bot_*), with retries and backoff.

Authenticates with the project's secret key (sb_secret_...) or the legacy service_role key. The key is sent in the
"apikey" header; legacy JWT keys are also sent as Bearer token, as Supabase expects.
"""
from __future__ import annotations

import asyncio
import logging
import random
import re
from typing import Any, Dict, Optional

import aiohttp

log = logging.getLogger("vanta.api")
_CODE = re.compile(r"^[a-z][a-z0-9_]{1,40}$")


class ApiError(Exception):
    def __init__(self, status: int, code: str, message: str = ""):
        super().__init__(f"{status} {code} {message}".strip())
        self.status, self.code, self.message = status, code, message


def _error(status: int, data: Any) -> ApiError:
    """PostgREST errors: {"code": "PT404" | "42501" | "PGRST202", "message": ..., "details": ..., "hint": ...}.
    The Vanta functions raise a short error code as message (e.g. "not_found", "rate_limited")."""
    if not isinstance(data, dict):
        return ApiError(status, "http_error")
    msg, pg = str(data.get("message") or ""), str(data.get("code") or "")
    if _CODE.match(msg):
        code = msg
    elif pg == "42501":
        code = "permission_denied"
    elif pg == "PGRST202":
        code = "function_missing"
    elif pg.startswith("PGRST3") or status == 401:
        code = "bad_key"
    else:
        code = "http_error"
    return ApiError(status, code, str(data.get("details") or msg)[:300])


class VantaApi:
    def __init__(self, supabase_url: str, key: str, session: Optional[aiohttp.ClientSession] = None,
                 attempts: int = 4, backoff: float = 1.0, timeout: float = 15.0):
        self.base = supabase_url.rstrip("/") + "/rest/v1/rpc/"
        self._headers = {"apikey": key, "Content-Type": "application/json", "Accept": "application/json"}
        if key.startswith("eyJ"):  # legacy service_role JWT
            self._headers["Authorization"] = f"Bearer {key}"
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

    async def rpc(self, fn: str, args: Optional[Dict[str, Any]] = None) -> Any:
        """Calls a function. Retries network errors, 5xx and 429 (honouring retry_after). Other 4xx raise immediately."""
        last: Exception = ApiError(0, "unreachable")
        for attempt in range(1, self.attempts + 1):
            delay = self.backoff * (2 ** (attempt - 1)) * (0.8 + 0.4 * random.random())
            try:
                async with self.session.post(self.base + fn, json=args or {}, headers=self._headers, timeout=self.timeout) as r:
                    try:
                        data = await r.json(content_type=None)
                    except ValueError:
                        data = None
                    if r.status < 400:
                        return data
                    err = _error(r.status, data)
                    if r.status == 429:
                        m = re.search(r"retry_after=(\d+)", err.message) or re.search(r"(\d+)", r.headers.get("Retry-After", ""))
                        if m:
                            delay = max(delay, float(m.group(1)))
                    elif r.status < 500:
                        raise err
                    last = err
            except (aiohttp.ClientError, asyncio.TimeoutError) as e:
                last = ApiError(0, "unreachable", type(e).__name__)
            if attempt < self.attempts:
                log.warning("Supabase %s failed (%s), retry %d/%d in %.1fs", fn, last, attempt, self.attempts - 1, delay)
                await asyncio.sleep(min(delay, 60))
        raise last

    @staticmethod
    def _args(**kw: Any) -> Dict[str, Any]:
        return {f"p_{k}": v for k, v in kw.items() if v is not None}

    # --- functions (supabase/migrations/*_bot_api.sql) --------------------------------------------
    async def events(self, after: int, limit: int = 50) -> Dict[str, Any]:
        return await self.rpc("vanta_bot_events", self._args(after=after, limit=limit))

    async def state(self, state_id: int) -> Dict[str, Any]:
        return await self.rpc("vanta_bot_state", self._args(state_id=int(state_id)))

    async def set_message(self, state_id: int, message_id: Optional[int]) -> None:
        await self.rpc("vanta_bot_set_message", {"p_state_id": state_id, "p_message_id": str(message_id) if message_id else None})

    async def set_status(self, status: str, *, state_id: Optional[int] = None, game_id: Optional[str] = None,
                         cheat_id: Optional[str] = None, fingerprint: Optional[str] = None,
                         fixed_in_version: Optional[str] = None) -> Dict[str, Any]:
        if state_id is not None:
            args = self._args(status=status, state_id=state_id, fixed_in_version=fixed_in_version)
        else:
            args = self._args(status=status, game_id=game_id, cheat_id=cheat_id, fingerprint=fingerprint, fixed_in_version=fixed_in_version)
        return await self.rpc("vanta_bot_set_status", args)

    async def ban(self, discord_id: int, banned: bool = True) -> Dict[str, Any]:
        return await self.rpc("vanta_bot_ban", {"p_discord_id": str(discord_id), "p_banned": banned})

    async def top(self, game: Optional[str] = None, limit: int = 10) -> Dict[str, Any]:
        return await self.rpc("vanta_bot_top", self._args(game=game, limit=limit))

    async def cheat(self, game: str, cheat: str) -> Dict[str, Any]:
        return await self.rpc("vanta_bot_cheat", self._args(game=game, cheat=cheat))

    async def game(self, game: str) -> Dict[str, Any]:
        return await self.rpc("vanta_bot_game", self._args(game=game))

    async def stats(self) -> Dict[str, Any]:
        return await self.rpc("vanta_bot_stats")

    async def digest(self, days: int = 7) -> Dict[str, Any]:
        return await self.rpc("vanta_bot_digest", self._args(days=days))

    async def cleanup(self) -> Dict[str, Any]:
        return await self.rpc("vanta_bot_cleanup")
