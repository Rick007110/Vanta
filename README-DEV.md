# Vanta v0.3.0 (developer notes)

Standalone Windows trainer for **single-player/offline** games. It has no Cheat Engine dependency.
Games marked `antiCheat` or `onlineOnly` are listed but refused (never opened, never launched).

## Layout
```
Directory.Build.props      brand name + version (see docs/BRANDING.md)
schema/game.schema.json    JSON Schema (draft 2020-12) for games/<id>/game.json
games/<id>/game.json       one folder per game (+ optional assets); games/index.json = catalog index (generated)
src/Vanta.Core             net8.0, platform-neutral engine + catalog + importer (unit-tested on Linux)
  Engine/Aob.cs            AOB patterns (?? / nibble wildcards), vectorised anchor search, chunked module scan
  Engine/Asm.cs            MiniAssembler: CE-style AA text -> x64 bytes via Iced (labels, rip-relative, db/dd/dq, rel32 jumps)
  Engine/Runtime.cs        site resolution (unique AOB, expect/checks), patches, code caves + hooks, undo log, pointer chains
  Engine/Session.cs        per-process cheat state: requires/hidden hooks, freeze, value polling, restore-all
  Engine/Controller.cs     UI <-> engine logic: detection, launch via the detected store (debounced), attach, version check, re-apply, hotkeys
  Engine/Verifier.cs       --verify: resolves every AOB against the module file on disk (PeFileMemory) or the live process
  Engine/FileFingerprint.cs size + SHA-256 of first/last MiB + PE timestamp/SizeOfImage of a module file
  Stores/                  store detection + launch URIs: Steam, Ubisoft Connect, Epic, GOG, EA app, Xbox (IStoreEnv = registry/files)
  Update/Update.cs         auto-updater core: SemVer, GitHub release parsing, checker, download + SHA-256, apply/backup/rollback
  Win/WinMemory.cs         OpenProcess/Read/WriteProcessMemory/VirtualProtectEx/VirtualAllocEx(near)/Toolhelp modules
  Win/SelfTest.cs          end-to-end engine test against tools/dummy/vanta_dummy.exe
  Win/WinStoreEnv.cs       real registry (64-bit view) + file system for the store providers
  Catalog/                 LocalCatalog (lazy, indexed), RemoteCatalog (design for later), GameValidator
  Import/CtImporter.cs     Cheat Engine .CT -> game.json
  ArtCache.cs              Steam CDN art -> %LOCALAPPDATA%\Vanta\art (fallback file names, negative cache)
  HotkeyParser.cs          "Ctrl+Numpad+" -> RegisterHotKey modifiers/VK
src/Vanta.App              net8.0-windows WinForms + WebView2 host (Vanta.exe); Updater.cs = in-app updater + helper + splash
src/Vanta.Cli              vanta-tool (validate | index | import | asm | selftest | verify | uifixture)
ui/                        web UI (Nocturne variant A design, rebranded), embedded into Vanta.exe
tests/Vanta.Tests          xunit (see Tests), tests/asmref (51 GNU as reference encodings), tests/ui (headless Chrome)
tools/dummy                selftest target (C + asm, mingw) and selftest.game.json
tools/make_tlc.py          generates games/the-last-caretaker/game.json
```

## Build (Linux box or Windows)
```
dotnet test tests/Vanta.Tests
dotnet publish src/Vanta.App -c Release -o out/Vanta          # win-x64, self-contained, single file, compressed
x86_64-w64-mingw32-gcc -O1 -s -static -o tools/dummy/vanta_dummy.exe tools/dummy/vanta_dummy.c tools/dummy/dummy_asm.S
node tests/ui/ui.e2e.js                                        # needs puppeteer-core + Chrome
./build.sh                                                     # everything + zip
```
WinForms is not trim-safe, so the exe is untrimmed but compressed (about 67 MB). No .NET install is needed.

