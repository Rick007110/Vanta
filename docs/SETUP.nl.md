# Community-meldingen instellen

*English (primary): [SETUP.md](SETUP.md).*

Drie onderdelen:

1. **Supabase** (gratis plan is genoeg): inloggen met Discord (Supabase Auth), de database met meldingen en de
   logica (RLS + functies). SQL in de map `supabase/`.
2. **Discord-bot** (Python, op Ferox/Pterodactyl): plaatst meldingen in een kanaal. Map `bot/`.
3. **Vanta**: de knoppen in de app. Die worden pas actief als de Supabase-URL en de publishable key in Vanta staan
   (stap 7).

Volgorde: Discord-applicatie → Supabase → bot → Vanta.

> **Twee soorten Supabase-keys.** De **publishable key** (`sb_publishable_…`, vroeger "anon") is openbaar en hoort
> in Vanta. De **secret key** (`sb_secret_…`, vroeger "service_role") omzeilt alle beveiliging en hoort **alleen**
> bij de bot (paneel of `.env`). Nooit in Vanta, nooit in git, nooit in Discord.

---

## 1. Discord-applicatie

1. Ga naar <https://discord.com/developers/applications> → **New Application** → naam `Vanta`.
2. **OAuth2**:
   - noteer de **Client ID** en klik bij **Client Secret** op *Reset Secret* → kopiëren. Deel dit nooit; het gaat
     alleen naar Supabase (stap 2.3).
   - **Redirects** → *Add Redirect*: `https://<project-ref>.supabase.co/auth/v1/callback` (de precieze URL toont
     Supabase in stap 2.3 als *Callback URL*).
3. **Bot**:
   - *Reset Token* → kopiëren (= `DISCORD_BOT_TOKEN`). Deel dit nooit.
   - Privileged Gateway Intents: **allemaal uit laten** (niet nodig).
   - *Public Bot*: uit (dan kan alleen jij hem uitnodigen).
4. Bot uitnodigen: open deze URL (vervang `APP_ID` door de Application ID):

   `https://discord.com/oauth2/authorize?client_id=APP_ID&scope=bot+applications.commands&permissions=84992`

   Rechten: View Channel, Send Messages, Embed Links, Read Message History (nodig om op een melding te reageren
   met "gefixt"). Kies je server en bevestig.
5. In Discord: **Instellingen → Geavanceerd → Ontwikkelaarsmodus** aan. Daarna:
   - rechtsklik op het meldingen-kanaal → *Kanaal-ID kopiëren* (= `DISCORD_CHANNEL_ID`);
   - rechtsklik op je eigen naam → *Gebruikers-ID kopiëren* (= `ADMIN_IDS`);
   - rechtsklik op de servernaam → *Server-ID kopiëren* (= `DISCORD_GUILD_ID`, optioneel maar aangeraden:
     slash commands verschijnen dan meteen).

## 2. Supabase

Een bestaand project gebruiken mag: alles komt in een eigen schema `vanta`, functies die met `vanta_` beginnen en
één trigger `vanta_profile_sync` op `auth.users`. Eigen tabellen worden niet aangeraakt. Nieuw project? Kies een
EU-regio (bijv. Frankfurt), dat is het netst voor de privacy.

1. **Database klaarzetten.** Dashboard → jouw project → **SQL Editor** → *New query* → plak de volledige inhoud van
   [`supabase/supabase-setup.sql`](../supabase/supabase-setup.sql) → **Run**. Verwacht: *Success. No rows returned*.
   Opnieuw uitvoeren (bijv. na een update) mag altijd. Met de Supabase CLI kan het ook: `supabase link` en daarna
   `supabase db push` (de migraties staan in `supabase/migrations/`).
   Het schema `vanta` hoeft **niet** bij *Exposed schemas*: Vanta en de bot praten alleen via de `vanta_*`-functies.
2. **Gegevens kopiëren** (*Project Settings*):
   - **Project URL**: *Data API* (of de knop *Connect*), bijv. `https://abcd1234.supabase.co`;
   - **Publishable key**: *API Keys* → `sb_publishable_…` (voor Vanta; openbaar);
   - **Secret key**: *API Keys* → *Secret keys* → `sb_secret_…` (voor de bot; **geheim**). Oudere projecten hebben
     ook nog de "anon"- en "service_role"-keys; die werken ook, maar Supabase stopt er eind 2026 mee.
