# Changelog

All notable changes to Vanta. English is the primary language; the Dutch version is in `LEESMIJ.txt`.
Releases: https://github.com/Rick007110/Vanta/releases

## v0.3.7 (2026-09-29)

- The sidebar search moves to the centre of the window when focused (click or Ctrl+K), with a dimmed background and a keyboard-navigable result list.
- Windrose: 5 → 31 cheats. New: Unlock Travel (AOB verified, 1 hit) and value editors for max health, health regeneration, max stamina, stamina regeneration and cost, posture, armor, corruption, damage and damage resistance, critical chance and damage, all 8 attributes (Strength ... Endurance), movement speed modifier, jump hold time, character time speed and revive time. Health, stamina and jump height now use the offsets from the newest public table. All value editors are experimental: pointer offsets cannot be checked on disk.
- Avatar: Frontiers of Pandora: 5 → 11 cheats. New: position editors X/Y/height (teleport by hand), Night Time switch, Crafting Materials Set to 900 and a Clan Favour editor. Hook AOBs verified (1 hit each); the values they read are experimental.
- Mafia III: Definitive Edition: 10 → 27 cheats. New: No Reload, Rapid Fire, No Detection, Permanent Intel Vision (ESP), Max Reserve Ammo & Equipment, No Throwable Decrease, Adrenaline Shots Always 10, Free Shop Items, Apathetic NPCs, Freeze All Entities, an alternative No Recoil/Spread, editors for health, magazine ammo, adrenaline shots and "Electronics", plus an alternative max reserve ammo and Colour Vision. Cheats with side effects say so in their description; Colour Vision is marked broken because its author says it crashes the game.
- None of the new cheats are tested in-game yet. Every AOB is found exactly once in the current Steam build (except Mafia III Free Shop Items, which is experimental).

## v0.3.6 (2026-09-28)

- Games without cheats are no longer listed in the catalog. Every game in Vanta has at least one cheat.

## v0.3.5 (2026-09-28)

- New games: Mafia III: Definitive Edition (10 cheats), Windrose (Early Access, 5 cheats) and Avatar: Frontiers of Pandora (5 cheats). Every AOB was checked against the current Steam build and is found exactly once; the cheats have not been tested in-game yet. The Windrose value editors (health, stamina, walk speed, jump height) use pointer offsets from an older build and are experimental.
- Windrose and Avatar: only use cheats in a solo world or the solo campaign, never in co-op.
- The Last Caretaker: fixed Unlimited Jump staying active after turning it off. The old version wrote a maximum jump count of 99 into your character, and turning the cheat off did not undo that. It now only changes the jump counter instruction, which is fully restored when you turn it off (fixed in code, not yet tested in-game). If you used the old version, reload your save once to get the normal jump count back.

## v0.3.4 (2026-09-28)

- English is now the default language everywhere: the app, error messages, game notes, update notifications, "What's new", the documentation and the Discord bot. Dutch remains available: Settings > Language > Nederlands. If you never picked a language yourself, Vanta now starts in English; a language you chose explicitly is kept from now on.
- Game texts (notes, scope, cheat names and hints) in game.json are English, with Dutch translations under "i18n": { "nl": ... }.
- Release notes are English first, with the Dutch text in a collapsible section. "What's new" shows the part for your language.
- README.txt and CHANGELOG.md (this file) are the primary docs; LEESMIJ.txt is the Dutch version.

## v0.3.3 (2026-09-28)

- Request a game: the window is wider, so long game names and the explanation are fully visible (no more cut-off text or horizontal scrollbar).
- Searching Steam now only shows real games: DLC, soundtracks, season passes, packs, demos and tools are left out. If you paste a link or app ID of a DLC, the base game is picked.
- All scrollbars in Vanta are now thin and dark, matching the rest.
- The Privacy button now opens the privacy page on the Vanta website.

## v0.3.2 (2026-09-28)

- Request a game: "Request a game" button at the bottom left. Search a game on Steam (by name, Steam app ID or store link) and vote for it, so you can see which games the community wants most in Vanta. Voting requires signing in with Discord; the list of most requested games is visible to everyone.
- The update notification at the bottom right is more compact: only the version and the buttons Update now, What's new and Later. "What's new" opens a window with the full release notes (close with X, Esc or by clicking outside).

## v0.3.1 (2026-09-28)

- Far Cry 6: updated for the latest game update (Steam build 11359732). Infinite Stamina, Infinite Oxygen, No Reload and the Resource and money multiplier work again (not yet tested in-game).
- If a cheat is still active from an earlier Vanta session, Vanta now says so clearly instead of "no unique AOB found". Restart the game in that case.

## v0.3.0 (2026-09-28)

- Sign in with Discord (optional): Settings > Account > Sign in with Discord. Without an account everything works as always.
- Report cheats: right-click a cheat > Works / Broken / Report... to share with the community whether a cheat works in your game version (with a short note). Not tested / Default withdraws your report.
- Community status: under Notes you see per cheat how many players report "works" or "broken" and whether a problem has been fixed.
- Delete account: Settings > Account > Delete account removes your account and all your reports immediately.
- Share anonymous usage (off by default): counts per day how often each cheat is turned on, without account, name or IP address. Turn on in Settings > Account.
- Privacy: "Privacy" button under Account (https://rick007110.github.io/vanta-site/privacy/), or docs\privacy.md in this folder.

## v0.2.2 (2026-09-27)

- Updates are also found while Vanta is already open: at start, every 30 minutes and when you activate the window again (at most once per 5 minutes). After "Later" the same version is not shown again this session.
- The update notification no longer flickers or jumps while downloading and is no longer transparent.

## v0.2.1 (2026-09-27)

- New status "Broken": such a cheat is greyed out and cannot be turned on ("Try anyway" still allows it). Notes show "Does not work in this version."
- Right-click a cheat: Works / Broken / Not tested / Default (from game.json). Your choice is saved per game, game version and cheat in %LOCALAPPDATA%\Vanta\status.json and takes precedence over game.json.
- Settings > "Export test status": copies your test status as JSON to the clipboard and saves it to %LOCALAPPDATA%\Vanta\teststatus-export-<date>.json, so you can send it in.

## v0.2 (2026-09-27)

- Far Cry 6 and Far Cry 5 (all cheats still untested in the current builds).
- Store detection: Vanta finds your install via Steam, Ubisoft Connect, Epic, GOG, EA app or Xbox and starts the game via the right launcher ("Start via ...").
- Vanta.exe --verify: checks without starting the game whether the cheats match your game version.
- Automatic updates via GitHub (with SHA-256 check and rollback on problems).
