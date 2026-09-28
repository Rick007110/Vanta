# Setting up community reports

*Nederlandse versie: [SETUP.nl.md](SETUP.nl.md).*

Three parts:

1. **Supabase** (the free plan is enough): sign-in with Discord (Supabase Auth), the database with reports and the
   logic (RLS + functions). SQL in the `supabase/` folder.
2. **Discord bot** (Python, on Ferox/Pterodactyl): posts reports in a channel. Folder `bot/`.
3. **Vanta**: the buttons in the app. They only become active once the Supabase URL and the publishable key are in Vanta
   (step 7).

Order: Discord application → Supabase → bot → Vanta.

> **Two kinds of Supabase keys.** The **publishable key** (`sb_publishable_…`, formerly "anon") is public and belongs
> in Vanta. The **secret key** (`sb_secret_…`, formerly "service_role") bypasses all security and belongs **only**
> with the bot (panel or `.env`). Never in Vanta, never in git, never in Discord.

---

## 1. Discord application

1. Go to <https://discord.com/developers/applications> → **New Application** → name `Vanta`.
2. **OAuth2**:
   - note the **Client ID** and click *Reset Secret* at **Client Secret** → copy. Never share it; it only goes
     to Supabase (step 2.3).
   - **Redirects** → *Add Redirect*: `https://<project-ref>.supabase.co/auth/v1/callback` (Supabase shows the exact
     URL in step 2.3 as *Callback URL*).
3. **Bot**:
   - *Reset Token* → copy (= `DISCORD_BOT_TOKEN`). Never share it.
   - Privileged Gateway Intents: **leave all off** (not needed).
   - *Public Bot*: off (then only you can invite it).
4. Invite the bot: open this URL (replace `APP_ID` with the Application ID):

   `https://discord.com/oauth2/authorize?client_id=APP_ID&scope=bot+applications.commands&permissions=84992`

   Permissions: View Channel, Send Messages, Embed Links, Read Message History (needed to reply to a report
   with "fixed"). Pick your server and confirm.
5. In Discord: turn on **Settings → Advanced → Developer Mode**. Then:
   - right-click the reports channel → *Copy Channel ID* (= `DISCORD_CHANNEL_ID`);
   - right-click your own name → *Copy User ID* (= `ADMIN_IDS`);
   - right-click the server name → *Copy Server ID* (= `DISCORD_GUILD_ID`, optional but recommended:
     slash commands then show up immediately).

## 2. Supabase

Using an existing project is fine: everything goes into its own schema `vanta`, functions starting with `vanta_` and
one trigger `vanta_profile_sync` on `auth.users`. Your own tables are not touched. New project? Pick an EU region
(e.g. Frankfurt), which is cleanest for privacy.

1. **Set up the database.** Dashboard → your project → **SQL Editor** → *New query* → paste the full contents of
   [`supabase/supabase-setup.sql`](../supabase/supabase-setup.sql) → **Run**. Expected: *Success. No rows returned*.
   Running it again (e.g. after an update) is always fine. The Supabase CLI works too: `supabase link` and then
   `supabase db push` (the migrations are in `supabase/migrations/`).
   The `vanta` schema does **not** need to be added to *Exposed schemas*: Vanta and the bot only talk through the
   `vanta_*` functions.
2. **Copy the details** (*Project Settings*):
   - **Project URL**: *Data API* (or the *Connect* button), e.g. `https://abcd1234.supabase.co`;
   - **Publishable key**: *API Keys* → `sb_publishable_…` (for Vanta; public);
   - **Secret key**: *API Keys* → *Secret keys* → `sb_secret_…` (for the bot; **secret**). Older projects also
     have the "anon" and "service_role" keys; those work too, but Supabase retires them at the end of 2026.
