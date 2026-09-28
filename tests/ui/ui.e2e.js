'use strict';
// Headless Chrome check of the Vanta UI with a fake WebView2 host (window.chrome.webview + window.vantaHost),
// served under the same CSP as Vanta.exe. Payloads come from ui/shared/devdata.js (= real TrainerController output).
// Art requests to https://vanta.example/art/<appid>/<kind> are answered from tests/ui/art (Steam CDN copies, test only).
const http = require('http');
const fs = require('fs');
const path = require('path');
const assert = require('assert');
const puppeteer = require(process.env.PUPPETEER_CORE || 'puppeteer-core');
const UI = path.join(__dirname, '..', '..', 'ui');
const OUT = process.env.OUT || path.join(__dirname, 'out');
const VER = process.env.VER || 'v0.2';
const CSP = "default-src 'self'; img-src 'self' https://vanta.example https://cdn.discordapp.com data:; style-src 'self' 'unsafe-inline'; script-src 'self' 'unsafe-inline'; font-src 'self'; connect-src 'none'";
const MIME = { '.html': 'text/html', '.js': 'text/javascript', '.css': 'text/css', '.ttf': 'font/ttf', '.png': 'image/png', '.jpg': 'image/jpeg' };

// Long real-world Steam names (from the v0.3.2 bug report: dialog overflowed sideways and cut names off).
const LONG_SEARCH = [   // base games only (the host drops DLC/soundtracks/packs); long names + one unbreakable token
  { appid: 2840770, name: 'Avatar: Frontiers of Pandora™', image: null, cover: null },
  { appid: 2183900, name: 'Warhammer 40,000: Space Marine 2', image: null, cover: null },
  { appid: 1811260, name: 'The Legend of Heroes: Trails through Daybreak II - Deluxe Edition (Digital Artbook Bundle)', image: null, cover: null },
  { appid: 489830, name: 'The Elder Scrolls V: Skyrim Special Edition', image: null, cover: null },
  { appid: 999001, name: 'SUPERLONGSINGLEWORDGAMENAMEWITHOUTANYSPACESDEFINITIVEEDITION2026', image: null, cover: null },
];
const LONG_REQ = [
  { appid: 1086940, name: 'Baldur’s Gate 3 - Digital Deluxe Edition Soundtrack & Artbook Bundle', cover: null, status: 'planned', note: 'Na de volgende grote patch, zodra de offsets stabiel zijn', votes: 48, votes7d: 9, voted: false },
  { appid: 2840770, name: 'VALLEY OF MO’ARA STARTER PACK - AVATAR: FRONTIERS OF PANDORA™', cover: null, status: 'in_progress', note: null, votes: 31, votes7d: 4, voted: true },
  { appid: 359550, name: 'Tom Clancy’s Rainbow Six® Siege X - Deluxe Edition (Year 10 Pass)', cover: null, status: 'rejected', note: 'Online anti-cheat', votes: 22, votes7d: 1, voted: false },
  { appid: 1245620, name: 'ELDEN RING NIGHTREIGN - Deluxe Edition Upgrade', cover: null, status: 'open', note: null, votes: 17, votes7d: 2, voted: false },
  { appid: 264710, name: 'Subnautica', cover: null, status: 'open', note: null, votes: 12, votes7d: 3, voted: false },
  { appid: 105600, name: 'Terraria', cover: null, status: 'open', note: null, votes: 4, votes7d: 1, voted: false },
];

global.window = {};
eval(fs.readFileSync(path.join(UI, 'shared', 'devdata.js'), 'utf8'));
const DEV = global.window.VantaDev;

function hostScript({ many = 0, status = 'attached', blocked = false, account = null, reqDown = false, longReq = false, longCheat = false, lang = 'nl' } = {}) {
  // English is the app default; most checks below run in Dutch (secondary language) with the Dutch fixture
  const SRC = lang === 'nl' ? DEV.nl : DEV;
  const lib = JSON.parse(JSON.stringify(SRC.library));
  for (let i = 0; i < many; i++) lib.games.push({ id: `game-${i}`, name: `Testgame ${String(i + 1).padStart(4, '0')}`, short: 'TG', badge: 'v1.' + (i % 9), version: '', cheatCount: 3 + (i % 20),
    steamAppId: null, categories: [['survival', 'rpg', 'shooter', 'strategy', 'racing', 'sim'][i % 6]], antiCheat: i % 97 === 0, onlineOnly: false, group: 'all', art: null, process: `Game${i}.exe`, cheats: [], lazy: true });
  if (blocked) lib.games.push({ id: 'online-shooter', name: 'Online Shooter X', short: 'OS', badge: '', version: '', cheatCount: 0, steamAppId: null, categories: ['shooter'], antiCheat: true, onlineOnly: true, group: 'all', art: null, process: 'shooter.exe', cheats: [], lazy: true });
  return `
window.__posted = []; window.__csp = [];
document.addEventListener('securitypolicyviolation', (e) => window.__csp.push(e.violatedDirective + ' ' + e.blockedURI));
window.vantaHost = { library: ${JSON.stringify(lib)} };
(function () {
  const games = ${JSON.stringify(SRC.games)};
  // fixture: one cheat marked "broken" in game.json
  const tlc = games['the-last-caretaker'];
  if (tlc) tlc.cheats.forEach((c) => { c.baseConfidence = c.confidence; if (c.id === 'inf_jump') { c.confidence = c.baseConfidence = 'broken'; c.note = ${JSON.stringify(lang === 'nl' ? 'Werkt niet in deze versie.' : 'Does not work in this version.')}; } });
  if (tlc && ${JSON.stringify(!!longCheat)}) tlc.cheats.forEach((c) => { if (c.id === 'inf_health') c.name = 'Infinite Health, Stamina and Oxygen (incl. fall damage, drowning and radiation)'; });
  const CONF = { works: 'confirmed', broken: 'broken', untested: 'untested' };
  const NOTE = { confirmed: null, broken: 'Werkt niet in deze versie.', untested: 'Niet geverifieerd voor deze versie.', experimental: 'Experimenteel: kan de game laten crashen. Sla eerst op.' };
  const listeners = [];
  const send = (m) => setTimeout(() => listeners.forEach((fn) => fn({ data: m })), 10);
  const st = { status: ${JSON.stringify(status)} };
  const acc = ${JSON.stringify(account)};
  const accMsg = () => Object.assign({ type: 'account', busy: false, error: null, pending: 0, shareUsage: false }, acc, { user: acc && acc.loggedIn ? { id: '400000000000000001', username: 'tester', avatarUrl: 'https://cdn.discordapp.com/embed/avatars/1.png' } : null });
  const REQ = { down: ${JSON.stringify(!!reqDown)}, items: [{ appid: 264710, name: 'Subnautica', cover: null, status: 'planned', note: null, votes: 12, votes7d: 3, voted: false },
    { appid: 105600, name: 'Terraria', cover: null, status: 'open', note: ${JSON.stringify(lang === 'nl' ? 'Na de volgende update' : 'After the next update')}, votes: 4, votes7d: 1, voted: false }] };
  if (${JSON.stringify(!!longReq)}) REQ.items = ${JSON.stringify(LONG_REQ)};
  const COMM = { inf_health: { works: 3, broken: 12, status: 'open', fixedInVersion: null }, no_weight: { works: 0, broken: 0, status: 'fixed', fixedInVersion: '0.2.3' } };
  window.__hostSend = send;
  window.chrome = { webview: {
    addEventListener: (t, fn) => { if (t === 'message') listeners.push(fn); },
    postMessage: (m) => {
      window.__posted.push(m);
      const ack = (ok = true, error) => send({ type: 'ack', reqId: m.reqId, ok, error });
      const gid = m.gameId;
      if (m.type === 'ready' || m.type === 'selectGame') {
        const id = gid || ${JSON.stringify(lib.selected)};
        const g = games[id] || Object.assign({}, window.vantaHost.library.games.find((x) => x.id === id), { cheats: [], lazy: false, notes: [] });
        send({ type: 'game', game: g });
        const blocked = g.antiCheat || g.onlineOnly;
        send({ type: 'status', gameId: id, process: blocked ? 'blocked' : (games[id] ? st.status : 'notfound'), detail: blocked ? g.name + ': online/anti-cheat game. Vanta koppelt hier niet aan (alleen singleplayer/offline).' : null, pid: 11820 });
        if (games[id] && st.status === 'attached') send({ type: 'state', gameId: id, cheats: [
          { id: 'inf_battery', enabled: true, error: null }, { id: 'no_weight', enabled: true, error: null },
          { id: 'xp_value', value: 12450, hint: null, error: null }, { id: 'xp_mult', value: 3, hint: null, error: null } ] });
        if (m.type === 'ready' && acc) send(accMsg());
        if (m.type === 'ready') send({ type: 'settings', settings: { language: ${JSON.stringify(lang)}, catalogDir: '', catalogUrl: '', attachDelaySec: 4, autoAttach: true }, catalog: { source: 'C:\\\\Games\\\\Vanta\\\\games', games: window.vantaHost.library.games.length }, dataDir: '%LOCALAPPDATA%\\\\Vanta', version: '0.1.0' });
        return ack();
      }
      if (m.type === 'toggle') {
        if (m.id === 'inf_grenades' && m.enabled) {
          const err = 'Infinite Grenades: geen unieke AOB gevonden (patroon 1: 0 treffer(s), patroon 2: 3 treffer(s), patroon 3: 3 treffer(s)). Niets gepatcht.';
          send({ type: 'state', gameId: gid, cheats: [{ id: m.id, enabled: false, error: err }] });
          send({ type: 'log', time: '04:31', text: err, level: 'error' });
          return ack(false, err);
        }
        send({ type: 'state', gameId: gid, cheats: [{ id: m.id, enabled: m.enabled, error: null }] });
        send({ type: 'log', time: '04:31', text: 'Cheat ' + (m.enabled ? 'aangezet' : 'uitgezet'), level: 'info' });
        return ack();
      }
      if (m.type === 'saveSettings' && m.settings.hotkeys) {
        const g = JSON.parse(JSON.stringify(games[gid || m.settings.gameId]));
        g.cheats.forEach((c) => { if (c.id in m.settings.hotkeys) c.hotkey = m.settings.hotkeys[c.id] || null; });
        send({ type: 'game', game: g });
        return ack();
      }
      if (m.type === 'setStatus') {
        const g = games[gid]; const c = g && g.cheats.find((x) => x.id === m.id);
        if (!c) return ack(false, 'onbekende cheat');
        c.localStatus = m.status || null; c.confidence = m.status ? CONF[m.status] : c.baseConfidence; c.note = NOTE[c.confidence] || null;
        send({ type: 'game', game: JSON.parse(JSON.stringify(g)) });
        return ack();
      }
      if (m.type === 'exportStatus') {
        const out = { vanta: '0.2.1', schema: 1, games: {} };
        Object.values(games).forEach((g) => g.cheats.filter((c) => c.localStatus).forEach((c) => {
          const e = out.games[g.id] = out.games[g.id] || { name: g.name, versions: { 'fileVersion=0.8.5.651238': { label: g.version, cheats: {} } } };
          e.versions['fileVersion=0.8.5.651238'].cheats[c.id] = { status: c.localStatus, gameJson: c.baseConfidence };
        }));
        send({ type: 'statusExport', json: JSON.stringify(out, null, 2), path: '%LOCALAPPDATA%\\\\Vanta\\\\teststatus-export-20260927-150000.json' });
        return ack();
      }
      if (acc && m.type === 'accountLogin') { acc.busy = true; send(accMsg()); setTimeout(() => { acc.busy = false; acc.loggedIn = true; send(accMsg()); }, 300); return ack(); }
      if (acc && m.type === 'accountLogout') { acc.loggedIn = false; send(accMsg()); return ack(); }
      if (acc && m.type === 'accountDelete') { acc.busy = true; send(accMsg()); setTimeout(() => { acc.busy = false; acc.loggedIn = false; send(accMsg()); send({ type: 'accountDeleted' }); }, 200); return ack(); }
      if (acc && (m.type === 'report' || m.type === 'withdraw')) {
        const offline = m.note && /offline/.test(m.note);
        const status = m.type === 'withdraw' ? null : m.status;
        const community = offline || !status ? null : status === 'broken' ? { works: 3, broken: 13, status: 'open', fixedInVersion: null } : { works: 4, broken: 12, status: 'open', fixedInVersion: null };
        setTimeout(() => send({ type: 'reportResult', gameId: gid, id: m.id, status, ok: !offline, queued: !!offline, error: offline ? 'offline' : null, fingerprint: 'fileVersion=0.8.5.651238', community }), 120);
        return ack();
      }
      if (acc && m.type === 'getCommunity') { send({ type: 'community', gameId: gid, fingerprint: 'fileVersion=0.8.5.651238', available: true, cheats: COMM }); return ack(); }
      if (m.type === 'getRequests') { setTimeout(() => send({ type: 'requests', ok: !REQ.down, error: REQ.down ? 'server_not_ready' : null, loggedIn: !!(acc && acc.loggedIn), items: REQ.down ? null : REQ.items }), 40); return ack(); }
      if (m.type === 'steamSearch' && m.term === '700') { send({ type: 'steamSearch', term: m.term, ok: false, error: 'not_a_game', items: [] }); return ack(); }
      if (m.type === 'steamSearch' && ${JSON.stringify(!!longReq)}) { send({ type: 'steamSearch', term: m.term, ok: true, error: null, items: ${JSON.stringify(LONG_SEARCH)} }); return ack(); }
      if (m.type === 'steamSearch') { send({ type: 'steamSearch', term: m.term, ok: true, error: null, items: [{ appid: 264710, name: 'Subnautica', image: null, cover: null }, { appid: 848450, name: 'Subnautica: Below Zero <b>x</b>', image: null, cover: null }] }); return ack(); }
      if (m.type === 'requestVote' || m.type === 'requestUnvote') {
        let r = REQ.items.find((x) => x.appid === m.appid);
        if (!r) { r = { appid: m.appid, name: m.name, cover: null, status: 'open', note: null, votes: 0, votes7d: 0, voted: false }; REQ.items.push(r); }
        r.voted = m.type === 'requestVote'; r.votes += r.voted ? 1 : -1;
        setTimeout(() => { send({ type: 'requestVoteResult', appid: m.appid, unvote: !r.voted, ok: true, error: null, request: Object.assign({}, r) }); send({ type: 'requests', ok: true, error: null, loggedIn: true, items: REQ.items.slice().sort((a, b) => b.votes - a.votes) }); }, 60);
        return ack();
      }
      if (m.type === 'checkUpdate') { send({ type: 'updateStatus', state: { state: 'uptodate', version: '0.2.0' } }); return ack(); }
      if (m.type === 'updateNow') { send({ type: 'updateStatus', state: 'downloading', progress: 0.42, version: '0.3.0' }); return ack(); }
      if (m.type === 'getSettings') { send({ type: 'settings', settings: { language: ${JSON.stringify(lang)}, catalogDir: '', catalogUrl: '', autoAttach: true }, catalog: { source: 'games', games: window.vantaHost.library.games.length }, dataDir: '%LOCALAPPDATA%\\\\Vanta', version: '0.1.0' }); return ack(); }
      return ack();
    } } };
})();`;
}