## Runtime architecture
* **UI thread.** Runs the frameless WinForms window and WebView2. The UI is served from embedded resources at
  `https://vanta.example/` (a reserved domain, so nothing is ever fetched from the internet). Art is served at
  `https://vanta.example/art/<appid>/<cover|hero|logo>` from the ArtCache. The CSP is strict (`connect-src 'none'`).
* **Engine thread.** Owns the `TrainerController`. It processes the UI message queue, runs `Poll()` every 1 s
  (process list) and `Tick()` every 200 ms (freeze and value polling).
* **Bridge.** The UI talks to the host with `chrome.webview.postMessage(json)`. Each message gets an `ack` plus
  `state`/`status`/`game`/`log`/`settings`/`focusGame`/`hotkey` events (see `ui/shared/bridge.js`).
* **Lazy catalog.** The library is injected from `index.json` only (no cheats). A full definition is loaded when a
  game is selected. The UI renders at most 250 items per group; search and the category filter narrow the rest.
  1200 games: index load takes about 20 ms, and UI start is under 3 s.
* **Restore guarantees.** Every write keeps the original bytes and is undone in reverse order on: disable, "Alles uit",
  detach, app close (FormClosing), session end, ProcessExit, and unhandled exceptions. A cave is freed only after its
  hook is restored, plus a 300 ms grace period. When the game exits, the session is abandoned without writing. When it
  restarts, the controller re-attaches and re-applies the desired cheats.
* **Hotkeys.** `RegisterHotKey` runs on a message-only window, and bindings come from `Settings.Hotkeys` overrides plus
  the definition defaults. They are suspended while the UI captures a new combination. Toggles use MOD_NOREPEAT; +/- may repeat.

### Elevation: `asInvoker` (deliberate)
Steam games run as the normal user, and a same-user process can be opened with `PROCESS_VM_*` without admin rights.
Running Vanta elevated would make `steam://run/<id>` start Steam, and therefore the game, elevated too. That is bad
practice and causes permission problems. If a game does run elevated, `OpenProcess` fails and the UI says
"Kan het proces niet openen (...). Start Vanta als administrator." (right-click, then "Als administrator uitvoeren").

## game.json (schema v1)
Required fields: `schemaVersion, id, name, processNames, antiCheat, cheats`.
Optional fields: `steamAppId, launcherProcesses, module, supportedVersions[{label, fileVersion, productVersion,
peTimestamp, moduleSize, sha256, fileSize, headSha256, tailSha256, build}], onlineOnly, categories, badge, author, source,
notes, art, stores, launchExe, antiCheatFiles, antiCheatModules, scope`.

* `stores`: `{steam:{appId}, ubisoft:{ids[]}, epic:{appNames[]}, gog:{ids[]}, ea:{offerIds[], registryKeys[{key,value}]},
  xbox:{packageFamilyName, appId}}`. The detector checks every store and picks the one where the game is really installed
  (Steam only counts when `SizeOnDisk > 0`, the "fully installed" state flag is set and the folder/launch exe exist, so a
  Steam copy that only "owns" a Ubisoft game is reported as owned, not installed). Launch: `steam://run/<id>`,
  `uplay://launch/<id>/0`, `com.epicgames.launcher://apps/<AppName>?action=launch&silent=true`, GOG exe (or
  `goggalaxy://openGameView/<id>`), `origin2://game/launch?offerIds=`, `shell:AppsFolder\<PFN>!<AppId>`. If the URI fails,
  the direct exe (`launchExe`) is tried. Xbox/Game Pass apps are usually protected (UWP); opening them fails with a clear message.
* `launchExe`: exe relative to the install folder (e.g. `bin/FarCry6.exe`).
* `antiCheatFiles` / `antiCheatModules`: files in the install or modules in the process whose presence means an anti-cheat
  is installed/running. Vanta then refuses to launch or attach (used for old Far Cry 5 builds with EasyAntiCheat).
