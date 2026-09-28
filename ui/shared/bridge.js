/* Bridge: the only channel between UI and host.
 *   Bridge.send(msg) -> Promise<ack>     UI -> host (msg.type: ready, selectGame, toggle, setValue, button, disableAll,
 *                                         launch, attach, detach, getSettings, saveSettings, window, hotkeyCapture, openUrl)
 *   Bridge.on(type, fn)                  host -> UI ('game' | 'state' | 'status' | 'log' | 'hotkey' | 'settings' | 'focusGame' | 'ack')
 * Every outgoing message gets a numeric reqId; the host answers {type:'ack', reqId, ok, error?}.
 * Transport: WebView2 (window.chrome.webview) in Vanta.exe; a mock host in a normal browser.
 */
(function (root) {
  'use strict';
  const handlers = new Map();
  const pending = new Map();
  let seq = 0;

  function emit(type, payload) {
    (handlers.get(type) || []).forEach((fn) => { try { fn(payload); } catch (e) { console.error('[Bridge]', type, e); } });
  }
  function receive(raw) {
    const msg = typeof raw === 'string' ? JSON.parse(raw) : raw;
    if (!msg || !msg.type) return;
    if (msg.type === 'ack' && pending.has(msg.reqId)) {
      const { resolve, timer } = pending.get(msg.reqId);
      clearTimeout(timer); pending.delete(msg.reqId); resolve(msg);
    }
    emit(msg.type, msg);
  }

  const transports = {
    webview2: {
      available: () => !!(root.chrome && root.chrome.webview),
      init: () => root.chrome.webview.addEventListener('message', (e) => receive(e.data)),
      post: (m) => root.chrome.webview.postMessage(m),
    },
    mock: { available: () => true, init: () => {}, post: (m) => Mock.handle(m) },
  };
  const name = ['webview2', 'mock'].find((k) => transports[k].available());
  const T = transports[name];

  // ---- Mock host (browser only): simulates the Vanta host with the dev fixture ----
  const Mock = {
    status: {}, settings: { language: (root.location && new URLSearchParams(root.location.search).get('lang')) || 'en', catalogDir: '', catalogUrl: '', autoAttach: true }, hotkeys: {},
    now() { const d = new Date(); return String(d.getHours()).padStart(2, '0') + ':' + String(d.getMinutes()).padStart(2, '0'); },
    later(fn, ms = 160) { setTimeout(fn, ms); },
    L: (en, nl) => (Mock.settings.language === 'nl' ? nl : en),   // mock texts follow the language like the host does
    log(text, level = 'info') { receive({ type: 'log', time: Mock.now(), text, level }); },
    game(id) {
      const src = root.VantaDev && (Mock.settings.language === 'nl' && root.VantaDev.nl ? root.VantaDev.nl : root.VantaDev);
      const full = src && src.games && src.games[id];
      if (full) return JSON.parse(JSON.stringify(full));
      const g = root.TrainerData.games.find((x) => x.id === id);
      return Object.assign({}, g, { cheats: [], lazy: false });
    },
    cheat(gid, id) { const g = Mock.game(gid); return g.cheats.find((c) => c.id === id); },
    sendGame(id) {
      const g = Mock.game(id); const hk = Mock.hotkeys[id] || {};
      g.cheats.forEach((c) => { ['', 'inc', 'dec'].forEach((k) => { const key = k ? c.id + '/' + k : c.id; const f = k ? 'hotkey' + k[0].toUpperCase() + k.slice(1) : 'hotkey'; if (key in hk) c[f] = hk[key] || null; }); });
      receive({ type: 'game', game: g });
      receive({ type: 'status', gameId: id, process: Mock.status[id] || (id === root.TrainerData.selectedGameId ? 'attached' : 'notfound'), pid: 4242 });
    },
    handle(m) {
      const ack = (ok = true, error) => Mock.later(() => receive({ type: 'ack', reqId: m.reqId, ok, error }), 40);
      const gid = m.gameId;
      switch (m.type) {
        case 'ready': Mock.later(() => { Mock.sendGame(root.TrainerData.selectedGameId); Mock.handle({ type: 'getSettings' }); }, 20); return ack();
        case 'selectGame': Mock.later(() => Mock.sendGame(gid), 60); return ack();
        case 'toggle': {
          const c = Mock.cheat(gid, m.id);
          Mock.later(() => {
            if (m.enabled && c && c.id === 'inf_grenades') {
              const err = Mock.L('Infinite Grenades: no unique AOB found (pattern 1: 0 hit(s), pattern 2: 3 hit(s), pattern 3: 3 hit(s)). Nothing patched.', 'Infinite Grenades: geen unieke AOB gevonden (patroon 1: 0 treffer(s), patroon 2: 3 treffer(s), patroon 3: 3 treffer(s)). Niets gepatcht.');
              receive({ type: 'state', gameId: gid, cheats: [{ id: m.id, enabled: false, error: err }] }); Mock.log(err, 'error'); ack(false, err);
            } else { receive({ type: 'state', gameId: gid, cheats: [{ id: m.id, enabled: m.enabled, error: null }] }); Mock.log(`${c ? c.name : m.id} ${m.enabled ? Mock.L('enabled', 'ingeschakeld') : Mock.L('disabled', 'uitgeschakeld')}`); ack(); }
          });
          return;
        }
        case 'setValue': Mock.later(() => { receive({ type: 'state', gameId: gid, cheats: [{ id: m.id, value: m.value, error: null, hint: null }] }); ack(); }, 100); return;
        case 'button': Mock.later(() => { Mock.log(Mock.L('Done', 'Uitgevoerd')); ack(); }); return;
        case 'disableAll': Mock.later(() => { Mock.log(Mock.L('All cheats disabled', 'Alle cheats uitgeschakeld')); ack(); }); return;
        case 'launch':
          Mock.status[gid] = 'launching'; receive({ type: 'status', gameId: gid, process: 'launching' }); Mock.log(Mock.L('Starting game via Steam…', 'Game wordt gestart via Steam…'));
          Mock.later(() => { Mock.status[gid] = 'attached'; receive({ type: 'status', gameId: gid, process: 'attached', pid: 4242 }); Mock.log(Mock.L('Attached', 'Gekoppeld')); }, 1500);
          return ack();
        case 'attach': Mock.status[gid] = 'attached'; Mock.later(() => receive({ type: 'status', gameId: gid, process: 'attached', pid: 4242 }), 500); return ack();
        case 'detach': Mock.status[gid] = 'notfound'; Mock.later(() => receive({ type: 'status', gameId: gid, process: 'notfound' })); return ack();
        case 'getSettings': Mock.later(() => receive({ type: 'settings', settings: Mock.settings, catalog: { source: 'dev', games: root.TrainerData.games.length }, dataDir: '%LOCALAPPDATA%\\Vanta', version: root.TrainerData.app.version })); return ack();
        case 'saveSettings': {
          const s = m.settings || {};
          if (s.hotkeys && s.gameId) Mock.hotkeys[s.gameId] = s.hotkeys;
          ['language', 'catalogDir', 'catalogUrl', 'autoAttach'].forEach((k) => { if (k in s) Mock.settings[k] = s[k]; });
          Mock.later(() => { Mock.handle({ type: 'getSettings' }); Mock.sendGame(root.TrainerCore.state.selectedId); });
          return ack();
        }
        case 'getRequests': Mock.later(() => receive({ type: 'requests', ok: true, error: null, loggedIn: true, items: Mock.requests.slice().sort((a, b) => b.votes - a.votes) }), 220); return ack();
        case 'steamSearch': {
          const q = String(m.term || '').toLowerCase(), id = Number(q.replace(/\D/g, '')) || 0;
          const items = Mock.steam.filter((x) => x.appid === id || x.name.toLowerCase().includes(q)).slice(0, 8)
            .map((x) => Object.assign({}, x, { image: `https://cdn.cloudflare.steamstatic.com/steam/apps/${x.appid}/capsule_231x87.jpg`, cover: `https://shared.cloudflare.steamstatic.com/store_item_assets/steam/apps/${x.appid}/header.jpg` }));
          Mock.later(() => receive({ type: 'steamSearch', term: m.term, ok: true, error: null, items }), 380); return ack();
        }
        case 'requestVote':
        case 'requestUnvote': {
          let r = Mock.requests.find((x) => x.appid === m.appid);
          if (!r && m.type === 'requestVote') { r = { appid: m.appid, name: m.name, cover: m.cover, status: 'open', note: null, votes: 0, votes7d: 0, voted: false }; Mock.requests.push(r); }
          if (r && r.voted !== (m.type === 'requestVote')) { r.voted = !r.voted; r.votes += r.voted ? 1 : -1; r.votes7d = Math.max(0, r.votes7d + (r.voted ? 1 : -1)); }
          if (r && !r.votes && r.status === 'open') Mock.requests = Mock.requests.filter((x) => x !== r);
          Mock.later(() => { receive({ type: 'requestVoteResult', appid: m.appid, unvote: m.type === 'requestUnvote', ok: true, error: null, request: r ? Object.assign({}, r) : null }); Mock.handle({ type: 'getRequests' }); }, 260);
          return ack();
        }
        default: return ack();
      }
    },
    // dev fixture for "Request a game" (public Steam app ids)
    requests: [
      { appid: 264710, name: 'Subnautica', status: 'planned', note: null, votes: 41, votes7d: 9, voted: true },
      { appid: 105600, name: 'Terraria', status: 'open', note: null, votes: 27, votes7d: 6, voted: false },
      { appid: 413150, name: 'Stardew Valley', status: 'in_progress', note: 'Money and energy first.', votes: 23, votes7d: 4, voted: false },
      { appid: 367520, name: 'Hollow Knight', status: 'open', note: null, votes: 14, votes7d: 3, voted: false },
      { appid: 632360, name: 'Risk of Rain 2', status: 'open', note: null, votes: 6, votes7d: 1, voted: false },
    ].map((x) => Object.assign({ cover: null }, x)),
    steam: [
      { appid: 264710, name: 'Subnautica' }, { appid: 848450, name: 'Subnautica: Below Zero' }, { appid: 105600, name: 'Terraria' },
      { appid: 413150, name: 'Stardew Valley' }, { appid: 367520, name: 'Hollow Knight' }, { appid: 632360, name: 'Risk of Rain 2' },
      { appid: 1145360, name: 'Hades' }, { appid: 892970, name: 'Valheim' }, { appid: 252490, name: 'Rust' },
    ],
  };

  // dev preview only: sample release notes for ?update=x.y.z&whatsnew=1
  root.VantaMockReleaseNotes = [
    '## New', '', '- **Request a game**: search Steam for a game and vote for it via *Request a game* at the bottom left.',
    '- Compact update toast; the full release notes are under **What\'s new**.', '',
    '## Improved', '', '1. Voting works after signing in with Discord; the list is visible to everyone.', '2. Links open in your own browser.',
    '   - Long lists scroll nicely too.', '', '### Note', '', '> Updating turns off all cheats and restores your games.', '',
    '<details>', '<summary>Nederlands</summary>', '',
    '## Nieuw', '', '- **Game aanvragen**: zoek een game op Steam en stem erop via *Game aanvragen* linksonder.',
    '- Compacte update-melding; de volledige release-opmerkingen staan onder **Wat is er nieuw**.', '',
    '## Verbeterd', '', '1. Stemmen kan na inloggen met Discord, de lijst is voor iedereen zichtbaar.', '2. Links openen in je eigen browser.',
    '   - Ook lange lijsten scrollen netjes.', '', '### Let op', '', '> Updaten zet alle cheats uit en herstelt je games.', '',
    '</details>', '',
    '---', '', '**Full Changelog**: https://github.com/Rick007110/Vanta/compare/v0.3.1...v0.3.2',
  ].join('\n');

  T.init();

  root.Bridge = {
    transport: name,
    send(msg) {
      const m = Object.assign({ reqId: ++seq }, msg);
      return new Promise((resolve) => {
        const ms = (root.TrainerData && root.TrainerData.app && root.TrainerData.app.ackTimeout) || 5000;
        const timer = setTimeout(() => { pending.delete(m.reqId); resolve({ type: 'ack', reqId: m.reqId, ok: false, error: 'timeout' }); }, ms);
        pending.set(m.reqId, { resolve, timer });
        try { T.post(m); } catch (e) { clearTimeout(timer); pending.delete(m.reqId); resolve({ type: 'ack', reqId: m.reqId, ok: false, error: String(e) }); }
      });
    },
    on(type, fn) {
      if (!handlers.has(type)) handlers.set(type, []);
      handlers.get(type).push(fn);
      return () => handlers.set(type, handlers.get(type).filter((f) => f !== fn));
    },
    _receive: receive,
  };
})(window);
