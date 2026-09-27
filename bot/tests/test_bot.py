import datetime as dt
from pathlib import Path

import discord
import pytest

from vanta_bot.client import VantaBot, cheat_choices, game_choices
from vanta_bot.config import Config, ConfigError
from vanta_bot.state import LocalState
from vanta_bot.views import StatusButton, view_for

from .fakes import FakeApi, FakeChannel, summary

ENV = {"DISCORD_BOT_TOKEN": "x.y.z", "DISCORD_CHANNEL_ID": "123456789012345678", "ADMIN_IDS": "111111111111111111, 222222222222222222",
       "SUPABASE_URL": "https://abcd1234.supabase.co/", "SUPABASE_SERVICE_ROLE_KEY": "sb_secret_" + "s" * 30}


def make_bot(tmp_path: Path, **env):
    cfg = Config.from_env({**ENV, "STATE_FILE": str(tmp_path / "state.json"), **env})
    api = FakeApi()
    return VantaBot(cfg, api=api, state=LocalState(cfg.state_file)), api


def test_config_parses_and_validates():
    c = Config.from_env(ENV)
    assert c.channel_id == 123456789012345678 and c.admin_ids == {111111111111111111, 222222222222222222}
    assert c.supabase_url == "https://abcd1234.supabase.co" and c.poll_seconds == 15 and c.guild_id is None
    assert c.is_admin(111111111111111111) and not c.is_admin(3)
    with pytest.raises(ConfigError) as e:
        Config.from_env({"ADMIN_IDS": "abc", "SUPABASE_URL": "http://evil", "SUPABASE_SERVICE_ROLE_KEY": "short", "POLL_SECONDS": "1", "TIMEZONE": "Mars/Base"})
    msg = " ".join(e.value.problems)
    for k in ["DISCORD_BOT_TOKEN", "DISCORD_CHANNEL_ID", "ADMIN_IDS", "SUPABASE_URL", "SUPABASE_SERVICE_ROLE_KEY", "POLL_SECONDS", "TIMEZONE"]:
        assert k in msg


def _jwt(role):
    import base64
    import json
    part = base64.urlsafe_b64encode(json.dumps({"role": role}).encode()).rstrip(b"=").decode()
    return "eyJhbGciOiJIUzI1NiJ9." + part + ".sig"


def test_public_keys_are_refused_and_secret_keys_accepted():
    from vanta_bot.config import key_problem
    assert key_problem("sb_secret_abc") is None
    assert key_problem(_jwt("service_role")) is None
    assert "publishable" in key_problem("sb_publishable_abc")
    assert "anon" in key_problem(_jwt("anon"))
    assert key_problem("") and key_problem("nonsense")
    c = Config.from_env({**ENV, "SUPABASE_SERVICE_ROLE_KEY": "", "SUPABASE_SECRET_KEY": "sb_secret_x", "SUPABASE_URL": "https://x.supabase.co/rest/v1/"})
    assert c.supabase_key == "sb_secret_x" and c.supabase_url == "https://x.supabase.co"


def test_state_roundtrip_is_atomic(tmp_path):
    s = LocalState(tmp_path / "state.json")
    s.cursor, s.last_digest, s.messages["5"] = 42, "2026-W39", "999"
    s.save()
    assert not (tmp_path / "state.json.tmp").exists()
    t = LocalState(tmp_path / "state.json")
    assert (t.cursor, t.last_digest, t.messages) == (42, "2026-W39", {"5": "999"})
    (tmp_path / "state.json").write_text("{broken")
    assert LocalState(tmp_path / "state.json").cursor == 0


async def test_new_broken_report_posts_then_repeat_edits(tmp_path):
    bot, api = make_bot(tmp_path)
    ch = FakeChannel()
    api.pages = [{"cursor": 10, "more": False, "items": [{"has_broken": True, "types": ["report"], "state": summary()}]}]
    assert await bot.poll_once(ch) == 1
    assert len(ch.sent) == 1 and ch.sent[0].embed.title == "God mode — The Game"
    mid = ch.sent[0].id
    assert api.saved == [(1, mid)] and bot.local.cursor == 10
    assert LocalState(bot.config.state_file).cursor == 10  # survives a restart
    # repeat: Supabase now knows the message id -> edit, no new post
    api.pages = [{"cursor": 11, "more": False, "items": [{"has_broken": True, "types": ["report"], "state": summary(broken=3, message_id=str(mid))}]}]
    await bot.poll_once(ch)
    assert len(ch.sent) == 1 and ch.edits == [mid]
    assert "3 werkt niet" in ch.messages[mid].embed.fields[0].value


async def test_works_only_reports_do_not_post(tmp_path):
    bot, api = make_bot(tmp_path)
    ch = FakeChannel()
    api.pages = [{"cursor": 3, "more": False, "items": [{"has_broken": False, "types": ["report"], "state": summary(broken=0, works=4)}]}]
    await bot.poll_once(ch)
    assert ch.sent == [] and bot.local.cursor == 3


