# Supabase: database for community reports

Setup for admins: [docs/SETUP.md](../docs/SETUP.md) (step 2; Dutch: [docs/SETUP.nl.md](../docs/SETUP.nl.md)). Technical details below.

## Files

| File | Contents |
|---|---|
| `migrations/20260927120000_community_schema.sql` | schema `vanta`, tables, RLS policies, profile trigger on `auth.users` |
| `migrations/20260927120100_community_functions.sql` | validation, rate limits, score, RPCs for the app |
| `migrations/20260927120200_bot_api.sql` | RPCs for the bot (`service_role` only) |
| `migrations/20260928120000_game_requests_admin.sql` | game requests with votes, admin table, RPCs for the admin dashboard on the website |
| `supabase-setup.sql` | all migrations in one file for the SQL Editor; generate with `sh build-setup-sql.sh` |

Everything is idempotent and safe in an existing project: only the schema `vanta`, functions `public.vanta_*` and the
trigger `vanta_profile_sync` on `auth.users` (only reacts to Discord users and never blocks a sign-in).
The `vanta` schema is not exposed through the Data API; `anon` has no rights on it.

## Access

- **App** (publishable key + user JWT from Supabase Auth): `vanta_submit_report`, `vanta_withdraw_report`,
  `vanta_delete_my_account` (signed in), `vanta_community`, `vanta_count_usage` (also anonymous). Reporting and
  withdrawing run as `security invoker`, so under RLS: a user can only see and change their own reports, and
  banned users nothing.
- **Bot** (secret key = `service_role`): `vanta_bot_events`, `_state`, `_set_message`, `_set_status`, `_ban`, `_top`,
  `_cheat`, `_game`, `_stats`, `_digest`, `_cleanup`. Every function checks the role again itself.
- **Game requests**: `vanta_game_requests` (list, also anonymous), `vanta_request_game` (request/vote) and
  `vanta_unvote_game` (signed in). One vote per user per game (Steam appid); banned users cannot vote and their votes
  do not count. Limits: 30 vote actions per hour, 10 new requests per day per user.
- **Admin** (website `admin/`): `vanta_is_admin` and `vanta_admin_*` (reports, status, ban/unban, requests,
  statistics). Admin = Supabase user with a Discord identity (`auth.identities.provider_id`) listed in
  `vanta.admins`; that table is not readable through the API. Add an admin (SQL Editor):
  `insert into vanta.admins (discord_id) values ('<discord-id>');`
- Errors come back as an HTTP status with the error code as `message` (e.g. 429 `rate_limited`, detail
  `retry_after=N`; 403 `banned`; 400 `invalid_status`).

## Priority score

Per game, cheat and game version (fingerprint):

```
decay(r) = 0.5 ^ (age_days / 14)
base     = max(0, Σ decay(broken) − 0.5 · Σ decay(works))
score    = base · newest · spike · demand
  newest = 1.5 if this is the newest known game version, else 1
  spike  = 1 + min(1, early / 5) while the version is at most 7 days old; early = "broken" reports
           within 72 hours after the version was first seen
  demand = 1 + 0.1 · log10(1 + times enabled in the last 7 days)
```

Only reports from non-banned users count. After *fixed* / *can't reproduce* / *duplicate* only newer reports count
(for *fixed in version X* only from Vanta X or newer); closed reports have score 0.
A new "broken" report with Vanta ≥ the fix version reopens the report automatically.

## Testing (without Docker)

Required: PostgreSQL 15+ with pgTAP and `pg_prove`, [PostgREST](https://postgrest.org) and Python 3 with `aiohttp` and
`asyncpg`.

```
sh supabase/tests/idempotency.sh          # setup twice over existing tables + pgTAP (tests/database/)
sh supabase/tests/local-stack.sh up       # Postgres + PostgREST + fake Supabase Auth on http://127.0.0.1:54321
```

With the stack running:

```
VANTA_IT_SUPABASE_URL=http://127.0.0.1:54321 dotnet test tests/Vanta.Tests
VANTA_TEST_SUPABASE_URL=http://127.0.0.1:54321 python -m pytest bot/tests
Vanta.exe --account-selftest http://127.0.0.1:54321 sb_publishable_localtest
```

The stack uses the keys `sb_publishable_localtest` and `sb_secret_localtest`; the fake Auth signs in directly as a
Discord user (parameters `mock_discord_id`, `mock_deny`). `tests/supabase_stub.sql` mimics Supabase's roles and the
`auth` schema. Temporary files live in `supabase/.tmp/`.