3. **Discord als inlogmethode.** *Authentication* → *Sign In / Providers* → **Discord** → aanzetten:
   - *Client ID* en *Client Secret* uit stap 1.2 plakken;
   - kopieer de getoonde *Callback URL* naar Discord → OAuth2 → Redirects (stap 1.2);
   - *Allow users without an email*: aanzetten (aangeraden). Supabase vraagt Discord altijd om het e-mailadres; dit
     is niet uit te zetten. Met deze optie kunnen ook spelers zonder geverifieerd e-mailadres inloggen.
   - Opslaan.
4. **Redirect voor Vanta.** *Authentication* → *URL Configuration* → *Redirect URLs* → *Add URL*:

   `http://127.0.0.1:*/callback*`

   Vanta ontvangt de login op een willekeurige vrije poort op de eigen pc (alleen `127.0.0.1`, niet vanaf het
   netwerk bereikbaar). *Site URL* maakt niet uit.

## 3. Welke waarden waar

| Naam | Waar | Waarde |
|---|---|---|
| Discord Client ID + Client Secret | **alleen** Supabase (Discord-provider) | Discord → OAuth2 |
| Project URL | Vanta **en** bot (`SUPABASE_URL`) | `https://<ref>.supabase.co` |
| Publishable key | **alleen** Vanta | `sb_publishable_…` (openbaar) |
| Secret key | **alleen** bot (`SUPABASE_SERVICE_ROLE_KEY`) | `sb_secret_…` (geheim) |
| `DISCORD_BOT_TOKEN` | bot | Bot-token (geheim) |
| `DISCORD_CHANNEL_ID` | bot | kanaal-ID |
| `ADMIN_IDS` | bot | jouw gebruikers-ID (meerdere: komma-gescheiden) |
| `DISCORD_GUILD_ID` | bot (optioneel) | server-ID |

Optioneel voor de bot: `POLL_SECONDS` (15), `DIGEST_WEEKDAY` (0 = maandag), `DIGEST_HOUR` (10),
`TIMEZONE` (Europe/Amsterdam), `LOG_LEVEL` (INFO). `SUPABASE_SECRET_KEY` mag als andere naam voor
`SUPABASE_SERVICE_ROLE_KEY`.

## 4. Bot lokaal proberen (optioneel)

In de map `bot` met Python 3.10+: `pip install -r requirements.txt`, `.env.example` kopiëren naar `.env`, invullen,
dan `python bot.py --check` (controleert instellingen, key en database) en `python bot.py`.

## 5. De bot op Ferox (Pterodactyl)

Kort stappenplan staat ook in [`bot/LEESMIJ-bot.txt`](../bot/LEESMIJ-bot.txt) (Engels: [`bot/README-bot.txt`](../bot/README-bot.txt)). De bot praat Engels in Discord.

1. **Server met een Python-egg.** In het paneel bij *Startup*: kies als Docker-image Python 3.11 of 3.12 als dat
   kan (3.10 werkt ook; ouder niet).
2. **Bestanden uploaden.** Nodig zijn: `bot.py`, de map `vanta_bot/`, `requirements.txt` en `.env.example`
   (tests, Dockerfile en README hoeven niet).
   - *File Manager*: zip uploaden met **Upload**, daarna rechtsklik op de zip → **Unarchive**. Controleer dat
     `bot.py` direct in `/home/container` staat (niet in een submap).
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
   - `config: … is missing` → een waarde in `.env` mist of klopt niet.
   - `… is the publishable key / anon key` → verkeerde Supabase-key; gebruik de secret key.
   - `Discord rejects the token` → `DISCORD_BOT_TOKEN` verkeerd (opnieuw kopiëren of resetten).
   - `function_missing` / *Could not find the function* → `supabase-setup.sql` is nog niet uitgevoerd (stap 2.1).
   - `permission_denied` → verkeerde key (publishable/anon in plaats van secret).
   - `unreachable` → `SUPABASE_URL` klopt niet of het Supabase-project is gepauzeerd (gratis projecten pauzeren na
     een week zonder verkeer; in het dashboard op *Restore* klikken).
   - `no permission in channel` → geef de bot in dat kanaal: Kanaal bekijken, Berichten versturen, Links insluiten,
     Berichtgeschiedenis lezen.
   - `ModuleNotFoundError: discord` → pakketten niet geïnstalleerd: controleer *Requirements file* en herstart.
   - Slash commands niet zichtbaar → vul `DISCORD_GUILD_ID` in, of wacht tot een uur (globaal); herstart Discord met Ctrl+R.
