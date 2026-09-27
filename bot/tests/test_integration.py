"""Runs the bot logic against a local Supabase-like stack (Postgres + the Vanta migrations + PostgREST + mock auth):
    sh ../supabase/tests/local-stack.sh up
    VANTA_TEST_SUPABASE_URL=http://127.0.0.1:54321 python -m pytest
The stack uses the test keys sb_publishable_localtest / sb_secret_localtest."""
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

URL = os.environ.get("VANTA_TEST_SUPABASE_URL")
SECRET = os.environ.get("VANTA_TEST_SECRET_KEY", "sb_secret_localtest")
PUBLISHABLE = os.environ.get("VANTA_TEST_PUBLISHABLE_KEY", "sb_publishable_localtest")
pytestmark = pytest.mark.skipif(not URL, reason="local Supabase stack not configured")


async def login(s: aiohttp.ClientSession, discord_id: str, name: str) -> str:
    """PKCE sign-in as the Vanta app does it (the mock skips the Discord consent screen)."""
    verifier = base64.urlsafe_b64encode(secrets.token_bytes(32)).rstrip(b"=").decode()
    challenge = base64.urlsafe_b64encode(hashlib.sha256(verifier.encode()).digest()).rstrip(b"=").decode()
    q = {"provider": "discord", "redirect_to": "http://127.0.0.1:50111/callback", "code_challenge": challenge,
         "code_challenge_method": "s256", "mock_discord_id": discord_id, "mock_name": name}
    async with s.get(f"{URL}/auth/v1/authorize", params=q, allow_redirects=False) as r:
        assert r.status == 302, await r.text()
        code = parse_qs(urlparse(r.headers["Location"]).query)["code"][0]
    async with s.post(f"{URL}/auth/v1/token?grant_type=pkce", headers={"apikey": PUBLISHABLE},
                      json={"auth_code": code, "code_verifier": verifier}) as r:
        assert r.status == 200, await r.text()
        return (await r.json())["access_token"]


async def report(s: aiohttp.ClientSession, token: str, **body) -> dict:
    async with s.post(f"{URL}/rest/v1/rpc/vanta_submit_report", headers={"apikey": PUBLISHABLE, "Authorization": f"Bearer {token}"},
                      json={f"p_{k}": v for k, v in body.items()}) as r:
        assert r.status == 200, await r.text()
        return await r.json()


async def test_bot_against_local_supabase(tmp_path):
    game = "it-" + secrets.token_hex(3)
    uid = str(400000000000000000 + secrets.randbelow(10 ** 12))
    async with aiohttp.ClientSession() as s:
        token = await login(s, uid, "integration")
        async with VantaApi(URL, SECRET) as api:
            cfg = Config.from_env({"DISCORD_BOT_TOKEN": "x", "DISCORD_CHANNEL_ID": "123456789012345678", "ADMIN_IDS": "111111111111111111",
                                   "SUPABASE_URL": URL, "SUPABASE_SERVICE_ROLE_KEY": SECRET, "STATE_FILE": str(tmp_path / "state.json")})
            bot = VantaBot(cfg, api=api, state=LocalState(cfg.state_file))
            ch = FakeChannel()
            while True:  # skip history
                d = await api.events(bot.local.cursor, 200)
                bot.local.cursor = d["cursor"]
                if not d["more"]:
                    break
            r = await report(s, token, game_id=game, cheat_id="godmode", fingerprint="fp", status="broken", vanta_version="0.3.0",
                             note="@everyone crashes", game_name="Integration Game", cheat_name="God mode")
            assert r["community"] == {"works": 0, "broken": 1, "status": "open", "fixed_in_version": None}
            await bot.poll_once(ch)
            assert len(ch.sent) == 1
            msg = ch.sent[0]
            assert msg.embed.title == "God mode — Integration Game"
            state_id = int(msg.view.children[0].item.custom_id.split(":")[2])
            st = await api.state(state_id)
            assert st["message_id"] == str(msg.id) and st["reporters"][0]["discord_id"] == uid
            # a second report edits the same message
            token2 = await login(s, str(int(uid) + 1), "second")
            await report(s, token2, game_id=game, cheat_id="godmode", fingerprint="fp", status="broken", vanta_version="0.3.0")
            await bot.poll_once(ch)
            assert len(ch.sent) == 1 and ch.edits == [msg.id]
            res = await api.set_status("fixed", game_id=game, cheat_id="godmode", fixed_in_version="0.3.1")
            assert res["updated"][0]["status"] == "fixed"
            await bot.poll_once(ch)
            assert ch.sent[-1].content.startswith("✅ **God mode**")
            assert (await api.top(game))["items"] == []
            # ban via the bot; the banned user can no longer report
            assert (await api.ban(int(uid)))["banned"] is True
            async with s.post(f"{URL}/rest/v1/rpc/vanta_submit_report", headers={"apikey": PUBLISHABLE, "Authorization": f"Bearer {token}"},
                              json={"p_game_id": game, "p_cheat_id": "ammo", "p_fingerprint": "fp", "p_status": "broken", "p_vanta_version": "0.3.1"}) as r:
                assert r.status == 403 and (await r.json())["message"] == "banned"
            d = await api.digest(7)
            assert "top" in d and "fixed" in d
            assert "events" in await api.cleanup()


async def test_publishable_key_cannot_use_bot_functions():
    async with VantaApi(URL, PUBLISHABLE, backoff=0.01) as api:
        with pytest.raises(ApiError) as e:
            await api.stats()
        assert e.value.code == "permission_denied"


async def test_unknown_function_and_unreachable():
    async with VantaApi(URL, SECRET, backoff=0.01) as api:
        with pytest.raises(ApiError) as e:
            await api.rpc("vanta_bot_does_not_exist")
        assert e.value.code == "function_missing" and e.value.status == 404
    async with VantaApi("http://127.0.0.1:9", SECRET, attempts=3, backoff=0.01) as api:
        with pytest.raises(ApiError) as e:
            await api.stats()
        assert e.value.status == 0
