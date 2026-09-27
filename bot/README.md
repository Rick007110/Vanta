# Vanta Discord-bot (Python)

Plaatst "werkt niet"-meldingen uit Vanta in een Discord-kanaal, werkt bestaande berichten bij bij nieuwe meldingen,
heeft knoppen voor beheerders (Gefixt / Niet reproduceerbaar / Dubbel / Heropenen), slash commands
(`/top`, `/cheat`, `/game`, `/stats`, `/fixed`, `/ban`, `/digest`) en een wekelijks overzicht.

- Python 3.10 of nieuwer (getest met 3.10 en 3.13), discord.py 2.7, aiohttp, python-dotenv.
- Startbestand: `bot.py`. Instellingen via omgevingsvariabelen of een `.env` naast `bot.py` (zie `.env.example`).
- Alle gegevens staan in de Worker; de bot bewaart alleen `state.json` (tot waar hij de meldingen gelezen heeft,
  plus berichtnummers als reserve) en `.commands-hash` in zijn eigen map.
- Logt naar stdout (zichtbaar in de console van het paneel). Herverbinden en Discord-rate-limits regelt discord.py;
  de Worker-client probeert netwerkfouten, 5xx en 429 opnieuw met oplopende wachttijd.

Hosting op een Pterodactyl-paneel (Ferox): zie [`../docs/SETUP.md`](../docs/SETUP.md#5-de-bot-op-ferox-pterodactyl).

```
python bot.py              # starten
python bot.py --check      # alleen instellingen + verbinding met de Worker controleren
python bot.py --sync       # slash commands opnieuw registreren
python bot.py --sync-only  # alleen slash commands registreren en stoppen
```

Tests: `pip install -r requirements-dev.txt && python -m pytest` (integratietests tegen een lokale Worker: zie
`tests/test_integration.py`). Een `Dockerfile` is optioneel en alleen nodig buiten Pterodactyl.
