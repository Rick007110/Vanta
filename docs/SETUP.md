# Community-meldingen instellen

Drie onderdelen:

1. **Worker** (Cloudflare, gratis plan): inloggen met Discord, meldingen, database (D1). Map `server/`.
2. **Discord-bot** (Python, op Ferox/Pterodactyl): plaatst meldingen in een kanaal. Map `bot/`.
3. **Vanta**: de knoppen in de app. Die worden pas actief als de Worker-URL in Vanta staat (stap 6).

Volgorde: Discord-applicatie aanmaken → Worker → bot → Vanta.

---

## 1. Discord-applicatie

1. Ga naar <https://discord.com/developers/applications> → **New Application** → naam `Vanta`.
2. **General Information**: noteer de **Application ID** (= `DISCORD_CLIENT_ID`).
3. **OAuth2**:
   - **Client Secret** → *Reset Secret* → kopiëren (= `DISCORD_CLIENT_SECRET`). Deel dit nooit.
   - **Redirects** → *Add Redirect*: `https://vanta-api.<jouw-subdomein>.workers.dev/auth/callback`
     (de precieze URL krijg je in stap 2.6; je kunt dit dan invullen).
4. **Bot**:
   - *Reset Token* → kopiëren (= `DISCORD_BOT_TOKEN`). Deel dit nooit.
   - Privileged Gateway Intents: **allemaal uit laten** (niet nodig).
   - *Public Bot*: uit (dan kan alleen jij hem uitnodigen).
5. Bot uitnodigen: open deze URL (vervang `APP_ID`):

   `https://discord.com/oauth2/authorize?client_id=APP_ID&scope=bot+applications.commands&permissions=84992`

   Rechten: View Channel, Send Messages, Embed Links, Read Message History (nodig om op een melding te reageren
   met "gefixt"). Kies je server en bevestig.
6. In Discord: **Instellingen → Geavanceerd → Ontwikkelaarsmodus** aan. Daarna:
   - rechtsklik op het meldingen-kanaal → *Kanaal-ID kopiëren* (= `DISCORD_CHANNEL_ID`);
   - rechtsklik op je eigen naam → *Gebruikers-ID kopiëren* (= `ADMIN_IDS`);
   - rechtsklik op de servernaam → *Server-ID kopiëren* (= `DISCORD_GUILD_ID`, optioneel maar aangeraden:
     slash commands verschijnen dan meteen).

## 2. Cloudflare Worker

