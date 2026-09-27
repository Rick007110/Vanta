from __future__ import annotations

import datetime as dt
import hashlib
import json
import logging
from typing import Any, Dict, List, Optional
from zoneinfo import ZoneInfo

import discord
from discord import app_commands
from discord.ext import commands, tasks

from .api import ApiError, VantaApi
from .config import Config
from .render import digest_embed, fixed_notice, report_embed
from .state import LocalState
from .views import StatusButton, view_for

log = logging.getLogger("vanta.bot")
NO_MENTIONS = discord.AllowedMentions.none()


class VantaBot(commands.Bot):
    def __init__(self, config: Config, api: Optional[VantaApi] = None, state: Optional[LocalState] = None, sync_mode: str = "auto"):
        intents = discord.Intents.none()
        intents.guilds = True  # enough for slash commands, buttons and posting; no privileged intents
        super().__init__(command_prefix=commands.when_mentioned, intents=intents, allowed_mentions=NO_MENTIONS,
                         help_command=None, max_messages=None)
        self.config = config
        self.api = api or VantaApi(config.api_url, config.api_secret)
        self.local = state or LocalState(config.state_file)
        self.sync_mode = sync_mode  # auto | force | only
        self.known: Dict[str, Dict[str, Any]] = {}  # game_id -> {"name", "cheats": {cheat_id: name}} for autocomplete
        self.poll_task = tasks.loop(seconds=config.poll_seconds)(self._poll)
        self.poll_task.before_loop(self._wait_ready)
        self.digest_task = tasks.loop(minutes=5)(self._digest_tick)
        self.digest_task.before_loop(self._wait_ready)

    # ---- lifecycle ---------------------------------------------------------------------------
    async def setup_hook(self) -> None:
        from .commands import register

        self.add_dynamic_items(StatusButton)
        register(self)
        await self.sync_commands()
        if self.sync_mode == "only":
            return
        try:
            for s in (await self.api.top(limit=25)).get("items", []):
                self.remember(s)
        except ApiError as e:
            log.warning("Worker not reachable at startup (%s); will keep retrying", e)
        self.poll_task.start()
        self.digest_task.start()

    async def close(self) -> None:
        for t in (self.poll_task, self.digest_task):
            t.cancel()
        try:
            self.local.save()
        except OSError:
            log.exception("could not save state")
        await self.api.close()
        await super().close()

    async def _wait_ready(self) -> None:
        await self.wait_until_ready()

    async def on_ready(self) -> None:
        log.info("logged in as %s (%s); %d guild(s)", self.user, getattr(self.user, "id", "?"), len(self.guilds))
        if self.sync_mode == "only":
            await self.close()

    async def on_resumed(self) -> None:
        log.info("gateway session resumed")

    async def on_disconnect(self) -> None:
        log.warning("disconnected from Discord; discord.py reconnects automatically")

    def commands_hash(self) -> str:
        payload = []
        for c in sorted(self.tree.get_commands(), key=lambda c: c.name):
            try:
                payload.append(c.to_dict(self.tree))  # discord.py >= 2.4
            except TypeError:
                payload.append(c.to_dict())  # type: ignore[call-arg]
        payload.append({"guild": self.config.guild_id})
        return hashlib.sha256(json.dumps(payload, sort_keys=True, default=str).encode()).hexdigest()

    async def sync_commands(self) -> None:
        """Only re-registers slash commands when they changed (sync is rate limited by Discord)."""
        h = self.commands_hash()
        marker = self.config.state_file.with_name(".commands-hash")
        try:
            old = marker.read_text().strip()
        except OSError:
            old = ""
        if self.sync_mode == "auto" and old == h:
            log.info("slash commands unchanged, no sync needed")
            return
        if self.config.guild_id:
            g = discord.Object(id=self.config.guild_id)
            self.tree.copy_global_to(guild=g)
            synced = await self.tree.sync(guild=g)
            log.info("synced %d commands to guild %s", len(synced), self.config.guild_id)
        else:
            synced = await self.tree.sync()
            log.info("synced %d global commands (can take up to an hour to show)", len(synced))
        try:
            marker.write_text(h)
        except OSError:
            pass

    def remember(self, s: Dict[str, Any]) -> None:
        g = self.known.setdefault(s["game_id"], {"name": s.get("game_name") or s["game_id"], "cheats": {}})
        if s.get("game_name"):
            g["name"] = s["game_name"]
        g["cheats"][s["cheat_id"]] = s.get("cheat_name") or s["cheat_id"]

    async def channel(self) -> discord.abc.Messageable:
        ch = self.get_channel(self.config.channel_id)
        if ch is None:
            ch = await self.fetch_channel(self.config.channel_id)
        return ch  # type: ignore[return-value]

    # ---- event feed --------------------------------------------------------------------------
    async def _poll(self) -> None:
        try:
            await self.poll_once()
        except ApiError as e:
            log.warning("poll: Worker error %s", e)
        except discord.Forbidden:
            log.error("poll: no permission in channel %s (needs View Channel, Send Messages, Embed Links)", self.config.channel_id)
        except Exception:
            log.exception("poll failed")  # keep the loop alive whatever happens

    async def poll_once(self, channel: Optional[Any] = None) -> int:
        """Fetches new events from the Worker and posts/edits messages. Returns the number of items handled."""
        ch = channel or await self.channel()
        handled = 0
        for _ in range(20):  # at most 20 pages per tick
            data = await self.api.events(self.local.cursor, 100)
            for item in data.get("items", []):
                try:
                    await self.handle_item(ch, item)
                except discord.HTTPException as e:
                    log.warning("could not render #%s: %s", item.get("state", {}).get("id"), e)
                handled += 1
            if data.get("cursor", self.local.cursor) != self.local.cursor:
                self.local.cursor = int(data["cursor"])
                self.local.save()
            if not data.get("more"):
                break
        return handled

    async def handle_item(self, ch: Any, item: Dict[str, Any]) -> None:
        st = item["state"]
        self.remember(st)
        sid = str(st["id"])
        local_mid = self.local.messages.get(sid)
        mid = st.get("message_id") or local_mid
        embed, view = report_embed(st), view_for(st)
        if mid:
            try:
                await ch.get_partial_message(int(mid)).edit(embed=embed, view=view)
                if not st.get("message_id"):
                    await self._save_message(st["id"], int(mid))
            except discord.NotFound:
                log.info("message for #%s was deleted; forgetting it", sid)
                self.local.messages.pop(sid, None)
                await self._save_message(st["id"], None)
                mid = None
        if not mid and item.get("has_broken") and st.get("status") == "open" and st.get("broken", 0) > 0:
            msg = await ch.send(embed=embed, view=view, allowed_mentions=NO_MENTIONS)
            mid = msg.id
            log.info("posted #%s %s/%s (%d broken)", sid, st["game_id"], st["cheat_id"], st.get("broken", 0))
            self.local.messages[sid] = str(mid)
            self.local.save()
            await self._save_message(st["id"], mid)
        if mid and "status" in item.get("types", []) and st.get("status") == "fixed":
            await ch.send(fixed_notice(st), allowed_mentions=NO_MENTIONS,
                          reference=discord.MessageReference(message_id=int(mid), channel_id=self.config.channel_id, fail_if_not_exists=False))

    async def _save_message(self, state_id: int, mid: Optional[int]) -> None:
        try:
            await self.api.set_message(state_id, mid)
        except ApiError as e:  # local state.json keeps the id; synced on the next edit
            log.warning("could not store message id for #%s in Worker: %s", state_id, e)

    # ---- weekly digest -----------------------------------------------------------------------
    def digest_due(self, now: dt.datetime) -> Optional[str]:
        local = now.astimezone(ZoneInfo(self.config.timezone))
        y, w, _ = local.isocalendar()
        week = f"{y}-W{w:02d}"
        if local.weekday() == self.config.digest_weekday and local.hour >= self.config.digest_hour and self.local.last_digest != week:
            return week
        return None

    async def _digest_tick(self) -> None:
        week = self.digest_due(dt.datetime.now(dt.timezone.utc))
        if not week:
            return
        try:
            await self.post_digest(week)
        except Exception:
            log.exception("digest failed")

    async def post_digest(self, week: str, channel: Optional[Any] = None) -> None:
        d = await self.api.digest(7)
        ch = channel or await self.channel()
        await ch.send(embed=digest_embed(d, week), allowed_mentions=NO_MENTIONS)
        self.local.last_digest = week
        self.local.save()
        log.info("posted weekly digest %s", week)


def game_choices(bot: VantaBot, current: str) -> List[app_commands.Choice[str]]:
    cur = current.lower()
    out = [app_commands.Choice(name=f"{v['name']} ({k})"[:100], value=k) for k, v in bot.known.items() if cur in k or cur in str(v["name"]).lower()]
    return out[:25]


def cheat_choices(bot: VantaBot, game: Optional[str], current: str) -> List[app_commands.Choice[str]]:
    cur = current.lower()
    cheats = bot.known.get(game or "", {}).get("cheats", {})
    out = [app_commands.Choice(name=f"{n} ({k})"[:100], value=k) for k, n in cheats.items() if cur in k or cur in str(n).lower()]
    return out[:25]
