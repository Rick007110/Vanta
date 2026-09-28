from __future__ import annotations

import logging
import re
from typing import TYPE_CHECKING, List, Optional

import discord
from discord import app_commands

from .api import ApiError
from .render import clean, game_embed, report_embed, stats_embed, top_embed

if TYPE_CHECKING:
    from .client import VantaBot

log = logging.getLogger("vanta.commands")
ID_RE = re.compile(r"^[a-z0-9][a-z0-9_\-]{0,63}$")
VERSION_RE = re.compile(r"^[0-9A-Za-z][0-9A-Za-z.\-+]{0,31}$")
ERRORS = {"invalid_game": "Unknown or invalid game id.", "invalid_cheat": "Invalid cheat id.", "not_found": "Nothing found.",
          "user_not_found": "This user has never signed in to Vanta.", "unreachable": "The Vanta server is unreachable."}


def register(bot: "VantaBot") -> None:
    from .client import cheat_choices, game_choices

    tree = bot.tree

    def admin_only():
        async def pred(i: discord.Interaction) -> bool:
            if not bot.config.is_admin(i.user.id):
                raise app_commands.CheckFailure("admin")
            return True
        return app_commands.check(pred)

    async def game_ac(i: discord.Interaction, current: str) -> List[app_commands.Choice[str]]:
        return game_choices(bot, current)

    async def cheat_ac(i: discord.Interaction, current: str) -> List[app_commands.Choice[str]]:
        return cheat_choices(bot, getattr(i.namespace, "game", None), current)

    def check_id(v: str, what: str) -> str:
        v = v.strip().lower()
        if not ID_RE.match(v):
            raise app_commands.AppCommandError(f"Invalid {what} id: use lowercase letters, digits, - or _.")
        return v

    @tree.command(name="top", description="Broken cheats with the highest priority")
    @app_commands.describe(game="Only this game (id)", count="Number of results (1-25)")
    @app_commands.autocomplete(game=game_ac)
    async def top(i: discord.Interaction, game: Optional[str] = None, count: app_commands.Range[int, 1, 25] = 10) -> None:
        await i.response.defer()
        g = check_id(game, "game") if game else None
        items = (await bot.api.top(g, count))["items"]
        for s in items:
            bot.remember(s)
        await i.followup.send(embed=top_embed(items, bot.known.get(g, {}).get("name", g) if g else None))

    @tree.command(name="cheat", description="Details of a cheat (admins also see the reporters)")
    @app_commands.describe(game="Game id", cheat="Cheat id")
    @app_commands.autocomplete(game=game_ac, cheat=cheat_ac)
    async def cheat(i: discord.Interaction, game: str, cheat: str) -> None:
        admin = bot.config.is_admin(i.user.id)
        await i.response.defer(ephemeral=admin)
        items = (await bot.api.cheat(check_id(game, "game"), check_id(cheat, "cheat")))["items"]
        if not items:
            await i.followup.send("No reports for this cheat yet.", ephemeral=True)
            return
        await i.followup.send(embeds=[report_embed(s, reporters=admin) for s in items[:3]], ephemeral=admin)

    @tree.command(name="game", description="Overview of a game")
    @app_commands.describe(game="Game id")
    @app_commands.autocomplete(game=game_ac)
    async def game_cmd(i: discord.Interaction, game: str) -> None:
        await i.response.defer()
        g = check_id(game, "game")
        d = await bot.api.game(g)
        for s in d["items"]:
            bot.remember(s)
        await i.followup.send(embed=game_embed(g, d["items"], d.get("usage7d", [])))

    @tree.command(name="stats", description="General Vanta statistics")
    async def stats(i: discord.Interaction) -> None:
        await i.response.defer()
        await i.followup.send(embed=stats_embed(await bot.api.stats()))

    @tree.command(name="fixed", description="Mark a cheat as fixed (admins)")
    @app_commands.describe(game="Game id", cheat="Cheat id", version="Vanta version with the fix, e.g. 0.2.3", fingerprint="Only this game version (optional)")
    @app_commands.autocomplete(game=game_ac, cheat=cheat_ac)
    @app_commands.default_permissions(manage_guild=True)
    @admin_only()
    async def fixed(i: discord.Interaction, game: str, cheat: str, version: Optional[str] = None, fingerprint: Optional[str] = None) -> None:
        v = version.strip().lstrip("vV") if version else None
        if v and not VERSION_RE.match(v):
            await i.response.send_message("Invalid version. Example: 0.2.3", ephemeral=True)
            return
        await i.response.defer(ephemeral=True)
        res = await bot.api.set_status("fixed", game_id=check_id(game, "game"), cheat_id=check_id(cheat, "cheat"),
                                       fingerprint=fingerprint.strip() if fingerprint else None, fixed_in_version=v)
        n = len(res.get("updated", []))
        log.info("%s marked %s/%s fixed (%s), %d state(s)", i.user.id, game, cheat, v, n)
        await i.followup.send(f"✅ {n} report(s) marked as fixed" + (f" in {clean(v, 32)}" if v else "") + ". The messages will be updated shortly.", ephemeral=True)

    @tree.command(name="ban", description="Block (or unblock) a user from reporting (admins)")
    @app_commands.describe(user="Discord user (or paste a user id)", unban="Lift the block")
    @app_commands.default_permissions(manage_guild=True)
    @admin_only()
    async def ban(i: discord.Interaction, user: discord.User, unban: bool = False) -> None:
        await i.response.defer(ephemeral=True)
        res = await bot.api.ban(user.id, banned=not unban)
        log.info("%s %s user %s", i.user.id, "unbanned" if unban else "banned", user.id)
        verb = "unblocked" if unban else "blocked; their reports no longer count"
        await i.followup.send(f"{clean(res.get('username'), 64)} is {verb}.", ephemeral=True)

    @tree.command(name="digest", description="Post the weekly digest now (admins)")
    @app_commands.default_permissions(manage_guild=True)
    @admin_only()
    async def digest(i: discord.Interaction) -> None:
        await i.response.defer(ephemeral=True)
        import datetime as dt
        y, w, _ = dt.date.today().isocalendar()
        await bot.post_digest(f"{y}-W{w:02d}")
        await i.followup.send("Weekly digest posted.", ephemeral=True)

    @tree.error
    async def on_error(i: discord.Interaction, error: app_commands.AppCommandError) -> None:
        orig = getattr(error, "original", error)
        if isinstance(error, app_commands.CheckFailure):
            msg = "Only admins can use this command."
        elif isinstance(orig, ApiError):
            msg = ERRORS.get(orig.code, f"The server returned an error: {orig.code}")
            if orig.status >= 500 or orig.status == 0:
                log.warning("command /%s: %s", getattr(i.command, "name", "?"), orig)
        elif type(error) is app_commands.AppCommandError:
            msg = str(error)
        else:
            log.exception("command /%s failed", getattr(i.command, "name", "?"), exc_info=orig)
            msg = "Something went wrong."
        try:
            if i.response.is_done():
                await i.followup.send(msg, ephemeral=True)
            else:
                await i.response.send_message(msg, ephemeral=True)
        except discord.HTTPException:
            pass