let hostJs = hostScript();
const server = http.createServer((req, res) => {
  const u = new URL(req.url, 'http://x');
  if (u.pathname === '/__host.js') { res.writeHead(200, { 'Content-Type': 'text/javascript' }); return res.end(hostJs); }
  const file = path.join(UI, decodeURIComponent(u.pathname === '/' ? '/index.html' : u.pathname));
  if (!file.startsWith(UI) || !fs.existsSync(file)) { res.writeHead(404); return res.end(); }
  let body = fs.readFileSync(file);
  if (file.endsWith('index.html')) body = Buffer.from(body.toString().replace('<script src="shared/brand.js">', '<script src="/__host.js"></script>\n  <script src="shared/brand.js">'));
  res.writeHead(200, { 'Content-Type': MIME[path.extname(file)] || 'application/octet-stream', 'Content-Security-Policy': CSP });
  res.end(body);
});

const results = [];
const check = (name, fn) => { try { fn(); results.push(['OK', name]); } catch (e) { results.push(['FAIL', name + ': ' + e.message]); } };
const sleep = (ms) => new Promise((r) => setTimeout(r, ms));

(async () => {
  await new Promise((r) => server.listen(0, '127.0.0.1', r));
  const base = `http://127.0.0.1:${server.address().port}/index.html`;
  const browser = await puppeteer.launch({ executablePath: process.env.CHROME || '/usr/bin/google-chrome', headless: true, args: ['--no-sandbox', '--disable-gpu', '--hide-scrollbars', '--lang=nl-NL'] });
  const page = await browser.newPage();
  await page.setViewport({ width: 1320, height: 860, deviceScaleFactor: 1 });
  const artHits = [], avatarHits = [];
  const intercept = async (page) => { await page.setRequestInterception(true); page.on('request', (r) => {
    const m = r.url().match(/^https:\/\/vanta\.example\/art\/(\d+)\/(cover|hero|logo|header)/);
    if (m) {
      artHits.push(m[2]);
      const f = path.join(__dirname, 'art', `${m[1]}_${{ cover: 'library_600x900.jpg', hero: 'library_hero.jpg', logo: 'logo.png', header: 'header.jpg' }[m[2]]}`);
      if (fs.existsSync(f)) return r.respond({ status: 200, contentType: f.endsWith('png') ? 'image/png' : 'image/jpeg', body: fs.readFileSync(f) });
      return r.respond({ status: 404, body: '' });
    }
    if (r.url().startsWith('https://cdn.discordapp.com/')) { avatarHits.push(r.url()); return r.respond({ status: 200, contentType: 'image/png', body: Buffer.from('iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mNk+M9QDwADhgGAWjR9awAAAABJRU5ErkJggg==', 'base64') }); }
    if (!r.url().startsWith('http://127.0.0.1')) return r.abort();
    r.continue();
  }); };
  await intercept(page);
  const errors = [];
  page.on('pageerror', (e) => errors.push(e.message));
  page.on('console', (m) => { if (m.type() === 'error' && !/Failed to load resource/.test(m.text())) errors.push(m.text()); });

  const load = async (opts) => { hostJs = hostScript(opts); await page.goto(base, { waitUntil: 'networkidle0' }); await sleep(500); };
  const text = (sel) => page.$eval(sel, (e) => e.textContent.trim());
  const shot = async (name) => { await sleep(350); await page.screenshot({ path: path.join(OUT, name) }); results.push(['SHOT', path.join(OUT, name)]); };

  // 0. English is the default language (fresh install / no explicit choice): UI, game texts, dialogs, release notes
  await load({ lang: 'en', account: { configured: true, loggedIn: false } });
  const en0 = await page.evaluate(() => ({ html: document.documentElement.lang, st: document.querySelector('[data-bind=status-label]').textContent,
    tab: document.querySelector('[data-action=tab][data-value=hotkeys]').textContent, ph: document.querySelector('[data-role=search], input[type=search]') && document.querySelector('[data-role=search], input[type=search]').placeholder,
    req: document.querySelector('[data-action=open-requests]') && document.querySelector('[data-action=open-requests]').textContent.trim(),
    cheat: document.querySelector('[data-cheat=inf_battery] .cheat-name, [data-cheat=inf_battery] [data-bind=name]') ? document.querySelector('[data-cheat=inf_battery] .cheat-name, [data-cheat=inf_battery] [data-bind=name]').textContent.trim() : null }));
  check('English default: lang=en, status Attached, Hotkeys tab, English search + Request a game', () => assert.deepStrictEqual({ html: en0.html, st: en0.st, tab: en0.tab, ph: en0.ph, req: en0.req },
    { html: 'en', st: 'Attached', tab: 'Hotkeys', ph: 'Search games…', req: 'Request a game' }));
  const dutchWords = /\b(Gekoppeld|Sneltoetsen|Instellingen|Zoek|Notities|Alles uit|aanvragen|Oneindige|Geen|niet|voor|deze)\b/;
  const enBody = await page.evaluate(() => document.querySelector('#app').innerText);
  check('English default: no Dutch words in the main window', () => assert.ok(!dutchWords.test(enBody), (enBody.match(dutchWords) || [])[0]));
  const keys = await page.evaluate(() => { const S = window.I18N.strings; const en = Object.keys(S.en), nl = Object.keys(S.nl); return { en: en.filter((k) => !(k in S.nl)), nl: nl.filter((k) => !(k in S.en)) }; });
  check('i18n: English and Dutch tables have the same keys', () => assert.deepStrictEqual(keys, { en: [], nl: [] }));
  await page.click('[data-action=tab][data-value=notes]'); await sleep(150);
  const enNotes = await page.evaluate(() => document.querySelector('#app').innerText);
  check('English default: game notes (game.json) in English', () => assert.ok(!dutchWords.test(enNotes) && /Supported version/.test(enNotes), (enNotes.match(dutchWords) || [])[0]));
  await page.click('[data-action=tab][data-value=cheats]'); await sleep(150);
  await page.click('[data-action=open-settings]'); await sleep(250);
  const enSet = await page.evaluate(() => { const s = document.querySelector('[data-set=language]'); return { val: s.value, opts: [...s.options].map((o) => o.value + ':' + o.textContent) }; });
  check('English default: Settings language = English, Dutch still selectable', () => assert.deepStrictEqual(enSet, { val: 'en', opts: ['en:English', 'nl:Nederlands'] }));
  await page.keyboard.press('Escape'); await sleep(200);
  const picked = await page.evaluate(() => ({ en: window.VantaMd.pick(window.VantaMockReleaseNotes, 'en'), nl: window.VantaMd.pick(window.VantaMockReleaseNotes, 'nl'), plain: window.VantaMd.pick('## Nieuw\n- x', 'en') }));
  check("What's new: English part by default, Dutch <details> part for nl, plain notes unchanged", () => assert.ok(/^## New/.test(picked.en) && !/Nieuw|<details>|Nederlands/.test(picked.en) && /Full Changelog/.test(picked.en)
    && /^## Nieuw/.test(picked.nl) && !/## New|<details>/.test(picked.nl) && /Full Changelog/.test(picked.nl) && picked.plain === '## Nieuw\n- x', JSON.stringify(picked)));
  await page.evaluate(() => window.__hostSend({ type: 'update', manual: true, current: '0.3.3', release: { state: 'available', version: '0.3.4', notes: window.VantaMockReleaseNotes, url: 'https://github.com/Rick007110/Vanta/releases/tag/v0.3.4', size: 64000000 } }));
  await sleep(300);
  const enToast = await page.evaluate(() => document.querySelector('.toast, [data-role=update], .upd') ? (document.querySelector('.toast, [data-role=update], .upd').innerText) : document.body.innerText);
  check('English default: update toast in English', () => assert.ok(/Update available: v0\.3\.4/.test(enToast) && /What's new/.test(enToast), enToast.slice(0, 200)));
  await page.click('[data-action=whats-new]'); await sleep(300);
  const enWn = await page.evaluate(() => { const b = document.querySelector('[data-role=notes]'); return b ? b.innerText : ''; });
  check("English default: What's new shows only the English notes", () => assert.ok(/New/.test(enWn) && /Request a game/.test(enWn) && !/Nieuw|Nederlands|details/.test(enWn), enWn.slice(0, 200)));
  await page.keyboard.press('Escape'); await sleep(200);
  await load({ lang: 'en', account: { configured: true, loggedIn: false } });
  await page.mouse.move(0, 0);
  await shot(`vanta-${VER}-english.png`);
  if (process.env.SHOT_EN) fs.copyFileSync(path.join(OUT, `vanta-${VER}-english.png`), process.env.SHOT_EN);
  await page.click('[data-action=open-requests]'); await sleep(250);
  const enReq = await page.evaluate(() => ({ title: document.querySelector('#req-title').textContent, body: document.querySelector('.modal-requests').innerText }));
  check('English default: Request a game dialog in English', () => assert.ok(enReq.title === 'Request a game' && !/Stem|aanvragen|Zoek/.test(enReq.body), enReq.body.slice(0, 200)));
  await page.keyboard.press('Escape'); await sleep(200);

  // 1. main, attached (Dutch, the secondary language)
  await load();
  const title = await page.title();
  check('document.title = Vanta', () => assert.strictEqual(title, 'Vanta'));
  const brand = await text('.brand-name');
  check('merknaam Vanta in sidebar', () => assert.strictEqual(brand, 'Vanta'));
  const transport = await page.$eval('#app', (e) => e.dataset.transport);
  check('webview2 transport gebruikt', () => assert.strictEqual(transport, 'webview2'));
  const posted0 = await page.evaluate(() => window.__posted.map((m) => m.type));
  check('ready-bericht verstuurd', () => assert.ok(posted0.includes('ready')));
  const rows = await page.$$eval('[data-cheat]', (els) => els.length);
  check('16 zichtbare cheats (hidden hooks verborgen)', () => assert.strictEqual(rows, 16));
  const stl = await text('[data-bind=status-label]');
  check('status Gekoppeld', () => assert.strictEqual(stl, 'Gekoppeld'));
  const on = await page.$eval('[data-cheat=inf_battery]', (e) => e.dataset.state);
  check('Infinite Battery aan (state van host)', () => assert.strictEqual(on, 'on'));
  const xp = await page.$eval('#v-xp_value', (e) => e.value);
  check('XP-waarde getoond (12.450)', () => assert.strictEqual(xp, '12.450'));
  const heroImg = await page.$eval('.hero-art', (e) => e.classList.contains('has-img'));
  if (fs.existsSync(path.join(__dirname, 'art'))) check('hero-art via https://vanta.example/art geladen', () => assert.ok(heroImg && artHits.includes('hero') && artHits.includes('cover')));
  else check('art-verzoeken naar https://vanta.example/art (geen lokale art: 404)', () => assert.ok(artHits.includes('hero') && artHits.includes('cover')));
  const noDemo = await page.$$eval('.demo, .ph-tag, [data-action=load-table]', (e) => e.length);
  check('geen demo-knoppen/placeholder-tags/inerte knoppen', () => assert.strictEqual(noDemo, 0));
  await page.click('[data-cheat=inf_ammo] [role=switch]');
  await sleep(150);
  const tog = await page.evaluate(() => window.__posted.find((m) => m.type === 'toggle' && m.id === 'inf_ammo'));
  check('toggle-bericht met gameId', () => assert.ok(tog && tog.enabled === true && tog.gameId === 'the-last-caretaker'));
  await page.mouse.move(0, 0);
  await shot(`vanta-${VER}.png`);

  // 2. error with hit counts
  await page.click('[data-cheat=inf_grenades] [role=switch]');
  await sleep(250);
  const err = await text('[data-cheat=inf_grenades] [data-bind=error]');
  check('Nederlandse AOB-fout met treffers per patroon', () => assert.ok(/patroon 2: 3 treffer\(s\)/.test(err), err));
  await shot(`vanta-${VER}-fout.png`);

  // 3. hotkeys tab + capture
  await page.click('[data-action=tab][data-value=hotkeys]');
  await sleep(100);
  const hkRows = await page.$$eval('.hk-row', (e) => e.length);
  check('sneltoetsen-tab: rij per toggle + 2 per waarde', () => assert.ok(hkRows >= 20, String(hkRows)));
  await page.click('[data-hk=inf_battery] [data-action=hk-change]');
  await sleep(80);
  const cap = await page.evaluate(() => window.__posted.filter((m) => m.type === 'hotkeyCapture').map((m) => m.active));
  check('host krijgt hotkeyCapture=true (globale sneltoetsen gepauzeerd)', () => assert.deepStrictEqual(cap, [true]));
  await shot(`vanta-${VER}-sneltoetsen-opnemen.png`);
  await page.keyboard.down('Control'); await page.keyboard.press('F1'); await page.keyboard.up('Control');
  await sleep(200);
  const saved = await page.evaluate(() => window.__posted.filter((m) => m.type === 'saveSettings').pop());
  check('Ctrl+F1 opgeslagen als override', () => assert.strictEqual(saved.settings.hotkeys.inf_battery, 'Ctrl+F1'));
  const chipNow = await page.$eval('[data-hk=inf_battery] .hk-val', (e) => e.textContent.replace(/\s/g, ''));
  check('nieuwe sneltoets zichtbaar', () => assert.strictEqual(chipNow, 'Ctrl+F1'));
  await shot(`vanta-${VER}-sneltoetsen.png`);

  // 4. notes
  await page.click('[data-action=tab][data-value=notes]');
  await sleep(100);
  const badges = await page.$$eval('.badge', (e) => e.map((x) => x.textContent));
  check('notities: betrouwbaarheid per cheat (1 bevestigd)', () => assert.strictEqual(badges.filter((b) => b === 'Bevestigd').length, 1));
  await shot(`vanta-${VER}-notities.png`);
  await page.click('[data-action=tab][data-value=cheats]');

  // 5. settings + language
  await page.click('[data-action=open-settings]');
  await sleep(250);
  const modal = await page.$('.modal');
  check('instellingen-venster opent', () => assert.ok(modal));
  await shot(`vanta-${VER}-instellingen.png`);
  await page.select('[data-set=language]', 'en');
  await page.click('[data-action=modal-save]');
  await sleep(300);
  const lang = await page.evaluate(() => ({ tab: document.querySelector('[data-action=tab][data-value=hotkeys]').textContent, st: document.querySelector('[data-bind=status-label]').textContent, sent: window.__posted.filter((m) => m.type === 'saveSettings').pop().settings.language }));
  check('taal naar Engels (UI + opgeslagen)', () => assert.deepStrictEqual(lang, { tab: 'Hotkeys', st: 'Attached', sent: 'en' }));

  // 6. other statuses
  for (const [st, label, primary] of [['wrongversion', 'Andere versie', 'Toch koppelen'], ['notfound', 'Niet gevonden', 'Start game'], ['error', 'Fout', 'Opnieuw']]) {
    await load({ status: st });
    const got = { label: await text('[data-bind=status-label]'), primary: await text('[data-bind=primary-label]') };
    check(`status ${st}`, () => assert.deepStrictEqual(got, { label, primary }));
    if (st === 'notfound') {
      await page.click('[data-action=primary]'); await page.click('[data-action=primary]');
      await sleep(100);
      const launches = await page.evaluate(() => window.__posted.filter((m) => m.type === 'launch').length);
      check('Start game: 1 launch-bericht bij dubbelklik', () => assert.strictEqual(launches, 1));
      await load({ status: st });
      await shot(`vanta-${VER}-niet-gestart.png`);
    }
    if (st === 'wrongversion') await shot(`vanta-${VER}-andere-versie.png`);
  }

  // 6b. store detection + scope (Far Cry 6 installed via Ubisoft Connect, Steam only owns it)
  await load({ status: 'notfound' });
  await page.click('[data-game=far-cry-6]');
  await sleep(300);
  const fc6 = await page.evaluate(() => ({ primary: document.querySelector('[data-bind=primary-label]').textContent, store: (document.querySelector('[data-bind=store] .meta-v') || {}).textContent,
    dir: (document.querySelector('[data-bind=store]') || { title: '' }).title, scope: (document.querySelector('[data-bind=scope]') || {}).textContent, disabled: document.querySelector('[data-action=primary]').disabled }));
  check('FC6: Start via Ubisoft Connect + winkel-chip + installatiemap', () => assert.deepStrictEqual([fc6.primary, fc6.store, fc6.disabled, /Far Cry 6$/.test(fc6.dir)], ['Start via Ubisoft Connect', 'Ubisoft Connect', false, true]));
  check('FC6: scope-melding (alleen solo) in de hero', () => assert.ok(/solo-campagne/.test(fc6.scope || ''), fc6.scope));
  const rows6 = await page.$$eval('[data-cheat]', (e) => e.length);
  check('FC6: 20 cheats zichtbaar', () => assert.strictEqual(rows6, 20));
  await page.mouse.move(0, 0);
  await shot(`vanta-${VER}-far-cry-6.png`);
  await page.click('[data-action=primary]');
  await sleep(100);
  const l6 = await page.evaluate(() => window.__posted.filter((m) => m.type === 'launch').map((m) => m.gameId));
  check('FC6: launch-bericht voor far-cry-6', () => assert.deepStrictEqual(l6, ['far-cry-6']));
  await page.click('[data-game=far-cry-5]');
  await sleep(300);
  const fc5 = { primary: await text('[data-bind=primary-label]'), rows: await page.$$eval('[data-cheat]', (e) => e.length) };
  check('FC5: Start via Steam, 16 cheats', () => assert.deepStrictEqual(fc5, { primary: 'Start via Steam', rows: 16 }));
  await page.mouse.move(0, 0);
  await shot(`vanta-${VER}-far-cry-5.png`);

  // 6d. no credits/sources in the UI (only neutral warnings)
  const titles = await page.$$eval('[data-cheat]', (els) => [...new Set(els.map((e) => e.title))]);
  check('cheat-tooltips: alleen neutrale waarschuwingen, geen bronnen', () => assert.ok(titles.every((x) => ['', 'Niet geverifieerd voor deze versie.', 'Experimenteel: kan de game laten crashen. Sla eerst op.'].includes(x)), JSON.stringify(titles)));
  await page.click('[data-action=tab][data-value=notes]');
  await sleep(100);
  const notesText = await page.$eval('[data-slot=panel]', (e) => e.textContent);
  check('notities: geen bron/auteur/interne notities', () => assert.ok(!/Bron|openbare Cheat Engine|Rick007110|catalogus \(/i.test(notesText), notesText.slice(0, 200)));
  await page.click('[data-action=tab][data-value=cheats]');

  // 6c. auto-update toast + settings check
  await load();
  await page.evaluate(() => window.__hostSend({ type: 'update', manual: false, current: '0.2.0', release: { state: 'available', version: '0.3.0', notes: '- Nieuwe game: Voorbeeld\n- Snellere AOB-scan', url: 'https://github.com/Rick007110/Vanta/releases/tag/v0.3.0', size: 64000000 } }));
  await sleep(300);
  const toast = await page.evaluate(() => { const e = document.querySelector('.toast'); return e && { title: e.querySelector('.toast-title').textContent, body: !!e.querySelector('.toast-body'), text: e.textContent, btns: [...e.querySelectorAll('button')].map((b) => b.textContent.trim()).filter(Boolean) }; });
  check('update-toast compact: titel + Nu updaten/Wat is er nieuw/Later, geen changelog', () => assert.ok(toast && toast.title === 'Update beschikbaar: v0.3.0' && !toast.body && !/Snellere AOB-scan/.test(toast.text)
    && JSON.stringify(toast.btns) === JSON.stringify(['Nu updaten', 'Wat is er nieuw', 'Later']), JSON.stringify(toast)));
  await page.mouse.move(0, 0);
  await shot(`vanta-${VER}-update.png`);

  // 6c1. "Wat is er nieuw": centered modal with the rendered notes; links go to the host, X/Esc/backdrop close
  await page.evaluate(() => window.__hostSend({ type: 'update', manual: true, current: '0.3.0', release: { state: 'available', version: '0.3.0',
    notes: '## Nieuw\n- **Snellere** AOB-scan\n- Zie [release](https://github.com/Rick007110/Vanta/releases/tag/v0.3.0)\n<img src=x onerror=alert(1)>\n' + Array.from({ length: 40 }, (_, i) => '- regel ' + i).join('\n'),
    url: 'https://github.com/Rick007110/Vanta/releases/tag/v0.3.0', size: 64000000 } }));
  await sleep(200);
  await page.click('[data-action=whats-new]');
  await sleep(300);
  const wn = await page.evaluate(() => { const m = document.querySelector('.modal-notes'), b = m && m.querySelector('[data-role=notes]'); const r = m && m.getBoundingClientRect();
    return m && { h: b.querySelector('h4') && b.querySelector('h4').textContent, strong: (b.querySelector('li strong') || {}).textContent, img: !!b.querySelector('img'), link: (b.querySelector('.md-link') || { dataset: {} }).dataset.url,
      scroll: b.scrollHeight > b.clientHeight && getComputedStyle(b).overflowY === 'auto', centered: Math.abs(r.left + r.width / 2 - innerWidth / 2) < 2 && Math.abs(r.top + r.height / 2 - innerHeight / 2) < 2,
      update: !!m.querySelector('.modal-foot [data-action=update-now]'), sameToast: document.querySelectorAll('.toast').length === 1 }; });
  check('Wat is er nieuw: gecentreerd, markdown (kop/vet/link), scrollbaar, veilig, Update-knop', () => assert.deepStrictEqual(wn, { h: 'Nieuw', strong: 'Snellere', img: false,
    link: 'https://github.com/Rick007110/Vanta/releases/tag/v0.3.0', scroll: true, centered: true, update: true, sameToast: true }));
  await shot(`vanta-${VER}-wat-is-er-nieuw.png`);
  const before = await page.evaluate(() => window.__posted.length);
  await page.click('.md-link'); await sleep(100);
  const lk = await page.evaluate((n) => window.__posted.slice(n).filter((m) => m.type === 'openUrl').map((m) => m.url), before);
  check('link in release-opmerkingen: openUrl naar host (externe browser)', () => assert.deepStrictEqual(lk, ['https://github.com/Rick007110/Vanta/releases/tag/v0.3.0']));
  await page.keyboard.press('Escape'); await sleep(100);
  const escClosed = await page.evaluate(() => ({ modal: !document.querySelector('.modal-notes'), focus: document.activeElement && document.activeElement.dataset.action }));
  check('Esc sluit, focus terug op Wat is er nieuw', () => assert.deepStrictEqual(escClosed, { modal: true, focus: 'whats-new' }));
  await page.click('[data-action=whats-new]'); await sleep(200);
  await page.mouse.click(8, 8); await sleep(100);
  const bdClosed = await page.evaluate(() => !document.querySelector('.modal-notes'));
  await page.click('[data-action=whats-new]'); await sleep(200);
  await page.click('.modal-notes .modal-x'); await sleep(100);
  const xClosed = await page.evaluate(() => !document.querySelector('.modal-notes'));
  check('achtergrond en X sluiten de modal', () => assert.deepStrictEqual([bdClosed, xClosed], [true, true]));
  await page.click('[data-action=update-now]');
  await sleep(250);
  const dl = await page.evaluate(() => ({ sent: window.__posted.filter((m) => m.type === 'updateNow').length, title: document.querySelector('.toast-title-text').textContent, pct: document.querySelector('[data-bind=toast-pct]').textContent, bar: (document.querySelector('.toast-bar i') || { style: {} }).style.transform }));
  check('Nu updaten: bericht naar host + voortgang', () => assert.deepStrictEqual(dl, { sent: 1, title: 'v0.3.0 downloaden…', pct: '42%', bar: 'scaleX(0.42)' }));

  // 6c2. 200 rapid progress messages: same toast element, enter animation not restarted, no jumping, opaque, on top
  const fl = await page.evaluate(async () => {
    const slot = document.querySelector('[data-slot=toast]');
    const el = slot.querySelector('.toast');
    await new Promise((r) => setTimeout(r, 400));                       // let the single enter animation finish
    let starts = 0, childSwaps = 0, sampling = true;
    slot.addEventListener('animationstart', () => starts++, true);
    new MutationObserver((ms) => ms.forEach((m) => { if (m.target === slot) childSwaps++; })).observe(slot, { childList: true });
    const tops = new Set(), heights = new Set(); let minOp = 1;
    const sample = () => { if (!sampling) return; const r = el.getBoundingClientRect(); tops.add(Math.round(r.top)); heights.add(Math.round(r.height)); minOp = Math.min(minOp, Number(getComputedStyle(el).opacity)); requestAnimationFrame(sample); };
    requestAnimationFrame(sample);
    const pct = el.querySelector('[data-bind=toast-pct]'), bar = el.querySelector('[data-bind=toast-bar]');
    for (let i = 1; i <= 200; i++) { window.__hostSend({ type: 'updateStatus', state: 'downloading', progress: i / 200, version: '0.3.0' }); if (i % 20 === 0) await new Promise((r) => setTimeout(r, 16)); }
    await new Promise((r) => setTimeout(r, 500));
    const mid = { same: slot.querySelector('.toast') === el, sameParts: el.querySelector('[data-bind=toast-pct]') === pct && el.querySelector('[data-bind=toast-bar]') === bar,
      starts, childSwaps, running: el.getAnimations().length, tops: tops.size, heights: heights.size, minOp, pct: pct.textContent, bar: bar.style.transform, tabular: getComputedStyle(pct).fontVariantNumeric };
    window.__hostSend({ type: 'updateStatus', state: 'installing', version: '0.3.0' });
    await new Promise((r) => setTimeout(r, 300));
    sampling = false;
    const cs = getComputedStyle(el), zs = Number(getComputedStyle(slot).zIndex), zModal = 50;
    return Object.assign(mid, { sameAfterInstall: slot.querySelector('.toast') === el, startsAfterInstall: starts, installTitle: el.querySelector('.toast-title-text').textContent,
      bg: cs.backgroundColor, bgImg: cs.backgroundImage, backdrop: cs.backdropFilter, z: zs > zModal });
  });
  check('toast: 200 snelle voortgangsberichten, zelfde element (niet opnieuw aangemaakt)', () => assert.deepStrictEqual([fl.same, fl.sameParts, fl.childSwaps], [true, true, 0], JSON.stringify(fl)));
  check('toast: enter-animatie start niet opnieuw, geen knipperen (opacity 1)', () => assert.deepStrictEqual([fl.starts, fl.running, fl.minOp], [0, 0, 1], JSON.stringify(fl)));
  check('toast: springt niet (vaste positie en hoogte)', () => assert.deepStrictEqual([fl.tops, fl.heights], [1, 1], JSON.stringify(fl)));
  check('toast: alleen balk (scaleX) + percentage bijgewerkt, tabular-nums', () => assert.deepStrictEqual([fl.pct, fl.bar, fl.tabular], ['100%', 'scaleX(1)', 'tabular-nums'], JSON.stringify(fl)));
  check('toast: Installeren in hetzelfde element, geen nieuwe animatie', () => assert.deepStrictEqual([fl.sameAfterInstall, fl.startsAfterInstall, fl.installTitle], [true, 0, 'Vanta wordt bijgewerkt…'], JSON.stringify(fl)));
  check('toast: ondoorzichtige achtergrond, geen backdrop-filter, boven alles', () => assert.ok(/^rgb\(/.test(fl.bg) && fl.backdrop === 'none' && fl.z && /linear-gradient\(rgb\(/.test(fl.bgImg), JSON.stringify(fl)));
  await page.mouse.move(0, 0);
  await shot(`vanta-${VER}-update-installeren.png`);

  // 6c3. "Later" = not offered again this session (a manual check still shows it)
  await load();
  const offer = () => page.evaluate(() => window.__hostSend({ type: 'update', manual: false, current: '0.2.1', release: { state: 'available', version: '0.3.0', notes: '- x', url: '', size: 1 } }));
  await offer(); await sleep(250);
  await page.click('[data-action=update-later]'); await sleep(150);
  await offer(); await sleep(250);
  const later = await page.evaluate(() => ({ toast: !!document.querySelector('.toast'), sent: window.__posted.filter((m) => m.type === 'updateLater').map((m) => m.version) }));
  check('Later: bericht met versie, zelfde versie niet opnieuw getoond', () => assert.deepStrictEqual(later, { toast: false, sent: ['0.3.0'] }));
  await page.evaluate(() => window.__hostSend({ type: 'update', manual: true, current: '0.2.1', release: { state: 'available', version: '0.3.0', notes: '- x', url: '', size: 1 } }));
  await sleep(250);
  const manualShown = await page.$('.toast');
  check('handmatige controle toont uitgestelde update opnieuw', () => assert.ok(manualShown));
  await page.click('[data-action=update-close]'); await sleep(150);
  const dis = await page.evaluate(() => window.__posted.filter((m) => m.type === 'updateDismiss').map((m) => m.version));
  check('sluitknop: updateDismiss met versie naar host', () => assert.deepStrictEqual(dis, ['0.3.0']));

  // 6c4. "Game aanvragen": list, Steam search (host-side), vote; logged out = sign-in prompt; not deployed = friendly message
  await load({ account: { configured: true, loggedIn: false } });
  await page.click('[data-action=open-requests]'); await sleep(250);
  const rq0 = await page.evaluate(() => ({ rows: [...document.querySelectorAll('.req-list .req-row')].map((r) => r.dataset.appid), login: !!document.querySelector('.req-login [data-action=account-login]'),
    focus: document.activeElement && document.activeElement.dataset.role, sent: window.__posted.filter((m) => m.type === 'getRequests').length }));
  check('aanvragen: lijst geladen, zoekveld focus, inlog-prompt als uitgelogd', () => assert.deepStrictEqual(rq0, { rows: ['264710', '105600'], login: true, focus: 'req-search', sent: 1 }));
  await page.click('.req-list [data-action=req-vote]'); await sleep(450);
  const rq1 = await page.evaluate(() => ({ votes: window.__posted.filter((m) => m.type === 'requestVote').length, login: window.__posted.filter((m) => m.type === 'accountLogin').length,
    refetch: window.__posted.filter((m) => m.type === 'getRequests').length, prompt: !!document.querySelector('.req-login') }));
  check('aanvragen: stemmen zonder login start Discord-login, daarna lijst opnieuw geladen', () => assert.deepStrictEqual(rq1, { votes: 0, login: 1, refetch: 2, prompt: false }));
  await page.type('[data-role=req-search]', 'subn'); await sleep(700);
  const rq2 = await page.evaluate(() => ({ search: window.__posted.filter((m) => m.type === 'steamSearch').map((m) => m.term), rows: [...document.querySelectorAll('.req-results .req-row')].map((r) => r.querySelector('.req-name').innerHTML),
    focus: document.activeElement && document.activeElement.dataset.role }));
  check('aanvragen: Steam-zoeken via host (debounced), HTML ontsnapt, focus blijft', () => assert.deepStrictEqual(rq2, { search: ['subn'], rows: ['Subnautica', 'Subnautica: Below Zero &lt;b&gt;x&lt;/b&gt;'], focus: 'req-search' }));
  await page.click('.req-results [data-appid="848450"] [data-action=req-vote]'); await sleep(300);
  const rq3 = await page.evaluate(() => ({ sent: window.__posted.filter((m) => m.type === 'requestVote').map((m) => [m.appid, m.name]), list: [...document.querySelectorAll('.req-list .req-row')].map((r) => r.dataset.appid + (r.hasAttribute('data-voted') ? '*' : '')) }));
  check('aanvragen: stem op zoekresultaat -> requestVote, lijst bijgewerkt', () => assert.deepStrictEqual(rq3, { sent: [[848450, 'Subnautica: Below Zero <b>x</b>']], list: ['264710', '105600', '848450*'] }));
  await page.click('.req-list [data-appid="848450"] [data-action=req-vote]'); await sleep(300);
  const rq4 = await page.evaluate(() => window.__posted.filter((m) => m.type === 'requestUnvote').map((m) => m.appid));
  check('aanvragen: nogmaals klikken trekt stem in', () => assert.deepStrictEqual(rq4, [848450]));
  await page.$eval('[data-role=req-search]', (e) => { e.value = ''; }); await page.type('[data-role=req-search]', '700'); await sleep(700);
  const rq5 = await page.evaluate(() => (document.querySelector('[data-bind=req-results] .req-note') || {}).textContent);
  check('aanvragen: geplakte tool/DLC zonder basisspel -> vriendelijke melding', () => assert.strictEqual(rq5, 'Dit is geen game maar bijvoorbeeld DLC, een soundtrack of een tool. Zoek op de naam van het basisspel.'));
  await page.keyboard.press('Escape'); await sleep(100);
  await load({ reqDown: true });
  await page.click('[data-action=open-requests]'); await sleep(250);
  const rqDown = await page.evaluate(() => (document.querySelector('.req-list') || {}).textContent.trim());
  check('aanvragen: SQL nog niet toegepast -> nette melding', () => assert.strictEqual(rqDown, 'Game aanvragen is nog niet beschikbaar. Probeer het later opnieuw.'));
  await page.keyboard.press('Escape'); await sleep(100);

  // 6c5. Layout of the dialogs at the app's window sizes with long Steam names (v0.3.2 bug: the request dialog overflowed
  // sideways with a white native scrollbar and cut names off). This browser shows real scrollbars (no --hide-scrollbars).
  // Per dialog: nothing wider than its box (scrollWidth <= clientWidth for every element), nothing sticking out, no ellipsis,
  // and scrollbars are the thin dark ::-webkit-scrollbar ones (10px) instead of the native 15px ones.
  {
    // Headless Chrome never paints classic scrollbars (they take 0px), so the 10px width is only measured with UI_HEADFUL=1
    // (e.g. `xvfb-run -a env UI_HEADFUL=1 node tests/ui/ui.e2e.js`); the stylesheet rules are checked in both modes.
    const headful = process.env.UI_HEADFUL === '1';
    const vis = await puppeteer.launch({ executablePath: process.env.CHROME || '/usr/bin/google-chrome', headless: !headful, args: ['--no-sandbox', '--disable-gpu', '--lang=nl-NL', '--window-size=1960,1200'] });
    const vp = await vis.newPage();
    await intercept(vp);
    vp.on('pageerror', (e) => errors.push(e.message));
    const vload = async (opts, w, h) => { hostJs = hostScript(opts); await vp.setViewport({ width: w, height: h, deviceScaleFactor: 1 }); await vp.goto(base, { waitUntil: 'networkidle0' }); await sleep(500); };
    const layout = (sel) => vp.evaluate((sel) => {
      const m = document.querySelector(sel); if (!m) return null;
      const d = (e) => e.tagName.toLowerCase() + (e.className && typeof e.className === 'string' ? '.' + e.className.trim().split(/\s+/).join('.') : '');
      const mr = m.getBoundingClientRect(), over = [], outside = [], ellipsis = [];
      for (const e of [m, ...m.querySelectorAll('*')]) {
        if (e.closest('svg')) continue;
        const cs = getComputedStyle(e);
        if (cs.display === 'none' || cs.visibility === 'hidden') continue;
        if (e.clientWidth > 0 && e.scrollWidth > e.clientWidth) over.push(`${d(e)} ${e.scrollWidth}>${e.clientWidth}`);
        const r = e.getBoundingClientRect();
        if (r.width && (r.right > mr.right + 0.5 || r.left < mr.left - 0.5)) outside.push(d(e));
        if (cs.textOverflow === 'ellipsis') ellipsis.push(d(e));
      }
      const b = m.querySelector('.modal-body'), bs = getComputedStyle(b);
      const lines = (e) => Math.round(e.getBoundingClientRect().height / parseFloat(getComputedStyle(e).lineHeight));
      return { w: Math.round(mr.width), vw: innerWidth, over, outside, ellipsis, page: document.documentElement.scrollWidth <= innerWidth && document.body.scrollWidth <= innerWidth,
        vscroll: b.scrollHeight > b.clientHeight, sb: b.offsetWidth - b.clientWidth, std: [b, document.querySelector('.scroll'), document.querySelector('.library')].map((e) => getComputedStyle(e).scrollbarWidth + '/' + getComputedStyle(e).scrollbarColor),
        names: [...m.querySelectorAll('.req-name')].map((e) => ({ t: e.textContent, lines: lines(e), full: e.scrollWidth <= e.clientWidth && e.getBoundingClientRect().right <= b.getBoundingClientRect().right })),
        intro: (() => { const e = m.querySelector('.req-intro'); return e ? { lines: lines(e), full: e.scrollWidth <= e.clientWidth } : null; })(), bodyOverflowX: bs.overflowX };
    }, sel);
    const clean = (L) => L && L.over.length === 0 && L.outside.length === 0 && L.ellipsis.length === 0 && (L.page || L.vw < 1024);   // .app has min-width 1024
    const sbOk = (L) => (!headful || !L.vscroll || L.sb === 10) && L.std.every((x) => x === 'auto/auto');
    const SIZES = [[1320, 860], [1040, 680], [1280, 720], [1366, 768], [1600, 900], [1920, 1080], [880, 600]];   // default, minimum, common screens; 880x600 = min window at 125-150% DPI
    const valley = 'VALLEY OF MO’ARA STARTER PACK - AVATAR: FRONTIERS OF PANDORA™';
    let anyScroll = false;
    for (const [w, h] of SIZES) {
      await vload({ account: { configured: true, loggedIn: false }, longReq: true }, w, h);
      await vp.click('[data-action=open-requests]'); await sleep(250);
      await vp.type('[data-role=req-search]', 'avatar'); await sleep(800);
      await vp.mouse.move(0, 0);
      const L = await layout('.modal-requests');
      const tag = `${w}x${h}`;
      check(`aanvragen ${tag}: geen horizontale overflow (scrollWidth <= clientWidth), niets buiten de dialoog, geen ellipsis`, () => assert.ok(clean(L), JSON.stringify(L && { over: L.over, outside: L.outside, ellipsis: L.ellipsis, page: L.page })));
      check(`aanvragen ${tag}: breedte = min(860px, 92vw)`, () => assert.ok(Math.abs(L.w - Math.min(860, Math.round(0.92 * w))) <= 1, `${L.w}`));
      check(`aanvragen ${tag}: alle ${L.names.length} lange namen volledig zichtbaar, ondertitel volledig`, () => assert.ok(L.names.length === 11 && L.names.every((n) => n.full) && L.names.some((n) => n.t === valley) && L.intro.full, JSON.stringify(L.names)));
      if (w >= 1040) check(`aanvragen ${tag}: "${valley}" op één regel`, () => assert.ok(L.names.filter((n) => n.t === valley).every((n) => n.lines === 1), JSON.stringify(L.names.filter((n) => n.t === valley))));
      check(`aanvragen ${tag}: donkere dunne scrollbalk (::-webkit-scrollbar${headful ? ' 10px gemeten' : ''}, geen scrollbar-width/color die hem uitschakelt)`, () => assert.ok(sbOk(L), JSON.stringify({ vscroll: L.vscroll, sb: L.sb, std: L.std })));
      if (w === 1320) {
        const rules = await vp.evaluate(() => { const r = {}; for (const sh of document.styleSheets) for (const x of sh.cssRules) if (x.selectorText && /::-webkit-(scrollbar|resizer)/.test(x.selectorText)) x.selectorText.split(/,\s*/).forEach((k) => { r[k] = x.style; }); const g = (k, p) => r[k] && r[k].getPropertyValue(p);
          return { width: g('::-webkit-scrollbar', 'width'), track: g('::-webkit-scrollbar-track', 'background-color') || g('::-webkit-scrollbar-track', 'background'), radius: g('::-webkit-scrollbar-thumb', 'border-radius'), thumb: g('::-webkit-scrollbar-thumb', 'background-color'),
            hover: g('::-webkit-scrollbar-thumb:hover', 'background-color'), buttons: g('::-webkit-scrollbar-button', 'display'), corner: g('::-webkit-scrollbar-corner', 'background-color') || g('::-webkit-scrollbar-corner', 'background') }; });
        check('scrollbalken: dun, afgeronde duim in --line-3, lichter bij hover, transparant spoor, geen pijltjes', () => assert.deepStrictEqual(rules, { width: '10px', track: 'transparent', radius: '999px', thumb: 'var(--line-3)', hover: 'rgba(255, 255, 255, 0.28)', buttons: 'none', corner: 'transparent' }));
      }
      anyScroll = anyScroll || L.vscroll;
      if (w === 1320) {
        await sleep(300);
        const file = path.join(OUT, `vanta-${VER}-aanvragen-lange-namen.png`);
        await vp.screenshot({ path: file }); results.push(['SHOT', file]);
        if (process.env.SHOT_REQ) { fs.mkdirSync(path.dirname(process.env.SHOT_REQ), { recursive: true }); fs.copyFileSync(file, process.env.SHOT_REQ); results.push(['SHOT', process.env.SHOT_REQ]); }
      }
      if (w === 1040) { const file = path.join(OUT, `vanta-${VER}-aanvragen-1040.png`); await sleep(300); await vp.screenshot({ path: file }); results.push(['SHOT', file]); }
      if (w === 880) { const file = path.join(OUT, `vanta-${VER}-aanvragen-smal.png`); await sleep(300); await vp.screenshot({ path: file }); results.push(['SHOT', file]); }

      // "Wat is er nieuw" with a long URL, an unbroken word and a long code line
      await vp.keyboard.press('Escape'); await sleep(100);
      await vp.evaluate(() => window.__hostSend({ type: 'update', manual: true, current: '0.3.2', release: { state: 'available', version: '0.3.3', size: 71000000, url: 'https://github.com/Rick007110/Vanta/releases/tag/v0.3.3',
        notes: '## Nieuw\n- Zie https://github.com/Rick007110/Vanta/releases/tag/v0.3.3/een/heel/lange/link/zonder/spaties/die/niet/mag/uitsteken?query=aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa\n- ' + 'X'.repeat(160) + '\n\n```\n' + 'const lang = ' + '"y"'.repeat(80) + ';\n```\n' + Array.from({ length: 40 }, (_, i) => '- regel ' + i).join('\n') } }));
      await sleep(200);
      await vp.click('[data-action=whats-new]'); await sleep(300);
      const N = await layout('.modal-notes');
      check(`wat is er nieuw ${tag}: geen horizontale overflow, donkere scrollbalk`, () => assert.ok(clean(N) && N.vscroll && sbOk(N), JSON.stringify(N && { over: N.over, outside: N.outside, ellipsis: N.ellipsis, sb: N.sb, vscroll: N.vscroll })));
      if (w === 1040) { const file = path.join(OUT, `vanta-${VER}-wat-is-er-nieuw-1040.png`); await sleep(300); await vp.screenshot({ path: file }); results.push(['SHOT', file]); }

      // report dialog with a long cheat name
      await vload({ account: { configured: true, loggedIn: true }, longCheat: true }, w, h);
      await vp.click('[data-cheat=inf_health] .row-name', { button: 'right' }); await sleep(150);
      await vp.click('[data-action=report-open]'); await sleep(250);
      await vp.click('.rep-opt[data-status=broken]'); await sleep(100);
      const R = await layout('.modal-report');
      check(`melden ${tag}: lange cheatnaam past, geen horizontale overflow`, () => assert.ok(clean(R), JSON.stringify(R && { over: R.over, outside: R.outside, ellipsis: R.ellipsis })));
      if (w === 1040) { const file = path.join(OUT, `vanta-${VER}-melden-1040.png`); await sleep(300); await vp.screenshot({ path: file }); results.push(['SHOT', file]); }
    }
    check('aanvragen: verticale scrollbalk minstens één keer getest', () => assert.ok(anyScroll));
    await vis.close();
  }
  await load();
  await page.click('[data-action=open-settings]');
  await sleep(200);
  await page.click('[data-action=check-update]');
  await sleep(250);
  const upd = await page.evaluate(() => ({ sent: window.__posted.filter((m) => m.type === 'checkUpdate').length, line: document.querySelector('[data-bind=update-status]').textContent }));
  check('instellingen: Controleer op updates', () => assert.deepStrictEqual(upd, { sent: 1, line: 'Je hebt de nieuwste versie.' }));
  await page.click('[data-action=modal-close]');

  // 6e. test status: broken cheat, right-click menu, local override, export
  await load();
  try { await browser.defaultBrowserContext().overridePermissions(base.replace(/\/index\.html$/, ''), ['clipboard-read', 'clipboard-write', 'clipboard-sanitized-write']); } catch (_) { /* older chrome */ }
  const br = await page.$eval('[data-cheat=inf_jump]', (e) => ({ broken: e.hasAttribute('data-broken'), sw: e.querySelector('[role=switch]').disabled, title: e.title, tryVis: getComputedStyle(e.querySelector('[data-action=try-anyway]')).display !== 'none' }));
  check('broken cheat: grijs, schakelaar uit, "Toch proberen" zichtbaar', () => assert.deepStrictEqual([br.broken, br.sw, br.tryVis, /werkt niet in deze versie/i.test(br.title)], [true, true, true, true], JSON.stringify(br)));
  await page.click('[data-cheat=inf_jump] .row-name');
  await sleep(150);
  const tj = await page.evaluate(() => window.__posted.filter((m) => m.type === 'toggle' && m.id === 'inf_jump').length);
  check('klik op broken cheat stuurt geen toggle', () => assert.strictEqual(tj, 0));
  const tryOthers = await page.$$eval('[data-cheat]', (els) => els.filter((e) => e.dataset.cheat !== 'inf_jump' && getComputedStyle(e.querySelector('[data-action=try-anyway]') || e).display === 'inline-flex').length);
  check('"Toch proberen" alleen bij broken cheats', () => assert.strictEqual(tryOthers, 0));
  await page.mouse.move(0, 0);
  await shot(`vanta-${VER}-werkt-niet.png`);
  await page.click('[data-cheat=inf_jump] [data-action=try-anyway]');
  await sleep(200);
  const forced = await page.evaluate(() => ({ msg: window.__posted.filter((m) => m.type === 'toggle' && m.id === 'inf_jump').pop(), st: document.querySelector('[data-cheat=inf_jump]').dataset.state, broken: document.querySelector('[data-cheat=inf_jump]').hasAttribute('data-broken') }));
  check('"Toch proberen": toggle met force:true, cheat aan', () => assert.deepStrictEqual([forced.msg && forced.msg.enabled, forced.msg && forced.msg.force, forced.st, forced.broken], [true, true, 'on', false], JSON.stringify(forced)));
  await page.click('[data-cheat=inf_jump] [role=switch]');
  await sleep(200);
  const off = await page.evaluate(() => ({ msg: window.__posted.filter((m) => m.type === 'toggle' && m.id === 'inf_jump').pop(), broken: document.querySelector('[data-cheat=inf_jump]').hasAttribute('data-broken') }));
  check('broken cheat weer uitzetten kan, daarna weer vergrendeld', () => assert.deepStrictEqual([off.msg.enabled, off.broken], [false, true]));

  await page.click('[data-cheat=inf_health] .row-name', { button: 'right' });
  await sleep(200);
  const menu = await page.evaluate(() => { const m = document.querySelector('.ctx-menu'); return m && { items: [...m.querySelectorAll('.ctx-item .ctx-label')].map((e) => e.textContent), cur: (m.querySelector('.ctx-item.is-current .ctx-label') || {}).textContent, focus: document.activeElement && document.activeElement.classList.contains('ctx-item'), sub: m.querySelector('.ctx-sub').textContent }; });
  check('rechtsklikmenu: Werkt / Werkt niet / Niet getest / Standaard', () => assert.deepStrictEqual(menu && menu.items, ['Werkt', 'Werkt niet', 'Niet getest', 'Standaard (uit game.json)'], JSON.stringify(menu)));
  check('rechtsklikmenu: huidige = Standaard, focus in menu, game.json-waarde getoond', () => assert.deepStrictEqual([menu.cur, menu.focus, menu.sub], ['Standaard (uit game.json)', true, 'Teststatus · game.json: Ongetest']));
  await shot(`vanta-${VER}-rechtsklikmenu.png`);
  await page.keyboard.press('Escape');
  await sleep(100);
  const closed = await page.$('.ctx-menu');
  check('menu gesloten na Escape', () => assert.strictEqual(closed, null));

  const pick = async (id, status) => { await page.click(`[data-cheat=${id}] .row-name`, { button: 'right' }); await sleep(150); await page.click(`.ctx-item[data-status=${status}]`); await sleep(250); };
  await pick('inf_health', 'broken');
  const s1 = await page.evaluate(() => ({ msg: window.__posted.filter((m) => m.type === 'setStatus').pop(), broken: document.querySelector('[data-cheat=inf_health]').hasAttribute('data-broken'), local: document.querySelector('[data-cheat=inf_health]').hasAttribute('data-local'), menu: !!document.querySelector('.ctx-menu') }));
  check('Werkt niet: setStatus naar host, rij grijs, menu dicht', () => assert.deepStrictEqual([s1.msg.id, s1.msg.status, s1.msg.gameId, s1.broken, s1.local, s1.menu], ['inf_health', 'broken', 'the-last-caretaker', true, true, false]));
  await pick('inf_health', 'works');
  const s2 = await page.evaluate(() => ({ msg: window.__posted.filter((m) => m.type === 'setStatus').pop(), broken: document.querySelector('[data-cheat=inf_health]').hasAttribute('data-broken'), check: !!document.querySelector('[data-cheat=inf_health] .conf-ok.conf-local') }));
  check('Werkt: telt als bevestigd, groen vinkje', () => assert.deepStrictEqual([s2.msg.status, s2.broken, s2.check], ['works', false, true]));
  await pick('inf_jump', 'works');
  const s3 = await page.$eval('[data-cheat=inf_jump]', (e) => ({ broken: e.hasAttribute('data-broken'), sw: e.querySelector('[role=switch]').disabled }));
  check('Werkt overschrijft game.json broken: schakelaar weer bruikbaar', () => assert.deepStrictEqual(s3, { broken: false, sw: false }));
  await pick('no_weight', 'untested');
  await page.click('[data-action=tab][data-value=notes]');
  await sleep(150);
  const notes = await page.evaluate(() => Object.fromEntries([...document.querySelectorAll('[data-cheat-note]')].map((e) => [e.dataset.cheatNote, { badge: e.querySelector('.badge').textContent, local: e.querySelector('.badge').classList.contains('is-local'), warn: (e.querySelector('.conf-warn') || {}).textContent || '' }])));
  check('notities: lokale status zichtbaar (Bevestigd/Ongetest, gemarkeerd)', () => assert.deepStrictEqual([notes.inf_health.badge, notes.inf_health.local, notes.no_weight.badge, notes.no_weight.local, notes.inf_jump.badge], ['Bevestigd', true, 'Ongetest', true, 'Bevestigd']));
  await page.$eval('[data-cheat-note=inf_jump]', (e) => e.scrollIntoView({ block: 'center' })); await sleep(200);   // a scroll closes the menu
  await page.click('[data-cheat-note=inf_jump] .conf-name', { button: 'right' }); await sleep(150); await page.click('.ctx-item[data-status=default]'); await sleep(250);
  const nj = await page.$eval('[data-cheat-note=inf_jump]', (e) => ({ badge: e.querySelector('.badge').textContent, warn: (e.querySelector('.conf-warn') || {}).textContent }));
  check('notities: broken cheat toont "Werkt niet in deze versie."', () => assert.deepStrictEqual(nj, { badge: 'Werkt niet', warn: 'Werkt niet in deze versie.' }));
  const lastDefault = await page.evaluate(() => window.__posted.filter((m) => m.type === 'setStatus').pop());
  check('Standaard (uit game.json): status null naar host', () => assert.deepStrictEqual([lastDefault.id, lastDefault.status], ['inf_jump', null]));
  await shot(`vanta-${VER}-notities-teststatus.png`);
  await page.click('[data-action=tab][data-value=cheats]');

  await page.click('[data-action=open-settings]');
  await sleep(200);
  await page.click('[data-action=export-status]');
  await sleep(400);
  const ex = await page.evaluate(async () => { const l = document.querySelector('[data-bind=export-status]'); let clip = null; try { clip = await navigator.clipboard.readText(); } catch (_) {} return { sent: window.__posted.filter((m) => m.type === 'exportStatus').length, line: l.textContent, hidden: l.hidden, copied: l.dataset.copied, clip }; });
  check('Exporteer teststatus: bericht naar host + pad getoond', () => assert.ok(ex.sent === 1 && !ex.hidden && /teststatus-export-20260927-150000\.json/.test(ex.line), JSON.stringify(ex)));
  check('Exporteer teststatus: JSON op het klembord', () => assert.ok(ex.copied === 'true' && (ex.clip == null || (/"inf_health"/.test(ex.clip) && /"works"/.test(ex.clip))), JSON.stringify(ex)));
  await shot(`vanta-${VER}-exporteer-teststatus.png`);
  await page.click('[data-action=modal-close]');


  // 6b. community reports with a Discord account (fake host)
  await load({ account: { configured: true, loggedIn: false, privacyUrl: 'https://rick007110.github.io/vanta-site/privacy/' } });
  const pickA = async (id, status) => { await page.click(`[data-cheat=${id}] .row-name`, { button: 'right' }); await sleep(150); await page.click(`.ctx-item[data-status=${status}]`); await sleep(300); };
  await page.click('[data-cheat=inf_health] .row-name', { button: 'right' }); await sleep(150);
  const menuA = await page.$$eval('.ctx-menu .ctx-item .ctx-label', (e) => e.map((x) => x.textContent));
  check('met backend: rechtsklikmenu heeft "Melden…"', () => assert.deepStrictEqual(menuA, ['Werkt', 'Werkt niet', 'Niet getest', 'Standaard (uit game.json)', 'Melden…']));
  await page.keyboard.press('Escape'); await sleep(100);
  await pickA('inf_health', 'broken');
  const lo = await page.evaluate(() => ({ modal: !!document.querySelector('.modal-report'), rep: window.__posted.filter((m) => m.type === 'report').length }));
  check('niet ingelogd: Werkt niet blijft lokaal (geen dialoog, geen melding)', () => assert.deepStrictEqual(lo, { modal: false, rep: 0 }));
  await page.click('[data-action=open-settings]'); await sleep(250);
  const acc0 = await page.evaluate(() => ({ state: document.querySelector('[data-bind=acct-state]').dataset.state, btn: (document.querySelector('[data-action=account-login]') || {}).textContent, usage: !!document.querySelector('[data-set=shareUsage]') }));
  check('instellingen: "Inloggen met Discord" + anoniem-gebruik-schakelaar', () => assert.deepStrictEqual([acc0.state, (acc0.btn || '').trim(), acc0.usage], ['out', 'Inloggen met Discord', true]));
  const priv = await page.$eval('.acct-privacy', (e) => [e.dataset.action, e.dataset.url === 'https://rick007110.github.io/vanta-site/privacy/', e.textContent.trim()]).catch(() => null);
  check('instellingen: privacy-link naar de privacypagina op de website', () => assert.deepStrictEqual(priv, ['open-url', true, 'Privacy']));
  await shot(`vanta-${VER}-account-uitgelogd.png`);
  await page.click('[data-action=account-login]'); await sleep(80);
  const busy = await page.$eval('[data-bind=acct-state]', (e) => e.dataset.state);
  await sleep(500);
  const acc1 = await page.evaluate(() => ({ sent: window.__posted.filter((m) => m.type === 'accountLogin').length, state: document.querySelector('[data-bind=acct-state]').dataset.state, name: document.querySelector('[data-bind=acct-name]').textContent, img: (document.querySelector('.acct-avatar img') || {}).src }));
  check('inloggen: accountLogin naar host, wachten, daarna naam + avatar', () => assert.deepStrictEqual([busy, acc1.sent, acc1.state, acc1.name, acc1.img], ['busy', 1, 'in', 'Ingelogd als tester', 'https://cdn.discordapp.com/embed/avatars/1.png']));
  check('avatar van cdn.discordapp.com geladen (CSP staat het toe)', () => assert.ok(avatarHits.length >= 1));
  await page.click('[data-set=shareUsage]');
  await page.click('[data-action=modal-save]'); await sleep(200);
  const su = await page.evaluate(() => window.__posted.filter((m) => m.type === 'saveSettings').pop());
  check('anoniem gebruik delen: shareUsage in saveSettings', () => assert.strictEqual(su && su.settings.shareUsage, true));
  await pickA('inf_health', 'broken');
  const dlg = await page.evaluate(() => ({ modal: !!document.querySelector('.modal-report'), title: (document.querySelector('#rep-title') || {}).textContent, checked: (document.querySelector('.rep-opt[aria-checked=true]') || { dataset: {} }).dataset.status, focus: document.activeElement && document.activeElement.dataset.role }));
  check('ingelogd: Werkt niet opent meld-dialoog, focus in opmerking', () => assert.deepStrictEqual([dlg.modal, dlg.title, dlg.checked, dlg.focus], [true, 'Melden: Infinite Health', 'broken', 'report-note']));
  await page.type('[data-role=report-note]', 'Health zakt toch na val <b>schade</b>');
  const cnt = await text('[data-bind=rep-count]');
  check('opmerking: tekenteller', () => assert.strictEqual(cnt, '37'));
  await shot(`vanta-${VER}-melden.png`);
  await page.click('[data-action=report-send]'); await sleep(400);
  const sent = await page.evaluate(() => ({ msg: window.__posted.filter((m) => m.type === 'report').pop(), modal: !!document.querySelector('.modal-report'), log: document.querySelector('[data-bind=log-text]').textContent }));
  check('Versturen: report {broken, note} naar host, dialoog dicht, bevestiging', () => assert.deepStrictEqual([sent.msg.id, sent.msg.status, sent.msg.note, sent.modal, sent.log], ['inf_health', 'broken', 'Health zakt toch na val <b>schade</b>', false, 'Melding verstuurd: Infinite Health']));
  await pickA('inf_health', 'works');
  const w = await page.evaluate(() => ({ msg: window.__posted.filter((m) => m.type === 'report').pop(), modal: !!document.querySelector('.modal-report') }));
  check('Werkt: melding direct verstuurd, zonder dialoog', () => assert.deepStrictEqual([w.msg.status, w.msg.note, w.modal], ['works', null, false]));
  await pickA('no_weight', 'untested');
  const wd = await page.evaluate(() => window.__posted.filter((m) => m.type === 'withdraw').pop());
  check('Niet getest: eerdere melding ingetrokken (withdraw)', () => assert.deepStrictEqual([wd && wd.id, wd && wd.gameId], ['no_weight', 'the-last-caretaker']));
  await page.click('[data-action=tab][data-value=notes]'); await sleep(250);
  const comm = await page.evaluate(() => Object.fromEntries([...document.querySelectorAll('[data-cheat-note]')].map((e) => [e.dataset.cheatNote, (e.querySelector('[data-bind=community]') || {}).textContent || ''])));
  check('notities: community-regel "12 gebruikers melden: werkt niet" (bijgewerkt na eigen melding)', () => assert.strictEqual(comm.inf_health, '12 gebruikers melden: werkt niet · 4× werkt'));
  check('notities: gerepareerd-regel met versie', () => assert.strictEqual(comm.no_weight, 'Gerepareerd in Vanta 0.2.3'));
  const gc = await page.evaluate(() => window.__posted.filter((m) => m.type === 'getCommunity').pop());
  check('getCommunity naar host bij openen notities', () => assert.strictEqual(gc && gc.gameId, 'the-last-caretaker'));
  await shot(`vanta-${VER}-community.png`);
  await page.click('[data-action=tab][data-value=cheats]'); await sleep(200);
  await page.click('[data-cheat=inf_ammo] .row-name', { button: 'right' }); await sleep(150);
  await page.click('[data-action=report-open]'); await sleep(200);
  await page.click('.rep-opt[data-status=broken]');
  await page.type('[data-role=report-note]', 'offline test');
  await page.keyboard.down('Control'); await page.keyboard.press('Enter'); await page.keyboard.up('Control'); await sleep(400);
  const q = await page.evaluate(() => ({ log: document.querySelector('[data-bind=log-text]').textContent, level: document.querySelector('[data-bind=log-text]').dataset.level, modal: !!document.querySelector('.modal-report'), local: (window.__posted.filter((m) => m.type === 'setStatus' && m.id === 'inf_ammo').pop() || {}).status }));
  check('Melden… via menu + Ctrl+Enter; offline: "wordt later verstuurd"', () => assert.deepStrictEqual(q, { log: 'Geen verbinding: Infinite Ammo wordt later verstuurd', level: 'warn', modal: false, local: 'broken' }));
  await page.click('[data-action=open-settings]'); await sleep(250);
  await page.click('[data-action=account-delete]'); await sleep(100);
  const conf = await page.$eval('.acct-confirm', (e) => e.textContent.replace(/\s+/g, ' ').trim());
  check('Account verwijderen vraagt bevestiging', () => assert.ok(/definitief verwijderd/.test(conf), conf));
  await shot(`vanta-${VER}-account-verwijderen.png`);
  await page.click('[data-action=account-delete-confirm]'); await sleep(500);
  const del = await page.evaluate(() => ({ sent: window.__posted.filter((m) => m.type === 'accountDelete').length, state: document.querySelector('[data-bind=acct-state]').dataset.state, log: document.querySelector('[data-bind=log-text]').textContent }));
  check('na bevestigen: accountDelete, weer uitgelogd', () => assert.deepStrictEqual([del.sent, del.state, del.log], [1, 'out', 'Account en meldingen verwijderd']));
  await page.click('[data-action=modal-close]');

  // 7. catalog scale: 1200 games, search + category + blocked game
  await load({ many: 1200, blocked: true });
  const libCount = await page.$$eval('.lib-item', (e) => e.length);
  check('1200+ games: bibliotheek rendert (max 250 per groep)', () => assert.ok(libCount <= 252 && libCount > 100, String(libCount)));
  await page.type('[data-role=search]', 'testgame 011');
  await sleep(150);
  const found = await page.$$eval('.lib-item', (e) => e.length);
  check('zoeken filtert (10 treffers voor "testgame 011")', () => assert.strictEqual(found, 10));
  await page.$eval('[data-role=search]', (e) => { e.value = ''; e.dispatchEvent(new Event('input', { bubbles: true })); });
  await page.select('[data-role=category]', 'racing');
  await sleep(150);
  const racing = await page.$$eval('.lib-item', (e) => e.length);
  check('categoriefilter (racing = 200)', () => assert.strictEqual(racing, 200));
  await page.select('[data-role=category]', '');
  await page.type('[data-role=search]', 'online');
  await sleep(350);
  // focused search = centred palette over a dimmed backdrop; the matches are listed under the field
  const sp0 = await page.evaluate(() => { const b = document.querySelector('[data-search]').getBoundingClientRect(), p = document.querySelector('.spot-pop');
    return { floating: document.querySelector('[data-search]').classList.contains('is-floating'), dim: !document.querySelector('.spot-dim').hidden, pop: !p.hidden,
      centred: Math.abs(b.left + b.width / 2 - innerWidth / 2) < 2, picks: [...p.querySelectorAll('.spot-item')].map((e) => e.dataset.pick) }; });
  check('zoekpalet: veld gecentreerd, achtergrond gedimd, treffers in lijst', () => assert.deepStrictEqual(sp0, { floating: true, dim: true, pop: true, centred: true, picks: ['online-shooter'] }));
  await page.click('.spot-item[data-pick=online-shooter]');
  await sleep(400);
  const sp1 = await page.evaluate(() => ({ floating: document.querySelector('[data-search]').classList.contains('is-floating'), dim: document.querySelector('.spot-dim').hidden, focus: document.activeElement.dataset.role || null }));
  check('zoekpalet: klik op treffer sluit palet', () => assert.deepStrictEqual(sp1, { floating: false, dim: true, focus: null }));
  const bl = { label: await text('[data-bind=status-label]'), disabled: await page.$eval('[data-action=primary]', (b) => b.disabled) };
  check('anti-cheat game: Niet ondersteund, knop uit', () => assert.deepStrictEqual(bl, { label: 'Niet ondersteund', disabled: true }));
  await shot(`vanta-${VER}-geblokkeerd.png`);
  await page.$eval('[data-role=search]', (e) => { e.value = 'testgame 00'; e.dispatchEvent(new Event('input', { bubbles: true })); });
  await page.click('[data-game=game-3]');
  await sleep(300);
  await shot(`vanta-${VER}-catalogus-1200.png`);
  // Ctrl+K opens the palette, arrows move, Enter opens the highlighted game, Esc closes and clears
  await page.keyboard.down('Control'); await page.keyboard.press('k'); await page.keyboard.up('Control');
  await sleep(350);
  await page.keyboard.press('ArrowDown'); await page.keyboard.press('ArrowDown'); await page.keyboard.press('ArrowUp');
  const sp2 = await page.evaluate(() => ({ open: document.querySelector('[data-search]').classList.contains('is-floating'), n: document.querySelectorAll('.spot-item').length,
    active: document.querySelector('.spot-item[aria-selected=true]').dataset.pick }));
  check('zoekpalet: Ctrl+K opent, pijltjes navigeren', () => assert.deepStrictEqual(sp2, { open: true, n: 60, active: 'game-1' }));
  await page.keyboard.press('Enter'); await sleep(400);
  const sp3 = await page.evaluate(() => ({ open: document.querySelector('[data-search]').classList.contains('is-floating'), sel: document.querySelector('.lib-item.is-selected').dataset.game }));
  check('zoekpalet: Enter opent game', () => assert.deepStrictEqual(sp3, { open: false, sel: 'game-1' }));
  await page.focus('[data-role=search]'); await sleep(350);
  await page.mouse.click(120, 600); await sleep(350);
  const sp4 = await page.evaluate(() => ({ open: document.querySelector('[data-search]').classList.contains('is-floating'), dim: document.querySelector('.spot-dim').hidden, q: document.querySelector('[data-role=search]').value }));
  check('zoekpalet: klik op achtergrond sluit, zoekterm blijft', () => assert.deepStrictEqual(sp4, { open: false, dim: true, q: 'testgame 00' }));
  await page.focus('[data-role=search]'); await sleep(350);
  await page.keyboard.press('Escape'); await sleep(350);
  const sp5 = await page.evaluate(() => ({ open: document.querySelector('[data-search]').classList.contains('is-floating'), q: document.querySelector('[data-role=search]').value, ghost: !!document.querySelector('.search-ghost') }));
  check('zoekpalet: Esc sluit en wist', () => assert.deepStrictEqual(sp5, { open: false, q: '', ghost: false }));
  const t0 = Date.now();
  await load({ many: 1200 });
  check('laadtijd UI met 1200 games < 3 s', () => assert.ok(Date.now() - t0 < 3000, String(Date.now() - t0)));

  const csp = await page.evaluate(() => window.__csp);
  check('geen CSP-schendingen', () => assert.deepStrictEqual(csp, []));
  check('geen JS-fouten', () => assert.deepStrictEqual(errors, []));

  await browser.close(); server.close();
  results.forEach(([s, n]) => console.log(`${s.padEnd(4)} ${n}`));
  const fail = results.filter((r) => r[0] === 'FAIL').length;
  console.log(fail ? `UI: ${fail} FAILED` : `UI: all ${results.filter((r) => r[0] === 'OK').length} checks passed`);
  process.exit(fail ? 1 : 0);
})().catch((e) => { console.error(e); process.exit(2); });
