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
    status: {}, settings: { language: 'nl', catalogDir: '', catalogUrl: '', autoAttach: true }, hotkeys: {},
    now() { const d = new Date(); return String(d.getHours()).padStart(2, '0') + ':' + String(d.getMinutes()).padStart(2, '0'); },
    later(fn, ms = 160) { setTimeout(fn, ms); },
    log(text, level = 'info') { receive({ type: 'log', time: Mock.now(), text, level }); },
    game(id) {
      const full = root.VantaDev && root.VantaDev.games && root.VantaDev.games[id];
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
              const err = 'Infinite Grenades: geen unieke AOB gevonden (patroon 1: 0 treffer(s), patroon 2: 3 treffer(s), patroon 3: 3 treffer(s)). Niets gepatcht.';
              receive({ type: 'state', gameId: gid, cheats: [{ id: m.id, enabled: false, error: err }] }); Mock.log(err, 'error'); ack(false, err);
            } else { receive({ type: 'state', gameId: gid, cheats: [{ id: m.id, enabled: m.enabled, error: null }] }); Mock.log(`${c ? c.name : m.id} ${m.enabled ? 'ingeschakeld' : 'uitgeschakeld'}`); ack(); }
          });
          return;
        }
        case 'setValue': Mock.later(() => { receive({ type: 'state', gameId: gid, cheats: [{ id: m.id, value: m.value, error: null, hint: null }] }); ack(); }, 100); return;
        case 'button': Mock.later(() => { Mock.log('Uitgevoerd'); ack(); }); return;
        case 'disableAll': Mock.later(() => { Mock.log('Alle cheats uitgeschakeld'); ack(); }); return;
        case 'launch':
          Mock.status[gid] = 'launching'; receive({ type: 'status', gameId: gid, process: 'launching' }); Mock.log('Game wordt gestart via Steam…');
          Mock.later(() => { Mock.status[gid] = 'attached'; receive({ type: 'status', gameId: gid, process: 'attached', pid: 4242 }); Mock.log('Gekoppeld'); }, 1500);
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
        default: return ack();
      }
    },
  };

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
