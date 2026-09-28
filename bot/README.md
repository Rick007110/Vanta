# Vanta Discord bot (Python)

Posts "broken" reports from Vanta to a Discord channel, updates existing messages when new reports come in, has
buttons for admins (Fixed / Can't reproduce / Duplicate / Reopen), slash commands
(`/top`, `/cheat`, `/game`, `/stats`, `/fixed`, `/ban`, `/digest`) and a weekly digest. All texts are in English.

- Python 3.10 or newer (tested with 3.10 and 3.13), discord.py 2.7, aiohttp, python-dotenv. No Supabase SDK needed:
  the bot calls the `public.vanta_bot_*` functions through the Supabase REST API (PostgREST).
- Startup file: `bot.py`. Settings via environment variables or a `.env` next to `bot.py` (see `.env.example`).
- All data lives in Supabase (schema `vanta`); the bot only keeps `state.json` (how far it has read the reports, plus
  message ids as a fallback) and `.commands-hash` in its own folder.
- Logs to stdout (visible in the panel console). Reconnecting and Discord rate limits are handled by discord.py;
  the Supabase client retries network errors, 5xx and 429 with increasing back-off. About every hour the bot cleans up
  expired rate-limit rows and events older than 30 days (`vanta_bot_cleanup`).

**`SUPABASE_SERVICE_ROLE_KEY` (secret key) is secret**: it bypasses Row Level Security. Only put it in the panel or in
`.env` on the server; never in Vanta, never in git, never paste it in Discord. If it leaks anyway: create a new secret
key under API Keys in Supabase and delete the old one.

Database setup: run `../supabase/supabase-setup.sql` in the Supabase SQL Editor.
Hosting on a Pterodactyl panel (Ferox): see `README-bot.txt` (Dutch: `LEESMIJ-bot.txt`) and
[`../docs/SETUP.md`](../docs/SETUP.md).

```
python bot.py              # start
python bot.py --check      # only check the settings + the connection to Supabase
python bot.py --sync       # re-register the slash commands
python bot.py --sync-only  # only register the slash commands and exit
```

Tests: `pip install -r requirements-dev.txt && python -m pytest`. Integration tests against a local Postgres +
PostgREST + fake Auth: `sh ../supabase/tests/local-stack.sh up` and then
`VANTA_TEST_SUPABASE_URL=http://127.0.0.1:54321 python -m pytest`. A `Dockerfile` is optional.
