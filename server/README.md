# Vanta community-backend (Cloudflare Worker + D1)

Ontvangt meldingen uit Vanta ("Werkt" / "Werkt niet"), regelt inloggen met Discord en levert de gegevens
aan de Discord-bot (Python, draait apart; zie `../bot`). Installatie: zie [`../docs/SETUP.md`](../docs/SETUP.md).

## Inloggen (desktop, loopback + PKCE)

1. Vanta opent een listener op `127.0.0.1:<willekeurige poort>`, maakt `state` + `code_verifier` en opent de browser op
   `GET /auth/start?port&state&challenge` (`challenge = base64url(sha256(verifier))`).
2. De Worker bewaart de aanvraag (10 min) en stuurt door naar Discord (`scope=identify`, `prompt=none`).
3. `GET /auth/callback`: code inwisselen, `users/@me` ophalen, Discord-token direct intrekken (wordt nooit opgeslagen),
   gebruiker aanmaken/bijwerken, eenmalige code (2 min, alleen de hash in D1) → redirect naar `http://127.0.0.1:<poort>/callback?code&state`.
4. Vanta: `POST /auth/token {code, verifier}` → `{token: "vt_…", user}`. De Worker bewaart alleen de SHA-256 van de
   token (180 dagen geldig). Vanta bewaart de token met Windows DPAPI in `%LOCALAPPDATA%\Vanta\account.bin`.

## Endpoints

| Methode | Pad | Auth | |
|---|---|---|---|
| GET | `/health` | – | status |
| GET | `/auth/start`, `/auth/callback` | – | inloggen (zie boven) |
| POST | `/auth/token` | – | eenmalige code + PKCE-verifier → sessietoken |
| POST | `/auth/logout` | sessie | token intrekken |
| GET / DELETE | `/me` | sessie | profiel / account + alle meldingen verwijderen |
| POST / DELETE | `/reports` | sessie | melding plaatsen (1 per gebruiker per cheat per gameversie) / intrekken |
| GET | `/reports/mine` | sessie | eigen meldingen |
| GET | `/community/:game?fingerprint=` | – | aantallen per cheat voor die gameversie |
| POST | `/usage` | – | opt-in anoniem gebruik (dagtotalen, geen gebruiker/IP) |
| GET | `/bot/events`, `/bot/state/:id`, `/bot/top`, `/bot/cheat`, `/bot/game`, `/bot/stats`, `/bot/digest` | bot-geheim | voor de bot |
| POST | `/bot/message`, `/bot/status`, `/bot/ban` | bot-geheim | voor de bot |

Bot-endpoints vereisen `Authorization: Bearer <BOT_API_SECRET>` (vergelijking in constante tijd, mislukte pogingen
rate-limited). Beheerderscontrole (wie mag "Gefixt" drukken) gebeurt in de bot met `ADMIN_IDS`.

Limieten (vast venster in D1): inloggen 20/10 min per IP, token 30/10 min, meldingen 30/uur per gebruiker en 60/uur
per IP, lezen 240/10 min per IP, gebruik 30/uur per IP. IP-adressen worden niet opgeslagen: alleen een HMAC met een
dagelijks wisselende sleutel, die na het venster verdwijnt.

## Prioriteitsscore

Per (game, cheat, gameversie), alleen meldingen die meetellen (niet-geblokkeerde gebruikers; na "Gefixt" alleen
nieuwere meldingen vanaf de fix-versie):

```
verval(m)  = 0,5 ^ (leeftijd_in_dagen / 14)              halveringstijd 14 dagen
B          = Σ verval over "werkt niet"-meldingen
W          = Σ verval over "werkt"-meldingen
basis      = max(0, B − 0,5 · W)
nieuwste   = 1,5 als dit de nieuwste bekende gameversie is, anders 1
piek       = 1 + min(1, vroege_meldingen / 5)             alleen als de gameversie ≤ 7 dagen oud is;
                                                          vroeg = binnen 72 uur na de eerste melding van die versie
vraag      = 1 + 0,1 · log10(1 + keer aangezet in 7 dagen)   (alleen als gebruikers anoniem gebruik delen)
score      = basis · nieuwste · piek · vraag              (afgerond op 2 decimalen; 0 als de status niet "open" is)
```

Een "werkt niet"-melding vanaf de fix-versie heropent een gefixte melding automatisch.

## Lokaal ontwikkelen en testen

```
npm install
npm test                 # 33 tests (Vitest in de Workers-runtime, D1 in-memory, Discord gemockt)
npm run typecheck
sh scripts/dev-local.sh  # Worker op http://127.0.0.1:8787 + nep-Discord op :8788, lokale D1 in .tmp/
```

Met `dev-local.sh` kun je zonder Discord-account inloggen (de nep-Discord keurt alles direct goed). Het
bot-geheim staat dan in `.tmp/.dev.vars`. Integratietests:

```
VANTA_IT_BACKEND=http://127.0.0.1:8787 dotnet test ../tests/Vanta.Tests --filter AccountIntegration
VANTA_TEST_API_URL=http://127.0.0.1:8787 VANTA_TEST_BOT_SECRET=<uit .tmp/.dev.vars> python -m pytest   (in ../bot)
Vanta.exe --account-selftest http://127.0.0.1:8787
```
