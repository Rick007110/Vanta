# Supabase: database voor community-meldingen

Installatie voor beheerders: [docs/SETUP.md](../docs/SETUP.md) (stap 2). Hier de technische details.

## Bestanden

| Bestand | Inhoud |
|---|---|
| `migrations/20260927120000_community_schema.sql` | schema `vanta`, tabellen, RLS-policies, profiel-trigger op `auth.users` |
| `migrations/20260927120100_community_functions.sql` | validatie, rate-limits, score, RPC's voor de app |
| `migrations/20260927120200_bot_api.sql` | RPC's voor de bot (alleen `service_role`) |
| `migrations/20260928120000_game_requests_admin.sql` | game-aanvragen met stemmen, beheerderstabel, RPC's voor het beheerdashboard op de website |
| `supabase-setup.sql` | alle migraties in één bestand voor de SQL Editor; maken met `sh build-setup-sql.sh` |

Alles is idempotent en veilig in een bestaand project: alleen het schema `vanta`, functies `public.vanta_*` en de
trigger `vanta_profile_sync` op `auth.users` (reageert alleen op Discord-gebruikers en blokkeert nooit een login).
Het schema `vanta` wordt niet via de Data API ontsloten; `anon` heeft er geen rechten.

## Toegang

- **App** (publishable key + gebruikers-JWT van Supabase Auth): `vanta_submit_report`, `vanta_withdraw_report`,
  `vanta_delete_my_account` (ingelogd), `vanta_community`, `vanta_count_usage` (ook anoniem). Melden en intrekken
  draaien als `security invoker`, dus onder RLS: een gebruiker kan alleen zijn eigen meldingen zien en wijzigen, en
  geblokkeerde gebruikers niets.
- **Bot** (secret key = `service_role`): `vanta_bot_events`, `_state`, `_set_message`, `_set_status`, `_ban`, `_top`,
  `_cheat`, `_game`, `_stats`, `_digest`, `_cleanup`. Elke functie controleert zelf nog eens de rol.
- **Game-aanvragen**: `vanta_game_requests` (lijst, ook anoniem), `vanta_request_game` (aanvragen/stemmen) en
  `vanta_unvote_game` (ingelogd). Eén stem per gebruiker per game (Steam-appid); geblokkeerde gebruikers kunnen niet
  stemmen en hun stemmen tellen niet mee. Limieten: 30 stemacties per uur, 10 nieuwe aanvragen per dag per gebruiker.
- **Beheer** (website `admin/`): `vanta_is_admin` en `vanta_admin_*` (meldingen, status, ban/unban, aanvragen,
  statistieken). Beheerder = Supabase-gebruiker met een Discord-identiteit (`auth.identities.provider_id`) die in
  `vanta.admins` staat; die tabel is niet leesbaar via de API. Beheerder toevoegen (SQL Editor):
  `insert into vanta.admins (discord_id) values ('<discord-id>');`
- Fouten komen terug als HTTP-status met de foutcode als `message` (bijv. 429 `rate_limited`, detail
  `retry_after=N`; 403 `banned`; 400 `invalid_status`).

## Prioriteitsscore

Per game, cheat en gameversie (vingerafdruk):

```
decay(r) = 0.5 ^ (leeftijd_dagen / 14)
basis    = max(0, Σ decay(werkt niet) − 0.5 · Σ decay(werkt))
score    = basis · nieuwste · piek · vraag
  nieuwste = 1.5 als dit de nieuwste bekende gameversie is, anders 1
  piek     = 1 + min(1, vroege / 5) zolang de versie hooguit 7 dagen oud is; vroege = "werkt niet"-meldingen
             binnen 72 uur na de eerste keer dat de versie gezien is
  vraag    = 1 + 0.1 · log10(1 + keren aangezet in de laatste 7 dagen)
```

Alleen meldingen van niet-geblokkeerde gebruikers tellen. Na *gefixt* / *niet reproduceerbaar* / *dubbel* tellen
alleen nieuwere meldingen (bij *gefixt in versie X* alleen van Vanta X of nieuwer); gesloten meldingen hebben score 0.
Een nieuwe "werkt niet"-melding met Vanta ≥ de fix-versie heropent de melding automatisch.

## Testen (zonder Docker)

Nodig: PostgreSQL 15+ met pgTAP en `pg_prove`, [PostgREST](https://postgrest.org) en Python 3 met `aiohttp` en
`asyncpg`.

```
sh supabase/tests/idempotency.sh          # setup 2x over bestaande tabellen + pgTAP (tests/database/)
sh supabase/tests/local-stack.sh up       # Postgres + PostgREST + nep-Supabase-Auth op http://127.0.0.1:54321
```

Met de stack draaiend:

```
VANTA_IT_SUPABASE_URL=http://127.0.0.1:54321 dotnet test tests/Vanta.Tests
VANTA_TEST_SUPABASE_URL=http://127.0.0.1:54321 python -m pytest bot/tests
Vanta.exe --account-selftest http://127.0.0.1:54321 sb_publishable_localtest
```

De stack gebruikt de keys `sb_publishable_localtest` en `sb_secret_localtest`; de nep-Auth logt direct in als
Discord-gebruiker (parameters `mock_discord_id`, `mock_deny`). `tests/supabase_stub.sql` bootst de rollen en het
`auth`-schema van Supabase na. Tijdelijke bestanden staan in `supabase/.tmp/`.