7. De bot maakt zelf `state.json` en `.commands-hash` aan in zijn map. Laat die staan; weggooien kan geen kwaad
   (hij leest dan de meldingen van de laatste 30 dagen opnieuw, zonder dubbele berichten).

**Knoppen en commando's.** Onder elke melding staan knoppen *Fixed* (vraagt de Vanta-versie), *Can't
reproduce*, *Duplicate* en *Reopen*; alleen gebruikers uit `ADMIN_IDS` kunnen ze gebruiken. `/fixed`, `/ban`
en `/digest` zijn alleen voor beheerders. `/cheat` toont beheerders ook wie er meldde (alleen voor jou zichtbaar).
Elke maandag om 10:00 plaatst de bot een weekoverzicht. `/ban` blokkeert meldingen van die Discord-gebruiker en logt
hem overal uit.

## 6. Updaten

Nieuwe botbestanden uploaden (niet je `.env`/`state.json` overschrijven), `supabase-setup.sql` opnieuw uitvoeren in
de SQL Editor, *Restart*. De SQL is idempotent: bestaande meldingen blijven staan.

## 7. Vanta koppelen

Zet de Project URL en de **publishable** key in `src/Vanta.Core/Branding.cs`:

```csharp
public const string SupabaseUrl = "https://abcd1234.supabase.co";
public const string SupabaseKey = "sb_publishable_…";
```

en bouw een nieuwe release. Tot die tijd zijn de accountknoppen verborgen. Vanta weigert een secret/service_role-key
(dan blijven de knoppen uit en staat er een waarschuwing in het log). Testen zonder nieuwe release kan per pc met
`"supabaseUrl"` en `"supabaseKey"` in `%LOCALAPPDATA%\Vanta\settings.json`, of met de omgevingsvariabelen
`VANTA_SUPABASE_URL` en `VANTA_SUPABASE_KEY`.

Controle op een pc: `Vanta.exe --account-selftest` test de versleutelde opslag (DPAPI), de login-listener en een
gesimuleerde Supabase-login. Met `--account-selftest <url> <key>` doorloopt hij ook inloggen, melden, token vernieuwen
en account verwijderen, maar alleen tegen de lokale teststack (`sh supabase/tests/local-stack.sh up`, met nep-Discord);
tegen het echte project test je via de knop in Vanta.

In Vanta: **Instellingen → Account → Inloggen met Discord**. De browser opent de Discord-toestemming (via Supabase);
daarna kun je het tabblad sluiten. Rechtsklik op een cheat → *Werkt niet* opent een venster voor een korte opmerking;
*Werkt* wordt direct gedeeld; *Niet getest* / *Standaard* trekt je melding in; *Melden…* opent het venster altijd.
Onder **Notities** staat per cheat wat de community meldt. *Account verwijderen* wist het Supabase-account met alle
meldingen.

> **Let op bij een gedeeld project.** Gebruik je hetzelfde Supabase-project ook voor andere apps, dan verwijdert
> *Account verwijderen* in Vanta het hele Supabase-account van die gebruiker (dus ook voor die andere apps), en kan
> iedereen met een Discord-account een Supabase-gebruiker aanmaken via de Discord-login. Een apart project voor Vanta
> is daarom het eenvoudigst.

## 8. Kosten en limieten

Het gratis plan van Supabase (500 MB database, 50.000 maandelijks actieve gebruikers) is ruim genoeg. Gratis
projecten worden gepauzeerd na een week zonder verkeer; de bot vraagt elke 15 seconden nieuwe meldingen op en houdt
het project daardoor actief. De bot ruimt elk uur verlopen rate-limit-gegevens (met de IP-hashes) en bot-gebeurtenissen ouder dan 30 dagen op
(`vanta_bot_cleanup`). Limieten in de database: 30 meldingen per uur per account; community-opvragen 240 per
10 minuten en gebruikstellingen 30 per uur per IP-hash.