Nodig: een Cloudflare-account en Node.js 20+ (<https://nodejs.org>, LTS). In een terminal in de map `server`:

```
npm install
npx wrangler login
```

`wrangler login` opent de browser. Liever een API-token (bijv. voor automatisch deployen)? Maak er een op
<https://dash.cloudflare.com/profile/api-tokens> → *Create Token* → *Custom token* met deze rechten:

| Soort | Recht | Niveau |
|---|---|---|
| Account | Workers Scripts | Edit |
| Account | D1 | Edit |
| Account | Account Settings | Read |
| User | User Details | Read |
| Zone | Workers Routes | Edit (alleen als je later een eigen domein gebruikt) |

Zet dan in de terminal `CLOUDFLARE_API_TOKEN` en `CLOUDFLARE_ACCOUNT_ID` (Account ID staat rechts op het
Workers-overzicht in het dashboard). PowerShell: `$env:CLOUDFLARE_API_TOKEN="…"; $env:CLOUDFLARE_ACCOUNT_ID="…"`.

1. Database maken:
   ```
   npx wrangler d1 create vanta
   ```
   Kopieer de `database_id` uit de uitvoer naar `wrangler.toml` (vervang `00000000-…`).
2. Tabellen aanmaken:
   ```
   npx wrangler d1 migrations apply vanta --remote
   ```
3. In `wrangler.toml` onder `[vars]`: `DISCORD_CLIENT_ID = "<Application ID>"`.
4. Geheimen instellen (elk commando vraagt om de waarde; plakken + Enter):
   ```
   npx wrangler secret put DISCORD_CLIENT_SECRET
   npx wrangler secret put HASH_SECRET
   npx wrangler secret put BOT_API_SECRET
   ```
   `HASH_SECRET` en `BOT_API_SECRET` zijn zelfgekozen willekeurige strings van 64 tekens. Maken in PowerShell:
   ```
   $b = New-Object byte[] 32; (New-Object Security.Cryptography.RNGCryptoServiceProvider).GetBytes($b); -join ($b | ForEach-Object { $_.ToString('x2') })
   ```
   Bewaar `BOT_API_SECRET`: de bot heeft precies dezelfde waarde nodig.
5. Publiceren:
   ```
   npx wrangler deploy
   ```
6. De uitvoer toont de URL, bijv. `https://vanta-api.<jouw-subdomein>.workers.dev`. Zet die in `wrangler.toml` als
   `PUBLIC_URL = "https://vanta-api.<jouw-subdomein>.workers.dev"` (zonder `/` op het eind), voeg
   `…/auth/callback` toe als Redirect in de Discord-applicatie (stap 1.3) en publiceer opnieuw met `npx wrangler deploy`.
7. Controle: open `https://vanta-api.<jouw-subdomein>.workers.dev/health` → `{"ok":true,"service":"vanta-api"}`.

## 3. Welke waarden waar

| Naam | Waar | Waarde |
|---|---|---|
| `DISCORD_CLIENT_ID` | Worker (`wrangler.toml` `[vars]`) | Application ID |
| `PUBLIC_URL` | Worker (`wrangler.toml` `[vars]`) | `https://vanta-api.<subdomein>.workers.dev` |
| `database_id` | Worker (`wrangler.toml`) | uit `wrangler d1 create` |
| `DISCORD_CLIENT_SECRET` | Worker (secret) | OAuth2 Client Secret |
| `HASH_SECRET` | Worker (secret) | willekeurig, 64 tekens |
| `BOT_API_SECRET` | Worker (secret) **en** bot | willekeurig, 64 tekens, beide gelijk |
| `DISCORD_BOT_TOKEN` | bot | Bot-token |
| `DISCORD_CHANNEL_ID` | bot | kanaal-ID |
| `ADMIN_IDS` | bot | jouw gebruikers-ID (meerdere: komma-gescheiden) |
| `DISCORD_GUILD_ID` | bot (optioneel) | server-ID |
| `VANTA_API_URL` | bot | dezelfde URL als `PUBLIC_URL` |
| `CLOUDFLARE_API_TOKEN`, `CLOUDFLARE_ACCOUNT_ID` | alleen je eigen terminal / CI | alleen als je geen `wrangler login` gebruikt |

Optioneel voor de bot: `POLL_SECONDS` (15), `DIGEST_WEEKDAY` (0 = maandag), `DIGEST_HOUR` (10),
`TIMEZONE` (Europe/Amsterdam), `LOG_LEVEL` (INFO).

## 4. Bot lokaal proberen (optioneel)

In de map `bot` met Python 3.10+: `pip install -r requirements.txt`, `.env.example` kopiëren naar `.env`, invullen,
dan `python bot.py --check` (controleert instellingen + Worker) en `python bot.py`.

## 5. De bot op Ferox (Pterodactyl)

1. **Server met een Python-egg.** In het paneel bij *Startup*: kies als Docker-image Python 3.11 of 3.12 als dat
   kan (3.10 werkt ook; ouder niet).
2. **Bestanden uploaden.** Nodig zijn: `bot.py`, de map `vanta_bot/`, `requirements.txt` en `.env.example`
   (tests, Dockerfile en README hoeven niet).
   - *File Manager*: maak lokaal een zip van deze bestanden, klik **Upload**, daarna rechtsklik op de zip →
     **Unarchive**. Controleer dat `bot.py` direct in `/home/container` staat (niet in een submap).
   - Of via **SFTP** (gegevens onder *Settings → SFTP Details*; wachtwoord = je paneelwachtwoord), bijvoorbeeld
     met WinSCP of FileZilla.
3. **Startinstellingen** (tab *Startup*):
   - *App py file* / *Startup file*: `bot.py`
   - *Requirements file*: `requirements.txt` (de egg installeert de pakketten bij elke start)
   - *Git Repo Address* / *Auto Update*: leeg / uit
   - *Additional Python packages*: leeg
4. **Instellingen invullen.** Het eenvoudigst: in de *File Manager* **New File** → naam `.env` → de inhoud van
   `.env.example` plakken en invullen (zie tabel in stap 3) → opslaan. Heeft het paneel eigen velden bij *Startup*
   voor variabelen, dan mag het ook daar; die gaan voor op `.env`.
5. **Start** → tab **Console**. Goed gaat het als je ziet:
   ```
   vanta: Vanta bot 0.1.0, Python 3.x, discord.py 2.7.1
   vanta.bot: synced 7 commands to guild …
   vanta.bot: logged in as Vanta#1234 (…); 1 guild(s)
   ```
   Typ in Discord `/stats` om te testen.
6. **Wat de foutmeldingen betekenen**
   - `config: … ontbreekt` → een waarde in `.env` mist of klopt niet.
   - `Discord weigert de token` → `DISCORD_BOT_TOKEN` verkeerd (opnieuw kopiëren of resetten).
   - `Worker error 401` → `BOT_API_SECRET` is anders dan op de Worker; `503` → niet ingesteld op de Worker.
   - `no permission in channel` → geef de bot in dat kanaal: Kanaal bekijken, Berichten versturen, Links insluiten,
     Berichtgeschiedenis lezen.
   - `ModuleNotFoundError: discord` → pakketten niet geïnstalleerd: controleer *Requirements file* en herstart.
   - Slash commands niet zichtbaar → vul `DISCORD_GUILD_ID` in, of wacht tot een uur (globaal); herstart Discord met Ctrl+R.
7. De bot maakt zelf `state.json` en `.commands-hash` aan in zijn map. Laat die staan; weggooien kan geen kwaad
   (hij leest dan de meldingen van de laatste 30 dagen opnieuw, zonder dubbele berichten).

**Knoppen en commando's.** Onder elke melding staan knoppen *Gefixt* (vraagt de Vanta-versie), *Niet
reproduceerbaar*, *Dubbel* en *Heropenen*; alleen gebruikers uit `ADMIN_IDS` kunnen ze gebruiken. `/fixed`, `/ban`
en `/digest` zijn alleen voor beheerders. `/cheat` toont beheerders ook wie er meldde (alleen voor jou zichtbaar).
Elke maandag om 10:00 plaatst de bot een weekoverzicht.

## 6. Vanta koppelen

Zet de Worker-URL in `src/Vanta.Core/Branding.cs` (`BackendUrl = "https://vanta-api.<subdomein>.workers.dev"`)
en bouw een nieuwe release. Tot die tijd zijn de accountknoppen verborgen. Testen zonder nieuwe release kan per pc
met `"backendUrl": "https://…"` in `%LOCALAPPDATA%\Vanta\settings.json` of de omgevingsvariabele `VANTA_BACKEND_URL`.
Controle op een pc: `Vanta.exe --account-selftest` test de versleutelde opslag (DPAPI) en de login-listener. Met een
URL erachter doorloopt hij ook inloggen, melden en verwijderen, maar alleen tegen de lokale testserver met nep-Discord
(`server/scripts/dev-local.sh`); tegen de echte Worker test je via de knop in Vanta.

In Vanta: **Instellingen → Account → Inloggen met Discord**. Daarna: rechtsklik op een cheat → *Werkt niet* opent een
venster voor een korte opmerking; *Werkt* wordt direct gedeeld; *Niet getest* / *Standaard* trekt je melding in;
*Melden…* opent het venster altijd. Onder **Notities** staat per cheat wat de community meldt.

## 7. Kosten en limieten

Het gratis plan van Cloudflare (100.000 verzoeken per dag, D1 5 GB) is ruim genoeg. De Worker ruimt elk uur
verlopen sessies, oude inlogpogingen en rate-limit-gegevens op.
