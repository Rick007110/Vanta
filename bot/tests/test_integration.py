"""Runs the bot logic against a real local Worker (sh ../server/scripts/dev-local.sh).
Enable with: VANTA_TEST_API_URL=http://127.0.0.1:8787 VANTA_TEST_BOT_SECRET=<from .tmp/.dev.vars> pytest"""
import base64
import hashlib
import os
import secrets
from urllib.parse import parse_qs, urlparse

import aiohttp
import pytest

from vanta_bot.api import ApiError, VantaApi
from vanta_bot.client import VantaBot
from vanta_bot.config import Config
from vanta_bot.state import LocalState

from .fakes import FakeChannel

API = os.environ.get("VANTA_TEST_API_URL")
SECRET = os.environ.get("VANTA_TEST_BOT_SECRET", "")
pytestmark = pytest.mark.skipif(not API, reason="local Worker not configured")


async def login(s: aiohttp.ClientSession) -> str:
    verifier = base64.urlsafe_b64encode(secrets.token_bytes(32)).rstrip(b"=").decode()
    challenge = base64.urlsafe_b64encode(hashlib.sha256(verifier.encode()).digest()).rstrip(b"=").decode()
    url = f"{API}/auth/start?port=50111&state={secrets.token_urlsafe(16)}&challenge={challenge}"
    for _ in range(3):  # worker -> mock discord -> worker callback -> loopback (not followed)
        async with s.get(url, allow_redirects=False) as r:
            assert r.status == 302, await r.text()
            url = r.headers["Location"]
    code = parse_qs(urlparse(url).query)["code"][0]
    async with s.post(f"{API}/auth/token", json={"code": code, "verifier": verifier}) as r:
        assert r.status == 200
        return (await r.json())["token"]


async def test_bot_against_local_worker(tmp_path):
    game = "it-" + secrets.token_hex(3)
    async with aiohttp.ClientSession() as s:
        token = await login(s)
        async with VantaApi(API, SECRET) as api:
            cfg = Config.from_env({"DISCORD_BOT_TOKEN": "x", "DISCORD_CHANNEL_ID": "123456789012345678", "ADMIN_IDS": "111111111111111111",
                                   "VANTA_API_URL": API, "BOT_API_SECRET": SECRET, "STATE_FILE": str(tmp_path / "state.json")})
            bot = VantaBot(cfg, api=api, state=LocalState(cfg.state_file))
            ch = FakeChannel()
            # skip history
            while True:
                d = await api.events(bot.local.cursor, 200)
                bot.local.cursor = d["cursor"]
                if not d["more"]:
                    break
            async with s.post(f"{API}/reports", headers={"Authorization": f"Bearer {token}"},
                              json={"game_id": game, "cheat_id": "godmode", "fingerprint": "fp", "status": "broken", "vanta_version": "0.3.0",
                                    "note": "@everyone crashes", "game_name": "Integration Game", "cheat_name": "God mode"}) as r:
                assert r.status == 200, await r.text()
            await bot.poll_once(ch)
            assert len(ch.sent) == 1
            msg = ch.sent[0]
            assert msg.embed.title == "God mode — Integration Game"
            state_id = int(msg.view.children[0].item.custom_id.split(":")[2])
            assert (await api.state(state_id))["message_id"] == str(msg.id)
            res = await api.set_status("fixed", game_id=game, cheat_id="godmode", fixed_in_version="0.3.1")
            assert res["updated"][0]["status"] == "fixed"
            await bot.poll_once(ch)
            assert ch.edits == [msg.id] and ch.sent[-1].content.startswith("✅ **God mode**")
            top = await api.top(game)
            assert top["items"] == []


async def test_bad_secret_is_not_retried():
    async with VantaApi(API, "wrong" * 8, backoff=0.01) as api:
        with pytest.raises(ApiError) as e:
            await api.stats()
        assert e.value.status == 401


async def test_unreachable_is_retried():
    async with VantaApi("http://127.0.0.1:9", SECRET, attempts=3, backoff=0.01) as api:
        with pytest.raises(ApiError) as e:
            await api.stats()
        assert e.value.status == 0
