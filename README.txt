VANTA v0.3.6 - trainer for single-player games
==============================================

Vanta is a standalone program (no Cheat Engine required). It only works for
single-player/offline games. Games with online anti-cheat are refused on purpose.

Nederlandse versie: see LEESMIJ.txt. In Vanta: Settings > Language > Nederlands.
Full version history: CHANGELOG.md.

NEW IN v0.3.6
  - Games without cheats are no longer listed in the catalog. Every game in
    Vanta has at least one cheat.

NEW IN v0.3.5
  - 3 new games: Mafia III: Definitive Edition (10 cheats), Windrose (Early Access,
    5 cheats) and Avatar: Frontiers of Pandora (5 cheats). Every AOB was checked
    against the current Steam build (found exactly once); the cheats are not yet
    tested in-game. The Windrose value editors (health, stamina, speed, jump) are
    experimental.
  - Windrose and Avatar: only use cheats in a solo world/campaign, never in co-op.
  - The Last Caretaker: Unlimited Jump no longer stays active after turning it off
    (fixed in code, not yet tested in-game). Used the old version? Reload your
    save once to get the normal jump count back.

NEW IN v0.3.4
  - English is now the default language everywhere: the app, error messages,
    game notes, update notifications, "What's new", the documentation and the
    Discord bot. Dutch remains available: Settings > Language > Nederlands.
    If you never picked a language yourself, Vanta now starts in English; a
    language you chose explicitly is kept from now on.
  - Game texts (notes, scope, cheat names and hints) in game.json are English,
    with Dutch translations under "i18n": { "nl": ... }.
  - Release notes are English first, with the Dutch text in a collapsible section.
    "What's new" shows the part for your language.
  - README.txt (this file) and CHANGELOG.md are the primary docs; LEESMIJ.txt is
    the Dutch version.

NEW IN v0.3.3
  - Request a game: the window is wider, so long game names and the explanation
    are fully visible (no more cut-off text or horizontal scrollbar).
  - Searching Steam now only shows real games: DLC, soundtracks, season passes,
    packs, demos and tools are left out. If you paste a link or app ID of a DLC,
    the base game is picked.
  - All scrollbars in Vanta are now thin and dark, matching the rest.
  - The Privacy button now opens the privacy page on the Vanta website.

NEW IN v0.3.2
  - Request a game: "Request a game" button at the bottom left. Search a game on
    Steam (by name, Steam app ID or store link) and vote for it, so you can see
    which games the community wants most in Vanta. Voting requires signing in with
    Discord; the list of most requested games is visible to everyone.
  - The update notification at the bottom right is more compact: only the version
    and the buttons Update now, What's new and Later. "What's new" opens a window
    with the full release notes (close with X, Esc or by clicking outside).

NEW IN v0.3.1
  - Far Cry 6: updated for the latest game update (Steam build 11359732).
    Infinite Stamina, Infinite Oxygen, No Reload and the Resource and money
    multiplier work again (not yet tested in-game).
  - If a cheat is still active from an earlier Vanta session, Vanta now says so
    clearly instead of "no unique AOB found". Restart the game in that case.