3. **Discord as sign-in method.** *Authentication* → *Sign In / Providers* → **Discord** → enable:
   - paste the *Client ID* and *Client Secret* from step 1.2;
   - copy the *Callback URL* shown to Discord → OAuth2 → Redirects (step 1.2);
   - *Allow users without an email*: enable (recommended). Supabase always asks Discord for the e-mail address; this
     cannot be turned off. With this option players without a verified e-mail address can sign in too.
   - Save.
4. **Redirect for Vanta.** *Authentication* → *URL Configuration* → *Redirect URLs* → *Add URL*:

   `http://127.0.0.1:*/callback*`

   Vanta receives the sign-in on a random free port on the user's own PC (only `127.0.0.1`, not reachable from the
   network). *Site URL* does not matter.

## 3. Which values go where

| Name | Where | Value |
|---|---|---|
| Discord Client ID + Client Secret | **only** Supabase (Discord provider) | Discord → OAuth2 |
| Project URL | Vanta **and** bot (`SUPABASE_URL`) | `https://<ref>.supabase.co` |
| Publishable key | **only** Vanta | `sb_publishable_…` (public) |
| Secret key | **only** bot (`SUPABASE_SERVICE_ROLE_KEY`) | `sb_secret_…` (secret) |
| `DISCORD_BOT_TOKEN` | bot | bot token (secret) |
| `DISCORD_CHANNEL_ID` | bot | channel id |
| `ADMIN_IDS` | bot | your user id (several: comma-separated) |
| `DISCORD_GUILD_ID` | bot (optional) | server id |

Optional for the bot: `POLL_SECONDS` (15), `DIGEST_WEEKDAY` (0 = Monday), `DIGEST_HOUR` (10),
`TIMEZONE` (Europe/Amsterdam), `LOG_LEVEL` (INFO). `SUPABASE_SECRET_KEY` is accepted as another name for
`SUPABASE_SERVICE_ROLE_KEY`.

## 4. Try the bot locally (optional)

In the `bot` folder with Python 3.10+: `pip install -r requirements.txt`, copy `.env.example` to `.env`, fill it in,
then `python bot.py --check` (checks settings, key and database) and `python bot.py`.

## 5. The bot on Ferox (Pterodactyl)

A short checklist is also in [`bot/README-bot.txt`](../bot/README-bot.txt) (Dutch: [`bot/LEESMIJ-bot.txt`](../bot/LEESMIJ-bot.txt)).

1. **Server with a Python egg.** In the panel under *Startup*: pick Python 3.11 or 3.12 as Docker image if possible
   (3.10 works too; older does not).
2. **Upload the files.** Needed: `bot.py`, the `vanta_bot/` folder, `requirements.txt` and `.env.example`
   (tests, Dockerfile and README are not needed).
   - *File Manager*: upload the zip with **Upload**, then right-click the zip → **Unarchive**. Check that
     `bot.py` is directly in `/home/container` (not in a subfolder).
   - Or via **SFTP** (details under *Settings → SFTP Details*; password = your panel password), for example
     with WinSCP or FileZilla.
3. **Startup settings** (*Startup* tab):
   - *App py file* / *Startup file*: `bot.py`
   - *Requirements file*: `requirements.txt` (the egg installs the packages at every start)
   - *Git Repo Address* / *Auto Update*: empty / off
   - *Additional Python packages*: empty
4. **Fill in the settings.** Easiest: in the *File Manager* **New File** → name `.env` → paste the contents of
   `.env.example` and fill it in (see the table in step 3) → save. If the panel has its own fields under *Startup*
   for variables, you can use those instead; they take precedence over `.env`.
5. **Start** → **Console** tab. It is working when you see:
   ```
   vanta: Vanta bot 0.1.0, Python 3.x, discord.py 2.7.1
   vanta.bot: synced 7 commands to guild …
   vanta.bot: logged in as Vanta#1234 (…); 1 guild(s)
   ```
   Type `/stats` in Discord to test.