async def test_deleted_message_is_reposted_and_supabase_failure_uses_local_state(tmp_path):
    bot, api = make_bot(tmp_path)
    ch = FakeChannel()
    api.fail_save = True
    api.pages = [{"cursor": 5, "more": False, "items": [{"has_broken": True, "types": ["report"], "state": summary(message_id="123456789")}]}]
    await bot.poll_once(ch)  # stored id no longer exists in the channel -> new post
    assert len(ch.sent) == 1
    mid = ch.sent[0].id
    assert bot.local.messages["1"] == str(mid)
    # Supabase didn't store it; next event still edits thanks to the local fallback and then syncs
    api.fail_save = False
    api.pages = [{"cursor": 6, "more": False, "items": [{"has_broken": True, "types": ["report"], "state": summary()}]}]
    await bot.poll_once(ch)
    assert len(ch.sent) == 1 and ch.edits == [mid] and api.saved == [(1, mid)]


async def test_fixed_status_edits_and_replies(tmp_path):
    bot, api = make_bot(tmp_path)
    ch = FakeChannel()
    first = await ch.send(embed=None, allowed_mentions=discord.AllowedMentions.none())
    api.pages = [{"cursor": 8, "more": False, "items": [{"has_broken": False, "types": ["status"], "state": summary(status="fixed", fixed_in_version="0.3.1", message_id=str(first.id))}]}]
    await bot.poll_once(ch)
    assert ch.edits == [first.id]
    assert ch.messages[first.id].embed.description == "✅ Gefixt in Vanta 0.3.1"
    reply = ch.sent[-1]
    assert reply.content == "✅ **God mode** (The Game) is gefixt in Vanta 0.3.1." and reply.reference.message_id == first.id
    labels = [c.item.label for c in ch.messages[first.id].view.children]
    assert labels == ["Heropenen"]


async def test_pagination(tmp_path):
    bot, api = make_bot(tmp_path)
    ch = FakeChannel()
    api.pages = [
        {"cursor": 100, "more": True, "items": [{"has_broken": True, "types": ["report"], "state": summary(id=i, cheat_id=f"c{i}")} for i in range(1, 4)]},
        {"cursor": 150, "more": False, "items": [{"has_broken": True, "types": ["report"], "state": summary(id=9, cheat_id="c9")}]},
    ]
    assert await bot.poll_once(ch) == 4
    assert len(ch.sent) == 4 and bot.local.cursor == 150


def test_digest_schedule(tmp_path):
    bot, _ = make_bot(tmp_path, DIGEST_WEEKDAY="0", DIGEST_HOUR="10")
    mon_9 = dt.datetime(2026, 9, 28, 7, 30, tzinfo=dt.timezone.utc)   # 09:30 Amsterdam
    mon_10 = dt.datetime(2026, 9, 28, 8, 5, tzinfo=dt.timezone.utc)   # 10:05 Amsterdam
    tue = dt.datetime(2026, 9, 29, 12, 0, tzinfo=dt.timezone.utc)
    assert bot.digest_due(mon_9) is None
    assert bot.digest_due(mon_10) == "2026-W40"
    assert bot.digest_due(tue) is None
    bot.local.last_digest = "2026-W40"
    assert bot.digest_due(mon_10) is None


async def test_post_digest(tmp_path):
    bot, _ = make_bot(tmp_path)
    ch = FakeChannel()
    await bot.post_digest("2026-W40", ch)
    assert ch.sent[0].embed.title == "Weekoverzicht 2026-W40" and bot.local.last_digest == "2026-W40"


def test_buttons_are_persistent_dynamic_items():
    v = view_for(summary(id=77))
    assert v.timeout is None
    assert [c.custom_id for c in v.children] == ["vanta:fix:77", "vanta:cnr:77", "vanta:dup:77"]
    assert StatusButton.__discord_ui_compiled_template__.fullmatch("vanta:open:12")
    assert not StatusButton.__discord_ui_compiled_template__.fullmatch("vanta:delete:12")


async def test_commands_registered_and_hash_stable(tmp_path):
    bot, _ = make_bot(tmp_path)
    from vanta_bot.commands import register
    register(bot)
    names = sorted(c.name for c in bot.tree.get_commands())
    assert names == ["ban", "cheat", "digest", "fixed", "game", "stats", "top"]
    admin = {c.name for c in bot.tree.get_commands() if c.default_permissions is not None}
    assert admin == {"ban", "digest", "fixed"}
    assert bot.commands_hash() == bot.commands_hash()


def test_autocomplete(tmp_path):
    bot, _ = make_bot(tmp_path)
    bot.remember(summary())
    bot.remember(summary(cheat_id="ammo", cheat_name="Infinite ammo"))
    assert [c.value for c in game_choices(bot, "the")] == ["tlc"]
    assert [c.value for c in cheat_choices(bot, "tlc", "ammo")] == ["ammo"]
    assert cheat_choices(bot, "other", "") == []
