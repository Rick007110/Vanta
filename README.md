# Vanta

**Trainer for single-player games on Windows.** Standalone program, no Cheat Engine required. Games with online anti-cheat
are refused on purpose: Vanta is only meant for solo/offline play.

![Far Cry 6 in Vanta](docs/screenshots/far-cry-6.png)

## Download

Download the latest `Vanta-v*.zip` from [Releases](https://github.com/Rick007110/Vanta/releases/latest), extract it to
its own folder (e.g. `C:\Games\Vanta`) and start `Vanta.exe`. Then read `README.txt` (Dutch: `LEESMIJ.txt`).
Version history: [CHANGELOG.md](CHANGELOG.md).

* Windows 10/11 64-bit, Microsoft Edge WebView2 Runtime (included with Windows 11).
* Nothing needs to be installed (.NET is inside the exe).
* The exe is not digitally signed: SmartScreen may warn ("More info" → "Run anyway").
* Optionally verify the download with the `.sha256` file next to the zip.
* English by default; Dutch is available under Settings → Language.

## Games

| Game | Cheats | Status |
|---|---|---|
| The Last Caretaker (EA 0.8.5) | 16 | 1 confirmed, rest untested |
| Far Cry 6 (1.8.0, Ubisoft Connect/Steam) | 20 | untested: check with `--verify` |
| Far Cry 5 (Steam build 18766066) | 16 | untested: check with `--verify` |
| Mafia III: Definitive Edition (Steam build 5121098) | 27 | AOBs verified on disk (Free Shop experimental), value editors experimental, untested in-game |
| Windrose (Early Access, Steam build 24803703) | 31 | Infinite Jumps and Unlock Travel verified; value editors experimental |
| Avatar: Frontiers of Pandora (Steam build 22429549) | 11 | AOBs verified on disk (Denuvo: check `--verify --live`), value editors experimental, untested in-game |

Far Cry 5/6: solo campaign only, not in co-op or online. Far Cry 6 has no anti-cheat; Far Cry 5 had EasyAntiCheat removed
in its last patch (2019). If Vanta still finds anti-cheat files, it refuses.
Windrose and Avatar: solo world/campaign only, never in co-op.

## What Vanta does

* **Store detection**: finds the install via Steam, Ubisoft Connect, Epic, GOG, EA app or Xbox and starts the game via
  the right launcher ("Start via Ubisoft Connect").
* **Safe patching**: every cheat locates its code via an AOB pattern that must occur exactly once. Otherwise nothing is
  written. Disabling, detaching or closing Vanta restores every byte exactly.
* **`Vanta.exe --verify <game>`**: checks without starting the game whether all cheats match your game version.
* **Automatic updates** via GitHub Releases, with SHA-256 check, a backup and automatic rollback if the new version does
  not start.
* Hotkeys (also while the game is in the foreground), English/Dutch.

## Add a game yourself

A game is one JSON file: `games/<id>/game.json` (schema: [`schema/game.schema.json`](schema/game.schema.json)). Texts are
English; Dutch translations go under `"i18n": { "nl": { ... } }` at game and cheat level.

1. Convert a Cheat Engine table: `Vanta.exe import "Game.CT" -o games\<id> --id <id> --name "Name" --process Game.exe`
2. Resolve the flagged lines in `import-report.txt` by hand and fill in `antiCheat`/`onlineOnly` honestly.
3. `Vanta.exe validate games` and `Vanta.exe index games`
4. `Vanta.exe --verify <id> "path\to\module.dll"`: every cheat must have exactly 1 hit.

Details: [README-DEV.md](README-DEV.md).

## Community reports (optional)

With a Discord account you can report whether a cheat works; Vanta then shows per cheat what other players report.
Signing in is never required: without an account everything works as always. The backend is a Supabase project (database +
sign-in, [`supabase/`](supabase/)); the Discord bot ([`bot/`](bot/)) can be hosted separately. Setup: [docs/SETUP.md](docs/SETUP.md). Privacy: <https://rick007110.github.io/vanta-site/privacy/> (source: [docs/privacy.md](docs/privacy.md)).

## Building

`./build.sh` (dotnet 8 SDK; mingw for the selftest program). Tests: `dotnet test tests/Vanta.Tests`,
UI: `node tests/ui/ui.e2e.js` (puppeteer-core + Chrome).

## Nederlands

Vanta is een losstaande trainer voor **singleplayer/offline** Windows-games (geen Cheat Engine nodig). Games met online
anti-cheat worden geweigerd. Download de zip bij [Releases](https://github.com/Rick007110/Vanta/releases/latest) en lees
`LEESMIJ.txt`. Vanta start in het Engels; kies Nederlands via Settings → Language → Nederlands. Far Cry 5/6-cheats zijn
alleen voor de solo-campagne.

MIT License.