* `scope`: one line shown in the hero (e.g. "Alleen voor de solo-campagne").
* `author`, `source` and `confidenceNote` are internal and never shown in the UI. The UI only shows a neutral warning per
  cheat ("Niet geverifieerd voor deze versie." for `untested`, a crash warning for `experimental`,
  "Werkt niet in deze versie." for `broken`).
* `confidence: "broken"`: the cheat is greyed out and its toggle is locked. The host refuses `toggle` (and hotkeys) for
  it unless the UI message carries `force: true` ("Toch proberen"). Turning it off always works. The loader lower-cases
  confidence and maps unknown values to `untested`; the validator warns when a broken cheat is `autoEnable` or required.
* `module` can also be set per impl (`impl.module`); the default is the game `module`, else `processNames[0]`.

Cheat fields: `id, name, names{en}, section, type (toggle|number|slider|button), icon, hotkey/hotkeyInc/hotkeyDec,
description, hint, sub, confidence (confirmed|untested|experimental|broken), confidenceNote, requires[], hidden, autoEnable,
min/max/step/format/resetValue, buttonLabel, impl`.

`impl` types:
* `aobPatch`: `sites{name:{patterns[{aob, offset}], expect, checks[{u8At|i32At, min, max, not[]}], anyMemory}}`
  plus `patches[{site, offset, bytes}]`. Patterns are tried in order, and the first one with exactly one hit wins.
  Otherwise the error is "geen unieke AOB gevonden (patroon 1: 0 treffer(s), patroon 2: 3 treffer(s))" and nothing is written.
* `aobInject`: sites with `overwrite` (5 bytes or more), `asm[]` (CE-style AA assembled at apply time against the real
  cave address), `hooks[{site, label}]` (a jmp to the label, NOP-padded, abs64 when 14 bytes or more and out of range), and `exports[]`
  (labels other cheats can use as `sym:NAME`). Symbols include `<site>`, `<site>_ret`, `cave`, and exported labels of
  active cheats. Placeholders: `${orig:site[:start:len]}`, `${i32:site:off[:±adj]}`, `${u8:site:off}`. The cave is
  allocated within ±2 GB of the site.
* `pointer`: `base` (`"Mod.exe+1234"` | `"sym:NAME±hex"` | `{aob, patternOffset, ripOffset, insnLength, module}`),
  `offsets[]` (CE order: first deref first), `valueType (int32|int64|float|double|byte|int16)`, `onValue/offValue/freeze`
  for toggles, `action (set|add)/amount` for buttons.

Validate with `vanta-tool validate games` (or `Vanta.exe validate <map>`). This runs the schema check plus semantic
checks: AOBs parse, sites and requires exist, hotkeys are unique, the asm assembles against fake addresses, and `sym:`
bases are exported by a required cheat.

## Adding games
1. Import a table with `vanta-tool import "Game.CT" -o games/<id> --id <id> --name "..." --process Game.exe --appid 12345`.
   The importer handles aobscanmodule/aobscan, alloc (merged into one cave), labels, `site+off` hooks, jmp+nop,
   DISABLE db bytes (overwrite length), registersymbol, and pointer records. Lua/luacall/readmem entries are listed as `[!!]` in
   `import-report.txt`.
2. Fix the flagged entries by hand, set `antiCheat`/`onlineOnly` honestly, and add `supportedVersions` fingerprints
   (the log prints `fileVersion=..., peTimestamp=..., moduleSize=...` on attach).
3. Run `vanta-tool validate games` and `vanta-tool index games` (the app also rebuilds a stale index itself).
4. Check every AOB against the real game without running it: `Vanta.exe --verify <id> [module-file|install-folder]`
   (or `--live` against the running game, read-only). Each site must have exactly 1 hit. The report ends with a
   `supportedVersions` snippet (fileSize/headSha256/tailSha256) to paste into game.json once the build is confirmed.

