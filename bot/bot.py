#!/usr/bin/env python3
"""Vanta Discord bot. Start: python bot.py   (Pterodactyl: startup file = bot.py)

Options:
  --check      only check the configuration and the connection to Supabase, then exit
  --sync       force re-registering the slash commands at startup
  --sync-only  register the slash commands and exit
"""
from __future__ import annotations

import sys

if sys.version_info < (3, 10):
    sys.exit("Python 3.10 of nieuwer is nodig (dit is %d.%d). Kies een nieuwere Docker-image in het paneel." % sys.version_info[:2])

import asyncio
import logging
from pathlib import Path

BOT_DIR = Path(__file__).resolve().parent
sys.path.insert(0, str(BOT_DIR))

from dotenv import load_dotenv  # noqa: E402

load_dotenv(BOT_DIR / ".env", override=False)  # panel variables win over .env

import discord  # noqa: E402

from vanta_bot import __version__  # noqa: E402
from vanta_bot.api import ApiError, VantaApi  # noqa: E402
from vanta_bot.config import Config, ConfigError  # noqa: E402

log = logging.getLogger("vanta")


def setup_logging(level: str) -> None:
    handler = logging.StreamHandler(sys.stdout)
    discord.utils.setup_logging(handler=handler, level=getattr(logging, level, logging.INFO), root=True)
    logging.getLogger("discord.gateway").setLevel(logging.WARNING if level != "DEBUG" else logging.DEBUG)


HINTS = {
    "function_missing": "de Vanta-functies bestaan niet: voer supabase-setup.sql uit in de SQL Editor van Supabase",
    "permission_denied": "geen rechten: gebruik de secret/service_role key, niet de publishable/anon key",
    "bad_key": "SUPABASE_SERVICE_ROLE_KEY wordt geweigerd: kopieer de secret key opnieuw",
    "forbidden": "de key is geen service_role/secret key",
    "unreachable": "SUPABASE_URL is niet bereikbaar (typefout, of het project is gepauzeerd?)",
}


async def check(cfg: Config) -> int:
    async with VantaApi(cfg.supabase_url, cfg.supabase_key, attempts=1) as api:
        try:
            st = await api.stats()
        except ApiError as e:
            hint = HINTS.get(e.code, "")
            log.error("Supabase-check mislukt: %s %s", e, f"({hint})" if hint else "")
            return 1
    log.info("Supabase OK: %s gebruikers, %s meldingen", st.get("users"), st.get("reports"))
    return 0


async def main(argv: list) -> int:
    try:
        cfg = Config.from_env()
    except ConfigError as e:
        setup_logging("INFO")
        for p in e.problems:
            log.error("config: %s", p)
        log.error("Vul de variabelen in (paneel -> Startup, of een .env naast bot.py). Zie .env.example.")
        return 2
    setup_logging(cfg.log_level)
    log.info("Vanta bot %s, Python %s, discord.py %s", __version__, sys.version.split()[0], discord.__version__)
    if "--check" in argv:
        return await check(cfg)
    from vanta_bot.client import VantaBot

    discord.VoiceClient.warn_nacl = False  # no voice features: silence the PyNaCl/davey warnings
    if hasattr(discord.VoiceClient, "warn_dave"):
        discord.VoiceClient.warn_dave = False
    mode = "only" if "--sync-only" in argv else "force" if "--sync" in argv else "auto"
    bot = VantaBot(cfg, sync_mode=mode)
    if sys.platform != "win32":
        import signal

        asyncio.get_running_loop().add_signal_handler(signal.SIGTERM, lambda: asyncio.ensure_future(bot.close()))
    try:
        async with bot:
            await bot.start(cfg.token, reconnect=True)
    except discord.LoginFailure:
        log.error("Discord weigert de token: controleer DISCORD_BOT_TOKEN (Developer Portal -> Bot -> Reset Token).")
        return 3
    except discord.PrivilegedIntentsRequired:
        log.error("Deze bot heeft geen privileged intents nodig; controleer de code.")
        return 3
    return 0


if __name__ == "__main__":
    try:
        sys.exit(asyncio.run(main(sys.argv[1:])))
    except KeyboardInterrupt:
        log.info("gestopt")