NEW IN v0.3.0
  - Sign in with Discord (optional): Settings > Account > Sign in with Discord.
    Without an account everything works as always.
  - Report cheats: right-click a cheat > Works / Broken / Report... to share with the
    community whether a cheat works in your game version (with a short note).
    Not tested / Default withdraws your report.
  - Community status: under Notes you see per cheat how many players report
    "works" or "broken" and whether a problem has been fixed.
  - Delete account: Settings > Account > Delete account removes your account and all
    your reports immediately.
  - Share anonymous usage (off by default): counts per day how often each cheat is
    turned on, without account, name or IP address. Turn on in Settings > Account.
  - Privacy: "Privacy" button under Account (https://rick007110.github.io/vanta-site/privacy/),
    or docs\privacy.md in this folder.

NEW IN v0.2.2
  - Updates are also found while Vanta is already open: at start, every 30 minutes
    and when you activate the window again (at most once per 5 minutes). After
    "Later" the same version is not shown again this session.
  - The update notification no longer flickers or jumps while downloading and is no
    longer transparent.

NEW IN v0.2.1
  - New status "Broken": such a cheat is greyed out and cannot be turned on
    ("Try anyway" still allows it). Notes show "Does not work in this version."
  - Right-click a cheat: Works / Broken / Not tested / Default (from game.json).
    Your choice is saved per game, game version and cheat in
    %LOCALAPPDATA%\Vanta\status.json and takes precedence over game.json.
  - Settings > "Export test status": copies your test status as JSON to the
    clipboard and saves it to %LOCALAPPDATA%\Vanta\teststatus-export-<date>.json,
    so you can send it in.

NEW IN v0.2
  - Far Cry 6 and Far Cry 5 (all cheats still untested in the current builds).
  - Store detection: Vanta finds your install via Steam, Ubisoft Connect, Epic,
    GOG, EA app or Xbox and starts the game via the right launcher ("Start via ...").
  - Vanta.exe --verify: checks without starting the game whether the cheats match
    your game version.
  - Automatic updates via GitHub (with SHA-256 check and rollback on problems).

CONTENTS OF THIS FOLDER
  Vanta.exe               the program (everything is inside, nothing to install)
  games\                  cheat definitions per game (games\<id>\game.json) + index.json
  schema\                 JSON Schema for game.json (for adding games yourself)
  selftest\vanta_dummy.exe  harmless test program for "Vanta.exe --selftest"
  docs\                   technical documentation (README-DEV.md, BRANDING.md, privacy.md)
  README.txt, CHANGELOG.md  this file and the version history (LEESMIJ.txt = Dutch)

REQUIREMENTS
  - Windows 10/11 64-bit.
  - Microsoft Edge WebView2 Runtime (included with Windows 11). If it is missing,
    Vanta shows a message with the download link:
    https://go.microsoft.com/fwlink/p/?LinkId=2124703

STEP 1 - SELFTEST (1 minute)
  1. Extract the zip to its own folder, for example C:\Games\Vanta
     (preferably not in "Program Files" and do not run it from inside the zip).
  2. Open that folder, click the address bar, type  cmd  and press Enter.
  3. Type:  Vanta.exe --selftest   and press Enter.
  4. Expected: a list of [OK  ] lines and at the bottom "RESULT: PASSED (39 checks)".
     The result is also saved to %LOCALAPPDATA%\Vanta\selftest.log
     Windows Defender/SmartScreen may warn: the program is not digitally
     signed. Choose "More info" -> "Run anyway".

STEP 2 - CHECK YOUR GAME VERSION (FAR CRY 5/6)
  The Far Cry cheats come from public tables from 2018-2025 and are not yet tested
  in the current builds. First check whether they match (the game does not need to run):
    Vanta.exe --verify far-cry-6
    Vanta.exe --verify far-cry-5
  Without a path Vanta finds the install itself. You can also pass the file:
    Vanta.exe --verify far-cry-6 "D:\Games\Far Cry 6\bin\FC_m64d3d12.dll"
    Vanta.exe --verify far-cry-5 "...\steamapps\common\FarCry5\bin\FC_m64.dll"
  Or while the game is running (read-only):  Vanta.exe --verify far-cry-6 --live
  Per cheat you see whether the AOB is found exactly once. At the bottom it says
  "RESULT: x/y". The report is also saved to %LOCALAPPDATA%\Vanta\verify-<game>.txt.
  Send that file in: it is used to confirm or fix cheats.

STEP 3 - PLAY
  1. Double-click Vanta.exe and pick a game on the left.
  2. Click "Start via Steam/Ubisoft Connect/..." or start the game yourself.
     Vanta attaches automatically after a few seconds ("Attached").
  3. Turn cheats on with a click or the hotkey. If a cheat fails, a red message
     appears (e.g. "no unique AOB found") and NOTHING has been changed.
  4. "All off", detaching or closing Vanta restores everything in the game.
  Far Cry 5/6, Avatar: solo campaign only. Windrose: solo worlds only. Do not use in
  co-op or online.

BACK UP YOUR SAVE FIRST
  The Last Caretaker: %LOCALAPPDATA%\Voyage\Saved\SaveGames
  Far Cry 6 (Ubisoft Connect): ...\Ubisoft Game Launcher\savegames\<account>\5266
  Far Cry 5: ...\Ubisoft Game Launcher\savegames\<account>\<number> (copy the whole folder)
  Mafia III: %LOCALAPPDATA%\2K Games\Mafia III\Data\<number>\gamesaves
  Windrose: %LOCALAPPDATA%\R5\Saved\SaveProfiles\<Steam ID>
  Avatar: Frontiers of Pandora: ...\Ubisoft Game Launcher\savegames\<account>\<number>

HOTKEYS
  Also work while the game is in the foreground. To change: "Hotkeys" tab,
  click "Change" and press the new combination. Far Cry uses Numpad 1-9 and
  Ctrl+Numpad (see the tab per game).

UPDATES
  Vanta checks GitHub for a new version at start, then every 30 minutes and when you
  activate the window (at most once per 5 minutes).
  "Update now": cheats are turned off, the update is downloaded and verified
  (SHA-256), and Vanta restarts by itself. "Later": the update is prepared in the
  background and installed at the next start. If the new version does not start,
  Vanta automatically restores the previous version. Manually: Settings ->
  "Check for updates". If Vanta is in Program Files, Windows asks for permission
  (UAC). Your settings and own games in %LOCALAPPDATA%\Vanta are always kept.

SETTINGS (button at the bottom left)
  Language (English/Nederlands), catalog folder, auto-attach, updates.
  Settings, logs and art cache: %LOCALAPPDATA%\Vanta

ADMINISTRATOR?
  Not needed. Vanta runs as a normal user, just like Steam and your games. Only if a
  game itself runs as administrator does Vanta say "Run Vanta as administrator".

REPORTING PROBLEMS
  https://github.com/Rick007110/Vanta/issues
  Include these files:
    %LOCALAPPDATA%\Vanta\logs\vanta-<date>.log
    %LOCALAPPDATA%\Vanta\verify-<game>.txt   (after --verify)
    %LOCALAPPDATA%\Vanta\selftest.log