The assembler also accepts CE conveniences: `(float)1.5`/`(double)`/`(int)` immediates, whitespace-separated `dd`/`dq` lists,
`@@:` with `@f`/`@b`, `short`/`near` (ignored: label branches are always rel32), `imul r,imm`, and module names as symbols
(`call Game.dll+1234`; prefer site-relative targets such as `call site-265C4` because fixed RVAs break on rebuilds).

Third-party tables are not in the repo (set `VANTA_TABLES` / `VANTA_LUA_TABLE` to run the importer tests against local
copies). Typical importer results on community tables: 15-38 converted entries per table with 0 validation errors;
Lua-only tables (0 convertible) are ported by hand (`tools/make_tlc.py` for TLC; the Far Cry definitions were curated
from several imports).

## Far Cry 5 / Far Cry 6 (v0.2)
* FC6: no anti-cheat (Denuvo/VMProtect DRM only). Module `FC_m64d3d12.dll`; Steam 2369390 (often only a Ubisoft Connect
  stub), Ubisoft ids 5266 and 920. Target build: "1.8.0" (20-07-2025, FC_m64d3d12.dll 518,275,080 bytes).
* FC5: EasyAntiCheat was removed in the final patch (Nov 2019). Old installs that still have EAC files are refused via
  `antiCheatFiles`/`antiCheatModules`. Module `FC_m64.dll`; Steam 552520; target Steam build 18766066 (July 2025, which
  rebuilt FC_m64.dll, so 2018 AOBs may no longer match). Ubisoft id 856 is unconfirmed.
* Every cheat is `untested` or `experimental`; the internal `confidenceNote` says which table era/game version it came
  from (no author names). Confirm with `--verify`.
* Deliberately left out: teleports, HUD, one-hit kills for vehicles/animals, co-op features, FC5 silver bars (premium
  currency that is also sold for real money) and "unlock all store items", fixed-address pointer cheats.

## Local test status (v0.2.1)

Right-click a cheat row (or a row in Notities) for Werkt / Werkt niet / Niet getest / Standaard (uit game.json).
`StatusStore` saves it in `%LOCALAPPDATA%\Vanta\status.json`:

```json
{ "schema": 1, "games": { "<gameId>": { "name": "...", "lastVersion": "<key>",
  "versions": { "<key>": { "label": "...", "updated": "...", "cheats": { "<cheatId>": "works|broken|untested" } } } } } }
```

The version key is the attached exe's fingerprint without the file size (`fileVersion=…, peTimestamp=…, moduleSize=…`).
When the game isn't running, the last seen key is used, else `label:<supportedVersions[0].label>`.
Effective confidence: works → confirmed (green check), broken → broken, untested → untested, none → game.json.
UI messages: `{type:'setStatus', gameId, id, status|null}` and `{type:'exportStatus'}`. The host answers the export with
`{type:'statusExport', json, path}` and writes `teststatus-export-<yyyyMMdd-HHmmss>.json` next to status.json. The UI copies
the JSON to the clipboard. The export lists per game/version/cheat the local `status` and the `gameJson` value.

## Auto-updater (v0.2)
* `AppUpdater` checks `https://api.github.com/repos/Rick007110/Vanta/releases/latest` 5 s after the UI is ready, every
  30 min, and when the window is activated (at most once per 5 min; `UpdatePolicy`). A version is offered at most once per
  session; "Later" or the toast's close button (`updateDismiss`) suppresses it until the next start, a manual check always
  shows it. Download progress is throttled to ~10 messages/s (`ProgressThrottle`). The UI creates the toast element once
  (single enter animation); state changes swap its children and progress only sets the bar's `scaleX` and the percentage
  text. It is unauthenticated (60 requests/h per IP is plenty) and quiet when offline, rate-limited (403 with
  `x-ratelimit-remaining: 0` or 429) or when no release exists. Drafts and pre-releases are ignored; versions are compared
  as SemVer against the assembly version.
