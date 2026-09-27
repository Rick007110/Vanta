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
const CSP = "default-src 'self'; img-src 'self' https://vanta.example data:; style-src 'self' 'unsafe-inline'; script-src 'self' 'unsafe-inline'; font-src 'self'; connect-src 'none'";
const MIME = { '.html': 'text/html', '.js': 'text/javascript', '.css': 'text/css', '.ttf': 'font/ttf', '.png': 'image/png', '.jpg': 'image/jpeg' };

global.window = {};
eval(fs.readFileSync(path.join(UI, 'shared', 'devdata.js'), 'utf8'));
const DEV = global.window.VantaDev;

function hostScript({ many = 0, status = 'attached', blocked = false } = {}) {
  const lib = JSON.parse(JSON.stringify(DEV.library));
  for (let i = 0; i < many; i++) lib.games.push({ id: `game-${i}`, name: `Testgame ${String(i + 1).padStart(4, '0')}`, short: 'TG', badge: 'v1.' + (i % 9), version: '', cheatCount: 3 + (i % 20),
    steamAppId: null, categories: [['survival', 'rpg', 'shooter', 'strategy', 'racing', 'sim'][i % 6]], antiCheat: i % 97 === 0, onlineOnly: false, group: 'all', art: null, process: `Game${i}.exe`, cheats: [], lazy: true });
  if (blocked) lib.games.push({ id: 'online-shooter', name: 'Online Shooter X', short: 'OS', badge: '', version: '', cheatCount: 0, steamAppId: null, categories: ['shooter'], antiCheat: true, onlineOnly: true, group: 'all', art: null, process: 'shooter.exe', cheats: [], lazy: true });
  return `
window.__posted = []; window.__csp = [];
document.addEventListener('securitypolicyviolation', (e) => window.__csp.push(e.violatedDirective + ' ' + e.blockedURI));
window.vantaHost = { library: ${JSON.stringify(lib)} };
(function () {
  const games = ${JSON.stringify(DEV.games)};
  const listeners = [];
  const send = (m) => setTimeout(() => listeners.forEach((fn) => fn({ data: m })), 10);
  const st = { status: ${JSON.stringify(status)} };
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
        if (m.type === 'ready') send({ type: 'settings', settings: { language: 'nl', catalogDir: '', catalogUrl: '', attachDelaySec: 4, autoAttach: true }, catalog: { source: 'C:\\\\Games\\\\Vanta\\\\games', games: window.vantaHost.library.games.length }, dataDir: '%LOCALAPPDATA%\\\\Vanta', version: '0.1.0' });
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
      if (m.type === 'checkUpdate') { send({ type: 'updateStatus', state: { state: 'uptodate', version: '0.2.0' } }); return ack(); }
      if (m.type === 'updateNow') { send({ type: 'updateStatus', state: 'downloading', progress: 0.42, version: '0.3.0' }); return ack(); }
      if (m.type === 'getSettings') { send({ type: 'settings', settings: { language: 'nl', catalogDir: '', catalogUrl: '', autoAttach: true }, catalog: { source: 'games', games: window.vantaHost.library.games.length }, dataDir: '%LOCALAPPDATA%\\\\Vanta', version: '0.1.0' }); return ack(); }
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
  await page.setRequestInterception(true);
  const artHits = [];
  page.on('request', (r) => {
    const m = r.url().match(/^https:\/\/vanta\.example\/art\/(\d+)\/(cover|hero|logo)/);
    if (m) {
      artHits.push(m[2]);
      const f = path.join(__dirname, 'art', `${m[1]}_${{ cover: 'library_600x900.jpg', hero: 'library_hero.jpg', logo: 'logo.png' }[m[2]]}`);
      if (fs.existsSync(f)) return r.respond({ status: 200, contentType: f.endsWith('png') ? 'image/png' : 'image/jpeg', body: fs.readFileSync(f) });
      return r.respond({ status: 404, body: '' });
    }
    if (!r.url().startsWith('http://127.0.0.1')) return r.abort();
    r.continue();
  });
  const errors = [];
  page.on('pageerror', (e) => errors.push(e.message));
  page.on('console', (m) => { if (m.type() === 'error' && !/Failed to load resource/.test(m.text())) errors.push(m.text()); });

  const load = async (opts) => { hostJs = hostScript(opts); await page.goto(base, { waitUntil: 'networkidle0' }); await sleep(500); };
  const text = (sel) => page.$eval(sel, (e) => e.textContent.trim());
  const shot = async (name) => { await sleep(350); await page.screenshot({ path: path.join(OUT, name) }); results.push(['SHOT', path.join(OUT, name)]); };

  // 1. main, attached
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
  const toast = await page.evaluate(() => { const e = document.querySelector('.toast'); return e && { title: e.querySelector('.toast-title').textContent, body: e.querySelector('.toast-body').innerHTML, btns: [...e.querySelectorAll('button')].map((b) => b.textContent.trim()) }; });
  check('update-toast: titel, changelog, Nu updaten/Later', () => assert.ok(toast && toast.title === 'Update beschikbaar: v0.3.0' && /Snellere AOB-scan/.test(toast.body) && toast.btns.includes('Nu updaten') && toast.btns.includes('Later'), JSON.stringify(toast)));
  await page.mouse.move(0, 0);
  await shot(`vanta-${VER}-update.png`);
  await page.click('[data-action=update-now]');
  await sleep(250);
  const dl = await page.evaluate(() => ({ sent: window.__posted.filter((m) => m.type === 'updateNow').length, title: document.querySelector('.toast-title').textContent, bar: (document.querySelector('.toast-bar i') || { style: {} }).style.width }));
  check('Nu updaten: bericht naar host + voortgang', () => assert.deepStrictEqual(dl, { sent: 1, title: 'v0.3.0 downloaden…', bar: '42%' }));
  await load();
  await page.click('[data-action=open-settings]');
  await sleep(200);
  await page.click('[data-action=check-update]');
  await sleep(250);
  const upd = await page.evaluate(() => ({ sent: window.__posted.filter((m) => m.type === 'checkUpdate').length, line: document.querySelector('[data-bind=update-status]').textContent }));
  check('instellingen: Controleer op updates', () => assert.deepStrictEqual(upd, { sent: 1, line: 'Je hebt de nieuwste versie.' }));
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
  await sleep(100);
  await page.click('[data-game=online-shooter]');
  await sleep(300);
  const bl = { label: await text('[data-bind=status-label]'), disabled: await page.$eval('[data-action=primary]', (b) => b.disabled) };
  check('anti-cheat game: Niet ondersteund, knop uit', () => assert.deepStrictEqual(bl, { label: 'Niet ondersteund', disabled: true }));
  await shot(`vanta-${VER}-geblokkeerd.png`);
  await page.$eval('[data-role=search]', (e) => { e.value = 'testgame 00'; e.dispatchEvent(new Event('input', { bubbles: true })); });
  await page.click('[data-game=game-3]');
  await sleep(300);
  await shot(`vanta-${VER}-catalogus-1200.png`);
  const t0 = Date.now();
  await load({ many: 1200 });
  check('laadtijd UI met 1200 games < 3 s', () => assert.ok(Date.now() - t0 < 3000, String(Date.now() - t0)));

  const csp = await page.evaluate(() => window.__csp);
  check('geen CSP-schendingen', () => assert.deepStrictEqual(csp, []));
  check('geen JS-fouten', () => assert.deepStrictEqual(errors, []));

  await browser.close(); server.close();
  results.forEach(([s, n]) => console.log(`${s.padEnd(4)} ${n}`));
  const fail = results.filter((r) => r[0] === 'FAIL').length;
  console.log(fail ? `UI: ${fail} FOUT(EN)` : `UI: alle ${results.filter((r) => r[0] === 'OK').length} controles geslaagd`);
  process.exit(fail ? 1 : 0);
})().catch((e) => { console.error(e); process.exit(2); });