6. **What the error messages mean**
   - `config: … is missing` → a value in `.env` is missing or wrong.
   - `… is the publishable key / anon key` → wrong Supabase key; use the secret key.
   - `Discord rejects the token` → `DISCORD_BOT_TOKEN` is wrong (copy it again or reset it).
   - `function_missing` / *Could not find the function* → `supabase-setup.sql` has not been run yet (step 2.1).
   - `permission_denied` → wrong key (publishable/anon instead of secret).
   - `unreachable` → `SUPABASE_URL` is wrong or the Supabase project is paused (free projects pause after a week
     without traffic; click *Restore* in the dashboard).
   - `no permission in channel` → give the bot in that channel: View Channel, Send Messages, Embed Links,
     Read Message History.
   - `ModuleNotFoundError: discord` → packages not installed: check *Requirements file* and restart.
   - Slash commands not visible → fill in `DISCORD_GUILD_ID`, or wait up to an hour (global); restart Discord with Ctrl+R.
7. The bot creates `state.json` and `.commands-hash` in its folder. Leave them; deleting them does no harm
   (it then re-reads the reports of the last 30 days, without duplicate messages).

**Buttons and commands.** Under every report there are buttons *Fixed* (asks for the Vanta version), *Can't
reproduce*, *Duplicate* and *Reopen*; only users in `ADMIN_IDS` can use them. `/fixed`, `/ban` and `/digest` are for
admins only. `/cheat` also shows admins who reported (visible only to you). Every Monday at 10:00 the bot posts a
weekly digest. `/ban` blocks reports from that Discord user and signs them out everywhere. All bot texts are English.

## 6. Updating

Upload the new bot files (do not overwrite your `.env`/`state.json`), run `supabase-setup.sql` again in the SQL
Editor, *Restart*. The SQL is idempotent: existing reports are kept.

## 7. Connecting Vanta

Put the Project URL and the **publishable** key in `src/Vanta.Core/Branding.cs`:

```csharp
public const string SupabaseUrl = "https://abcd1234.supabase.co";
public const string SupabaseKey = "sb_publishable_…";
```

and build a new release. Until then the account buttons are hidden. Vanta refuses a secret/service_role key
(the buttons then stay off and a warning is logged). To test without a new release, per PC use
`"supabaseUrl"` and `"supabaseKey"` in `%LOCALAPPDATA%\Vanta\settings.json`, or the environment variables
`VANTA_SUPABASE_URL` and `VANTA_SUPABASE_KEY`.

Check on a PC: `Vanta.exe --account-selftest` tests the encrypted storage (DPAPI), the sign-in listener and a
simulated Supabase sign-in. With `--account-selftest <url> <key>` it also runs sign-in, reporting, token refresh
and account deletion, but only against the local test stack (`sh supabase/tests/local-stack.sh up`, with fake Discord);
against the real project, test via the button in Vanta.

In Vanta: **Settings → Account → Sign in with Discord**. The browser opens the Discord consent page (via Supabase);
you can close the tab afterwards. Right-click a cheat → *Broken* opens a window for a short note;
*Works* is shared immediately; *Not tested* / *Default* withdraws your report; *Report…* always opens the window.
Under **Notes** you see per cheat what the community reports. *Delete account* removes the Supabase account with all
reports.

> **Careful with a shared project.** If you use the same Supabase project for other apps too, *Delete account* in
> Vanta removes the user's whole Supabase account (so also for those other apps), and anyone with a Discord account
> can create a Supabase user via the Discord sign-in. A separate project for Vanta is therefore simplest.

## 8. Costs and limits

The Supabase free plan (500 MB database, 50,000 monthly active users) is plenty. Free projects are paused after a week
without traffic; the bot asks for new reports every 15 seconds and so keeps the project active. Every hour the bot
cleans up expired rate-limit data (with the IP hashes) and bot events older than 30 days (`vanta_bot_cleanup`).
Limits in the database: 30 reports per hour per account; community lookups 240 per 10 minutes and usage counts
30 per hour per IP hash.
