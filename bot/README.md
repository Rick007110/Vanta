# Vanta Discord-bot (Python)

Plaatst "werkt niet"-meldingen uit Vanta in een Discord-kanaal, werkt bestaande berichten bij bij nieuwe meldingen,
heeft knoppen voor beheerders (Gefixt / Niet reproduceerbaar / Dubbel / Heropenen), slash commands
(`/top`, `/cheat`, `/game`, `/stats`, `/fixed`, `/ban`, `/digest`) en een wekelijks overzicht.

- Python 3.10 of nieuwer (getest met 3.10 en 3.13), discord.py 2.7, aiohttp, python-dotenv. Geen Supabase-SDK nodig:
  de bot roept de functies `public.vanta_bot_*` aan via de REST-API van Supabase (PostgREST).
- Startbestand: `bot.py`. Instellingen via omgevingsvariabelen of een `.env` naast `bot.py` (zie `.env.example`).
- Alle gegevens staan in Supabase (schema `vanta`); de bot bewaart alleen `state.json` (tot waar hij de meldingen
  gelezen heeft, plus berichtnummers als reserve) en `.commands-hash` in zijn eigen map.
- Logt naar stdout (zichtbaar in de console van het paneel). Herverbinden en Discord-rate-limits regelt discord.py;
  de Supabase-client probeert netwerkfouten, 5xx en 429 opnieuw met oplopende wachttijd. Ongeveer elk uur ruimt de
  bot verlopen rate-limit-regels en events ouder dan 30 dagen op (`vanta_bot_cleanup`).

**`SUPABASE_SERVICE_ROLE_KEY` (secret key) is geheim**: die omzeilt Row Level Security. Alleen in het paneel of in
`.env` op de server zetten; nooit in Vanta, nooit in git, nooit in Discord plakken. Lekt hij toch: in Supabase bij
API Keys een nieuwe secret key maken en de oude verwijderen.

Database klaarzetten: `../supabase/supabase-setup.sql` in de SQL Editor van Supabase uitvoeren.
Hosting op een Pterodactyl-paneel (Ferox): zie [`../docs/SETUP.md`](../docs/SETUP.md).

```
python bot.py              # starten
python bot.py --check      # alleen instellingen + verbinding met Supabase controleren
python bot.py --sync       # slash commands opnieuw registreren
python bot.py --sync-only  # alleen slash commands registreren en stoppen
```

Tests: `pip install -r requirements-dev.txt && python -m pytest`. Integratietests tegen een lokale Postgres +
PostgREST + nep-Auth: `sh ../supabase/tests/local-stack.sh up` en dan
`VANTA_TEST_SUPABASE_URL=http://127.0.0.1:54321 python -m pytest`. Een `Dockerfile` is optioneel.