* A release must have `Vanta-v<version>.zip` plus `Vanta-v<version>.zip.sha256` (sha256sum format). Without a matching hash
  the update is refused. Downloads go to `%LOCALAPPDATA%\Vanta\updates\download\<version>\`.
* Toast "Update beschikbaar: vX.Y.Z" with the release notes. **Nu updaten**: download + verify, restore all games
  (`Host.Shutdown`), copy Vanta.exe to `updates\helper\Vanta-updater.exe` and start it with
  `--apply-update --pid <pid> --app <dir>`, then close. **Later**: download + verify in the background and write
  `updates\pending.json`; the next start shows the "Vanta wordt bijgewerkt…" splash and runs the helper before the UI.
* Helper: waits for the old process; if the app folder is not writable (Program Files) it restarts itself with `runas`
  (UAC). It verifies the hash again, extracts to `updates\staging` (zip-slip checked; the package must contain Vanta.exe),
  copies the app folder to `updates\backup`, and copies the new files over the app folder. This is a merge, so files
  that are not in the package (such as user-added game folders) stay. It then starts the new version, which writes
  `updates\started-<version>.ok` when its UI is ready. No marker within 90 s, or an early exit, means rollback: the
  backup is restored, files that only the new version added are removed, the version goes into `updates\failed.json`
  (so it is not offered automatically again) and the old version is started with `--update-failed`. From an elevated
  helper the app is started through explorer.exe so it runs as the normal user again.
* Never touched: `%LOCALAPPDATA%\Vanta` (settings, logs, art, WebView2 data, user games) apart from `updates\`.
* Manual: Settings → "Controleer op updates". `Vanta.exe --no-update` skips a pending install once.
* Catalog updates separate from app updates (design, not active): publish `catalog.zip` (games/ + index.json) as a release
  asset, or serve `games/index.json` + `games/<path>/game.json` raw from the repo for the `RemoteCatalog` below. The
  catalog is data only, so it can be refreshed without replacing the exe; it would get its own `.sha256` and stamp check.

## Remote catalog (designed, not active)
`RemoteCatalog(url, cacheDir)` reads `{url}/index.json` (same format as `games/index.json`) and `{url}/{path}/game.json`,
and caches both under `%LOCALAPPDATA%\Vanta\catalog-cache` so the app works offline. Definitions are data only.
The asm is assembled locally, and a remote catalog can never switch off the antiCheat refusal. Settings already has `catalogUrl`.
To activate it, choose `RemoteCatalog` in `Host` when the URL is set, and add signature checking before shipping.

## Tests
* `dotnet test`: 226+ xunit tests (store providers with fixtures, --verify on the dummy, updater: semver, release parsing, hash checks, apply/merge/rollback, pending flow, Far Cry definitions). They cover the AOB scanner (wildcards, naive-vs-fast, chunk boundaries, exec filter);
  the assembler (51 cases byte-identical to GNU as, labels/rip/branches decoded with Iced, errors); every TLC code cheat
  applied to a synthetic image (hook jumps to the cave, the cave jumps back to site+overwrite, restore is byte-identical,
  caves freed); not-unique errors with hit counts; rollback on a failed write; pointer chains; freeze; the controller
  (attach delay, restart re-apply, launch debounce, anti-cheat refusal, version mismatch, hotkeys); the validator;
  the importer; the catalog (1200 games, stale index); the hotkey parser; and the art cache.
* `Vanta.exe --selftest` (Windows): 26 checks against `selftest/vanta_dummy.exe`, which uses the same byte patterns
  as TLC battery/XP. It covers the AOB patch, code cave, exported symbol, AOB+RIP static pointer, freeze, byte-exact
  restore, cave freed, auto-attach, the game exit/restart re-apply, restore on shutdown, and the anti-cheat refusal.
  It passed under wine 10 on the build box.
* `node tests/ui/ui.e2e.js`: headless Chrome with a fake WebView2 host and the production CSP (38 checks + screenshots;
  env `PUPPETEER_CORE`, `CHROME`, `OUT`). Includes the store chip / "Start via …", the scope line and the update toast.
