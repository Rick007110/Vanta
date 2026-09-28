VANTA DISCORD BOT ON FEROX (PTERODACTYL) WITH SUPABASE
======================================================

(Nederlandse versie: LEESMIJ-bot.txt)

Contents of this folder:
  bot.py               the startup file
  vanta_bot/           the bot code (belongs next to bot.py)
  requirements.txt     Python packages (the panel installs them at start)
  .env.example         example of the settings
  supabase-setup.sql   the database setup for Supabase

Required: Python 3.10 or newer (3.11+ recommended), a Supabase project and a Discord application.


1. SUPABASE: SET UP THE DATABASE (once, ~1 minute)
   - Supabase dashboard -> your project -> SQL Editor -> New query.
   - Paste the full contents of supabase-setup.sql and click Run. Expected: "Success. No rows returned".
   - Safe in an existing project: everything goes into its own schema "vanta" plus functions starting with
     "vanta_". Your own tables are not touched. Running it again (e.g. after an update) is always fine.
   - You do NOT need to add the schema "vanta" to "Exposed schemas".

2. SUPABASE: COPY THE DETAILS
   - Project URL: Project Settings -> Data API (or the "Connect" button), e.g. https://abcd1234.supabase.co
   - Secret key: Project Settings -> API Keys -> "Secret keys" -> sb_secret_... (or the old "service_role" key).
     SECRET: this key bypasses all security. Only put it in the panel/.env, never in Vanta, git or Discord.
     The publishable/anon key does not work for the bot (it refuses it with a clear message).

3. DISCORD: CREATE THE BOT
   - https://discord.com/developers/applications -> New Application (e.g. "Vanta").
   - Bot tab -> Reset Token -> copy the token (SECRET). All three Privileged Gateway Intents can stay OFF.
   - Invite: OAuth2 tab -> URL Generator -> scopes "bot" and "applications.commands" ->
     permissions View Channel, Send Messages, Embed Links, Read Message History (together: 84992).
     Or directly: https://discord.com/oauth2/authorize?client_id=YOUR_APPLICATION_ID&scope=bot+applications.commands&permissions=84992
   - Turn on Developer Mode in Discord (Settings -> Advanced) and copy with right-click:
     the channel id, your own user id and (recommended) the server id.

4. PTERODACTYL (FEROX): UPLOAD THE FILES
   - Panel -> your server -> Files (File Manager) -> Upload -> vanta-bot.zip, then right-click -> Unarchive.
     (Or via SFTP: details are under Settings -> SFTP Details.) bot.py must be directly in the root folder,
     with the vanta_bot folder next to it.
   - Startup tab: set the startup file ("App py file"/"Startup file", the name differs per egg) to  bot.py
     and the requirements file to  requirements.txt  (if those fields exist).
     Pick Python 3.11 or newer under "Docker Image" if possible (3.10 works too).

5. SETTINGS
   - If the egg has input fields for variables: fill in the names below there. Otherwise:
     File Manager -> open .env.example -> copy the contents -> New File "  .env  " -> paste and fill in -> Save.
       DISCORD_BOT_TOKEN          token from step 3 (secret)
       DISCORD_CHANNEL_ID         channel for the reports
       ADMIN_IDS                  your Discord user id (several: comma-separated)
       DISCORD_GUILD_ID           server id (recommended: slash commands then show up immediately)
       SUPABASE_URL               project URL from step 2
       SUPABASE_SERVICE_ROLE_KEY  secret key from step 2 (secret)
     Optional: POLL_SECONDS (15), DIGEST_WEEKDAY (0 = Monday), DIGEST_HOUR (10), TIMEZONE (Europe/Amsterdam),
     LOG_LEVEL (INFO). Variables in the panel take precedence over .env.

6. START AND CHECK
   - Click Start and watch the Console. Good signs include:
       "Vanta bot ... Python 3.x, discord.py 2.7.1"
       "synced 7 commands to guild ..."
       "logged in as Vanta#1234 ..."
   - The bot keeps state.json and .commands-hash in its folder; do not delete them (then it posts nothing twice).
   - Updating: upload the new files (do not overwrite your .env/state.json), run supabase-setup.sql again, Restart.
     After this update the slash command options are English (count, version, user, unban); the bot re-registers
     them automatically at start.

COMMON CONSOLE MESSAGES
   config: ... is missing                  -> variable not filled in (step 5).
   ... is the publishable key / anon key   -> you pasted the wrong Supabase key; use the secret key.
   Discord rejects the token               -> create a new token (step 3) and fill it in.
   function_missing / "Could not find the function"
                                           -> supabase-setup.sql has not been run yet (step 1).
   permission_denied                       -> wrong key (publishable/anon instead of secret).
   unreachable                             -> SUPABASE_URL is wrong or the Supabase project is paused.
   no permission in channel                -> give the bot in that channel: View Channel, Send Messages, Embed Links.
   Python 3.10 or newer is required        -> pick a newer Docker Image in the Startup tab.

LATER: SIGNING IN FROM VANTA (needed so reports come in)
   - Supabase -> Authentication -> Sign In / Providers -> Discord: turn on, paste the Client ID and Client Secret from
     the Discord Developer Portal (OAuth2 tab). Copy the "Callback URL" Supabase shows
     (https://YOUR-PROJECT.supabase.co/auth/v1/callback) to Discord -> OAuth2 -> Redirects.
   - Supabase -> Authentication -> URL Configuration -> Redirect URLs -> add:  http://127.0.0.1:*/callback*
   - Vanta only gets the Project URL and the publishable key (those are public); never the secret key.
   The full step-by-step guide is in docs/SETUP.md in the Vanta repository.
