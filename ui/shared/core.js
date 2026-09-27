/* TrainerCore (Vanta): state, rendering orchestration (via the templates in app.js), DOM patching, events, Bridge wiring.
 * Hooks used by templates: [data-slot=library|hero|cheats|panel|modal], [data-cheat][data-type], [role=switch],
 * [data-bind=...], [data-action=...]. See app.js.
 */
(function (root) {
  'use strict';
  const D = root.TrainerData;
  const t = root.t;
  const PROD = !!D.hosted;
  const STATUS = {
    attached:     { primary: { label: 'pr.detach',    icon: 'unlink',  action: 'detach', kind: 'secondary' } },
    notfound:     { primary: { label: 'pr.launch',    icon: 'play',    action: 'launch', kind: 'primary' } },
    wrongversion: { primary: { label: 'pr.force',     icon: 'link',    action: 'attach', kind: 'primary' } },
    attaching:    { primary: { label: 'pr.attaching', icon: 'refresh', action: 'none',   kind: 'primary' } },
    launching:    { primary: { label: 'pr.launching', icon: 'refresh', action: 'none',   kind: 'primary' } },
    error:        { primary: { label: 'pr.retry',     icon: 'refresh', action: 'attach', kind: 'primary' } },
    blocked:      { primary: { label: 'pr.blocked',   icon: 'ban',     action: 'none',   kind: 'secondary' } },
  };
  const esc = (s) => String(s ?? '').replace(/[&<>"']/g, (c) => ({ '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;', "'": '&#39;' }[c]));
  const fmt = (n) => (n == null || isNaN(n)) ? '—' : Number(n).toLocaleString(root.I18N.locale(), { maximumFractionDigits: 3 });
  const norm = (s) => String(s || '').normalize('NFD').replace(/[\u0300-\u036f]/g, '').toLowerCase();
  const clamp = (v, a, b) => Math.min(b, Math.max(a, v));
  const reduced = () => root.matchMedia && root.matchMedia('(prefers-reduced-motion: reduce)').matches;
  const LIB_CAP = 250;   // rendered items per group; search narrows the rest (1000+ games stay fast)

  const state = {
    selectedId: D.selectedGameId, query: '', category: '', tab: 'cheats',
    status: {}, cheats: {}, log: D.log, settings: null, catalog: null, dataDir: '', capture: null, modal: null,
  };
  D.games.forEach((g) => { state.cheats[g.id] = {}; (g.cheats || []).forEach((c) => (state.cheats[g.id][c.id] = Object.assign({}, c))); });

  let R, T, hooks = {};
  const $ = (sel, el = R) => el.querySelector(sel);
  const $$ = (sel, el = R) => Array.from(el.querySelectorAll(sel));
  const game = () => D.games.find((g) => g.id === state.selectedId) || D.games[0];
  const cheatList = () => { const g = game(); return g ? (g.cheats || []).map((c) => state.cheats[g.id][c.id]).filter(Boolean) : []; };
  const cheat = (id) => state.cheats[state.selectedId] && state.cheats[state.selectedId][id];
  const statusOf = (id) => (state.status[id] && state.status[id].process) || (D.games.find((g) => g.id === id) && (D.games.find((g) => g.id === id).antiCheat || D.games.find((g) => g.id === id).onlineOnly) ? 'blocked' : 'notfound');
  const status = () => statusOf(state.selectedId);
  const interactive = () => status() === 'attached';

  // ---------- art ----------
  const CDN = { cover: 'library_600x900.jpg', hero: 'library_hero.jpg', logo: 'logo.png', header: 'header.jpg' };
  function artUrl(g, kind) {
    if (!g || !g.steamAppId) return null;
    return PROD ? `https://vanta.example/art/${g.steamAppId}/${kind}` : `https://cdn.cloudflare.steamstatic.com/steam/apps/${g.steamAppId}/${CDN[kind]}`;
  }
  root.vantaArt = {
    fail: (img) => { const p = img.parentNode; img.remove(); if (p) p.classList.remove('has-img'); },
    ok: (img) => { const p = img.parentNode; if (p) p.classList.add('has-img'); },
  };

  // ---------- rendering ----------
  function categories() {
    const count = {};
    D.games.forEach((g) => (g.categories || []).forEach((c) => (count[c] = (count[c] || 0) + 1)));
    return Object.keys(count).sort((a, b) => count[b] - count[a] || a.localeCompare(b)).slice(0, 40);
  }
  function renderCategories() {
    const sel = $('[data-role=category]'); if (!sel) return;
    const cats = categories();
    sel.innerHTML = `<option value="">${esc(t('cat.all'))}</option>` + cats.map((c) => `<option value="${esc(c)}"${c === state.category ? ' selected' : ''}>${esc(c)}</option>`).join('');
    sel.closest('[data-cat]').hidden = cats.length < 2;
  }
  function renderLibrary() {
    const q = norm(state.query.trim());
    const matches = D.games.filter((g) => (!q || norm(g.name).includes(q) || norm(g.process).includes(q) || (g.categories || []).some((c) => norm(c).includes(q)))
      && (!state.category || (g.categories || []).includes(state.category)));
    const slot = $('[data-slot=library]');
    if (!matches.length) { slot.innerHTML = T.libraryEmpty(esc(state.query)); return; }
    const groups = q || state.category ? [{ id: '_results', title: t('group.results'), items: matches }]
      : D.groups.map((gr) => ({ ...gr, title: t('group.' + gr.id), items: matches.filter((g) => (g.group || 'all') === gr.id) })).filter((gr) => gr.items.length);
    slot.innerHTML = groups.map((gr) => {
      const shown = gr.items.slice(0, LIB_CAP);
      const more = gr.items.length - shown.length;
      return T.libraryGroup(gr, shown.map((g) => T.libraryItem(g, { selected: g.id === state.selectedId, status: statusOf(g.id), cover: artUrl(g, 'cover') })).join('') + (more > 0 ? T.libraryMore(more) : ''), gr.items.length);
    }).join('');
    $$('[data-action=select-game]').forEach((e) => (e.dataset.status = statusOf(e.dataset.game)));
  }
  function renderGame() {
    const g = game();
    if (!g) { $('[data-slot=hero]').innerHTML = ''; $('[data-slot=cheats]').innerHTML = T.tableEmpty({ name: '' }); return; }
    renderHero(g);
    renderTab();
    patchStatus();
    if (hooks.onGameChange) hooks.onGameChange(g);
  }
  const heroKey = (g) => JSON.stringify([g.id, g.scope || '', g.install ? g.install.primary || (g.install.owned || [])[0] || null : null]);
  function renderHero(g) {
    const slot = $('[data-slot=hero]');
    slot.innerHTML = T.hero(g, { hero: artUrl(g, 'hero'), cover: artUrl(g, 'cover'), logo: artUrl(g, 'logo') });
    slot.dataset.key = heroKey(g);
  }
  function renderTab() {
    const g = game();
    $$('[data-action=tab]').forEach((b) => b.setAttribute('aria-selected', String(b.dataset.value === state.tab)));
    R.dataset.tab = state.tab;
    const slot = $('[data-slot=cheats]'), panel = $('[data-slot=panel]');
    slot.hidden = state.tab !== 'cheats'; panel.hidden = state.tab === 'cheats';
    if (state.tab === 'cheats') {
      const list = cheatList();
      if (g.lazy) slot.innerHTML = T.loading();
      else if (!list.length) slot.innerHTML = T.tableEmpty(g);
      else {
        const parts = D.sections.map((s) => {
          const items = list.filter((c) => c.section === s.id);
          const sec = Object.assign({}, s, { title: t('sec.' + s.id) });
          return items.length ? { section: sec, items, html: T.section(sec, items.map((c) => (T.cheat[c.type] || T.cheat.toggle)(c)).join(''), items) } : null;
        }).filter(Boolean);
        const other = list.filter((c) => !D.sections.some((s) => s.id === c.section));
        if (other.length) parts.push({ items: other, html: T.section({ id: 'extra2', title: t('sec.extra'), icon: 'sparkles' }, other.map((c) => (T.cheat[c.type] || T.cheat.toggle)(c)).join(''), other) });
        slot.innerHTML = T.layout ? T.layout(parts) : parts.map((p) => p.html).join('');
      }
      list.forEach(patchCheat); patchCounts();
    } else if (state.tab === 'hotkeys') {
      panel.innerHTML = g.lazy ? T.loading() : T.hotkeys(g, hotkeyRows(), state.capture);
    } else {
      panel.innerHTML = g.lazy ? T.loading() : T.notes(g, cheatList());
    }
  }

  // ---------- patching ----------
  function patchCheat(c) {
    const el = $(`[data-cheat="${c.id}"]`); if (!el) return;
    const isVal = c.type === 'number' || c.type === 'slider';
    const st = c.error ? 'error' : isVal && c.hint ? 'hint' : c.type === 'toggle' ? (c.enabled ? 'on' : 'off') : 'value';
    el.dataset.state = st;
    el.classList.toggle('is-pending', !!c._pending);
    const dis = !interactive() || (isVal && (!!c.hint && c.value == null));
    el.toggleAttribute('data-disabled', dis);
    const sw = $('[role=switch]', el);
    if (sw) { sw.setAttribute('aria-checked', String(!!c.enabled)); sw.disabled = dis; }
    const run = $('[data-action=run]', el); if (run) run.disabled = dis;
    const lbl = $('[data-bind=state-label]', el);
    if (lbl) lbl.textContent = c.error ? t('row.err') : c.enabled ? t('row.on') : t('row.off');
    const err = $('[data-bind=error]', el);
    if (err) { err.textContent = c.error || ''; err.hidden = !c.error; }
    el.title = c.error ? `${c.name}: ${c.error}` : (c.note || '');
    const sub = $('[data-bind=sub]', el);
    if (sub) { const txt = c.hint && c.value == null && !c.error ? c.hint : (sub.dataset.default || ''); sub.textContent = txt; sub.hidden = !txt || !!c.error; }
    const inp = $('[data-role=value-input]', el);
    if (inp && document.activeElement !== inp) inp.value = c.value == null ? '—' : fmt(c.value);
    if (inp) inp.disabled = dis;
    $$('[data-action=step]', el).forEach((b) => {
      b.disabled = dis || c.value == null || (b.dataset.dir === '-1' ? c.value <= c.min : c.value >= c.max);
    });
    const rg = $('[data-role=slider]', el);
    if (rg) {
      const v = c.value == null ? c.min : clamp(c.value, c.min, c.max);
      rg.value = v; rg.disabled = dis;
      rg.style.setProperty('--p', ((v - c.min) / ((c.max - c.min) || 1)) * 100 + '%');
      const shown = c.value == null ? '—' : (c.format || '{v}').replace('{v}', fmt(c.value));
      rg.setAttribute('aria-valuetext', shown);
      const out = $('[data-bind=slider-out]', el); if (out) out.textContent = shown;
    }
  }
  function patchCounts() {
    const list = cheatList(), g = game();
    const toggles = list.filter((c) => c.type === 'toggle');
    const on = (arr) => arr.filter((c) => c.type === 'toggle' && c.enabled && !c.error).length;
    $$('[data-bind=active-count]').forEach((e) => (e.textContent = !list.length ? (g && g.lazy ? t('st.loading') : t('count.none')) : interactive() ? t('count.active', { on: on(list), n: toggles.length }) : t('count.wait')));
    $$('[data-bind=section-count]').forEach((e) => {
      const items = list.filter((c) => c.section === e.dataset.section);
      const tg = items.filter((c) => c.type === 'toggle');
      e.textContent = tg.length ? `${on(items)}/${tg.length}` : `${items.length}`;
    });
  }
  function patchStatus() {
    const g = game(); if (!g) return;
    const st = status(), S = STATUS[st] || STATUS.notfound, detail = state.status[g.id] && state.status[g.id].detail;
    R.dataset.status = st;
    $$('[data-bind=status-pill]').forEach((e) => (e.dataset.status = st));
    $$('[data-bind=status-label]').forEach((e) => (e.textContent = t('st.' + st)));
    const p = g.process || '';
    let hint = { attached: t('hint.attached', { p }), notfound: g.install && g.install.primary ? t('hint.notfound.via', { store: g.install.primary.storeName }) : g.steamAppId ? t('hint.notfound') : t('hint.notfound.nosteam'), wrongversion: t('hint.wrongversion'),
      attaching: t('hint.attaching', { p }), launching: t('hint.launching'), error: t('hint.error'), blocked: t('hint.blocked') }[st] || '';
    if (detail) hint = st === 'error' || st === 'blocked' ? detail : `${hint} ${detail}`;
    $$('[data-bind=status-hint]').forEach((e) => { e.textContent = hint; e.title = hint; });
    $$('[data-action=primary]').forEach((b) => {
      b.dataset.kind = S.primary.kind; b.dataset.do = S.primary.action;
      const via = st === 'notfound' && g.install && g.install.primary && g.install.primary.storeName;
      b.disabled = S.primary.action === 'none' || (st === 'notfound' && !g.steamAppId && !via);
      const l = $('[data-bind=primary-label]', b); if (l) l.textContent = via ? t('pr.launchVia', { store: via }) : t(S.primary.label);
      const i = $('[data-bind=primary-icon]', b); if (i) i.innerHTML = root.Icon(S.primary.icon, 18);
    });
    $$('[data-action=select-game]').forEach((e) => (e.dataset.status = statusOf(e.dataset.game)));
    cheatList().forEach(patchCheat);
    patchCounts();
  }
  function patchLog() {
    $$('[data-bind=log-time]').forEach((e) => (e.textContent = state.log.time));
    $$('[data-bind=log-text]').forEach((e) => { e.textContent = state.log.text; e.dataset.level = state.log.level || 'info'; e.title = state.log.text; });
    $$('[data-bind=log-line]').forEach((e) => { e.classList.remove('is-new'); void e.offsetWidth; e.classList.add('is-new'); });
  }
  function patchFooter() {
    $$('[data-bind=engine]').forEach((e) => { e.dataset.on = String(PROD); e.textContent = PROD ? t('sb.engine') : t('sb.dev'); });
    $$('[data-bind=table-version]').forEach((e) => (e.textContent = `${root.Brand.name} ${D.app.version} · ${(D.games.length === 1 ? t('sb.game1') : t('sb.games', { n: D.games.length }))}`));
  }
  function relabel() {
    root.I18N.apply(R);
    renderCategories(); renderLibrary(); renderGame(); patchFooter(); patchLog();
  }

  // ---------- actions ----------
  function toggle(id, force) {
    const c = cheat(id); if (!c || c.type !== 'toggle' || !interactive()) return;
    const enabled = force ?? !(c.enabled && !c.error);
    c.enabled = enabled; c.error = null; c._pending = true;
    patchCheat(c); patchCounts();
    root.Bridge.send({ type: 'toggle', gameId: state.selectedId, id, enabled }).then(() => { c._pending = false; patchCheat(c); });
  }
  function flash(id) {
    const el = $(`[data-cheat="${id}"]`); if (!el) return;
    el.classList.remove('is-flash'); void el.offsetWidth; el.classList.add('is-flash');
    clearTimeout(el._ft); el._ft = setTimeout(() => el.classList.remove('is-flash'), reduced() ? 400 : 900);
  }
  const commitTimers = {};
  function setValue(id, v, { commit = true, immediate = false } = {}) {
    const c = cheat(id); if (!c || c.error || (c.hint && c.value == null)) return;
    const step = c.step || 1;
    c.value = clamp(v, c.min, c.max);
    if (c.type === 'slider') c.value = clamp(Math.round(v / step) * step, c.min, c.max);
    patchCheat(c);
    if (!commit) return;
    clearTimeout(commitTimers[id]);
    const send = () => root.Bridge.send({ type: 'setValue', gameId: state.selectedId, id, value: c.value });
    immediate ? send() : (commitTimers[id] = setTimeout(send, 350));
  }
  function select(id, { silent = false } = {}) {
    if (!D.games.some((g) => g.id === id)) return;
    if (id === state.selectedId && !game().lazy) return;
    state.selectedId = id; state.capture = null; renderLibrary(); renderGame();
    if (!silent) root.Bridge.send({ type: 'selectGame', gameId: id });
    const el = $(`[data-game="${id}"]`); if (el && el.scrollIntoView) el.scrollIntoView({ block: 'nearest' });
  }
  let lastPrimary = 0;
  function primary(btn) {
    const g = game(), a = btn.dataset.do;
    if (btn.disabled || a === 'none') return;
    const now = Date.now(); if (now - lastPrimary < 1500) return;   // no double launch/attach
    lastPrimary = now;
    if (a === 'launch') { state.status[g.id] = { process: 'launching' }; patchStatus(); root.Bridge.send({ type: 'launch', gameId: g.id }); }
    else if (a === 'attach') root.Bridge.send({ type: 'attach', gameId: g.id, force: true });
    else if (a === 'detach') root.Bridge.send({ type: 'detach', gameId: g.id });
  }
  function holdStep(btn) {
    const id = btn.closest('[data-cheat]').dataset.cheat, dir = Number(btn.dataset.dir);
    const c = cheat(id); const t0 = Date.now(); let iv;
    const step = () => { const mult = Date.now() - t0 > 1200 ? 10 : 1; setValue(id, c.value + dir * (c.step || 1) * mult, { commit: false }); btn.classList.add('is-pressed'); };
    step();
    const to = setTimeout(() => (iv = setInterval(step, 70)), 380);
    const stop = () => { clearTimeout(to); clearInterval(iv); btn.classList.remove('is-pressed'); setValue(id, c.value, { immediate: true });
      ['pointerup', 'pointerleave', 'pointercancel'].forEach((e) => btn.removeEventListener(e, stop)); };
    ['pointerup', 'pointerleave', 'pointercancel'].forEach((e) => btn.addEventListener(e, stop));
  }

  // ---------- hotkeys ----------
  const NP = { NumpadAdd: 'Numpad+', NumpadSubtract: 'Numpad-', NumpadMultiply: 'Numpad*', NumpadDivide: 'Numpad/', NumpadDecimal: 'Numpad.' };
  const NAV = { Insert: 'Insert', Delete: 'Delete', Home: 'Home', End: 'End', PageUp: 'PageUp', PageDown: 'PageDown', ArrowUp: 'Up', ArrowDown: 'Down', ArrowLeft: 'Left', ArrowRight: 'Right', Space: 'Space', Tab: 'Tab', Pause: 'Pause' };
  function keyName(e) {
    const c = e.code || '';
    if (/^F([1-9]|1\d|2[0-4])$/.test(c)) return c;
    if (c.startsWith('Key')) return c.slice(3);
    if (c.startsWith('Digit')) return c.slice(5);
    if (NP[c]) return NP[c];
    if (/^Numpad\d$/.test(c)) return c;
    return NAV[c] || null;
  }
  function comboOf(e) {
    const k = keyName(e); if (!k) return null;
    return [e.ctrlKey && 'Ctrl', e.altKey && 'Alt', e.shiftKey && 'Shift', e.metaKey && 'Win', k].filter(Boolean).join('+');
  }
  function hotkeyRows() {
    const rows = [];
    cheatList().forEach((c) => {
      if (c.type === 'number' || c.type === 'slider') {
        rows.push({ key: c.id + '/inc', cheat: c, kind: 'inc', value: c.hotkeyInc, def: c.defaultHotkeyInc });
        rows.push({ key: c.id + '/dec', cheat: c, kind: 'dec', value: c.hotkeyDec, def: c.defaultHotkeyDec });
      } else rows.push({ key: c.id, cheat: c, kind: '', value: c.hotkey, def: c.defaultHotkey });
    });
    return rows;
  }
  function saveHotkey(key, combo /* string, '' = none, null = default */) {
    const g = game();
    const map = {};
    hotkeyRows().forEach((r) => {
      let v = r.value || '';
      if (r.key === key) v = combo === null ? (r.def || '') : combo;
      if (v !== (r.def || '')) map[r.key] = v;
      // update local copy right away
      if (r.key === key) { const f = r.kind ? 'hotkey' + r.kind[0].toUpperCase() + r.kind.slice(1) : 'hotkey'; r.cheat[f] = v || null; }
    });
    root.Bridge.send({ type: 'saveSettings', settings: { gameId: g.id, hotkeys: map } });
  }
  function setCapture(key) {
    state.capture = key;
    root.Bridge.send({ type: 'hotkeyCapture', active: !!key });
    renderTab();
  }

  // ---------- settings modal ----------
  function openSettings() {
    state.modal = 'settings';
    root.Bridge.send({ type: 'getSettings' });
    renderModal();
  }
  function renderModal() {
    const slot = $('[data-slot=modal]');
    if (!state.modal) { slot.innerHTML = ''; slot.hidden = true; return; }
    slot.hidden = false;
    slot.innerHTML = T.settings(state.settings || { language: root.I18N.lang, catalogDir: '', catalogUrl: '', autoAttach: true }, { catalog: state.catalog, dataDir: state.dataDir, version: D.app.version, update: state.updateCheck });
    const first = $('[data-set=language]', slot); if (first) first.focus();
  }
  function closeModal() { state.modal = null; renderModal(); }
  function saveSettingsFromModal() {
    const slot = $('[data-slot=modal]');
    const s = {
      language: $('[data-set=language]', slot).value,
      catalogDir: $('[data-set=catalogDir]', slot).value.trim(),
      catalogUrl: $('[data-set=catalogUrl]', slot).value.trim(),
      autoAttach: $('[data-set=autoAttach]', slot).checked,
    };
    const langChanged = s.language !== root.I18N.lang;
    state.settings = Object.assign({}, state.settings, s);
    root.Bridge.send({ type: 'saveSettings', settings: s });
    closeModal();
    if (langChanged) { root.I18N.lang = s.language; relabel(); }
    state.log = { time: state.log.time, text: t('set.saved'), level: 'info' }; patchLog();
  }

  // ---------- updates ----------
  function renderToast() {
    const slot = $('[data-slot=toast]'); if (!slot || !T.updateToast) return;
    const u = state.update;
    if (!u) { slot.innerHTML = ''; slot.hidden = true; return; }
    slot.hidden = false; slot.innerHTML = T.updateToast(u);
  }
  function onUpdateStatus(s) {
    state.updateCheck = s;
    if (state.modal === 'settings') {
      const el = $('[data-bind=update-status]'); if (el && T.updLine) el.textContent = T.updLine(s);
      const b = $('[data-action=check-update]'); if (b) b.disabled = s.state === 'checking';
    }
    if (['downloading', 'installing', 'pending', 'updated', 'failed', 'error'].includes(s.state) && (state.update || ['updated', 'failed', 'pending'].includes(s.state))) {
      state.update = Object.assign({}, state.update || {}, s);
      renderToast();
    }
  }

  // ---------- events ----------
  function bind() {
    R.addEventListener('click', (e) => {
      const act = e.target.closest('[data-action]');
      const a = act && act.dataset.action;
      if (a === 'select-game') return select(act.dataset.game);
      if (a === 'primary') return primary(act);
      if (a === 'tab') { state.tab = act.dataset.value; if (state.capture) setCapture(null); return renderTab(); }
      if (a === 'open-hotkeys') { state.tab = 'hotkeys'; return renderTab(); }
      if (a === 'open-settings') return openSettings();
      if (a === 'modal-close') return closeModal();
      if (a === 'modal-save') return saveSettingsFromModal();
      if (a === 'modal-backdrop' && e.target === act) return closeModal();
      if (a === 'hk-change') return setCapture(state.capture === act.dataset.key ? null : act.dataset.key);
      if (a === 'hk-clear') { saveHotkey(act.dataset.key, ''); return renderTab(); }
      if (a === 'hk-default') { saveHotkey(act.dataset.key, null); return renderTab(); }
      if (a === 'disable-all') { cheatList().forEach((c) => { if (c.type === 'toggle' && c.enabled) { c.enabled = false; patchCheat(c); } }); patchCounts(); return root.Bridge.send({ type: 'disableAll', gameId: state.selectedId }); }
      if (a === 'window') return root.Bridge.send({ type: 'window', action: act.dataset.value });
      if (a === 'clear-search') { const s = $('[data-role=search]'); s.value = ''; state.query = ''; state.category = ''; renderCategories(); renderLibrary(); return s.focus(); }
      if (a === 'hotkey-chip') { state.tab = 'hotkeys'; return renderTab(); }
      if (a === 'retry') return toggle(act.closest('[data-cheat]').dataset.cheat, true);
      if (a === 'run') { const id = act.closest('[data-cheat]').dataset.cheat; flash(id); return root.Bridge.send({ type: 'button', gameId: state.selectedId, id }); }
      if (a === 'open-url') return root.Bridge.send({ type: 'openUrl', url: act.dataset.url });
      if (a === 'check-update') { onUpdateStatus({ state: 'checking' }); return root.Bridge.send({ type: 'checkUpdate' }); }
      if (a === 'update-now') { state.update = Object.assign({}, state.update, { state: 'downloading', progress: 0 }); renderToast(); return root.Bridge.send({ type: 'updateNow' }); }
      if (a === 'update-later') { state.update = null; renderToast(); return root.Bridge.send({ type: 'updateLater' }); }
      if (a === 'update-close') { state.update = null; return renderToast(); }
      if (a === 'step') return;
      const card = e.target.closest('[data-cheat][data-type=toggle]');
      if (card && !card.hasAttribute('data-disabled') && (!act || a === 'toggle')) toggle(card.dataset.cheat);
    });
    R.addEventListener('pointerdown', (e) => {
      // window drag fallback (WebView2 runtimes without CSS app-region support)
      if (PROD && e.button === 0 && e.target.closest('.drag') && !e.target.closest('button, input, select, a, .no-drag')) root.Bridge.send({ type: 'window', action: 'drag' });
      const b = e.target.closest('[data-action=step]');
      if (b && !b.disabled && e.button === 0) { e.preventDefault(); holdStep(b); }
    });
    R.addEventListener('dblclick', (e) => {
      if (PROD && e.target.closest('.drag') && !e.target.closest('button, input, select, a, .no-drag')) root.Bridge.send({ type: 'window', action: 'maximize' });
    });
    R.addEventListener('keydown', (e) => {
      const b = e.target.closest && e.target.closest('[data-action=step]');
      if (b && (e.key === 'Enter' || e.key === ' ')) { e.preventDefault(); const c = cheat(b.closest('[data-cheat]').dataset.cheat); setValue(c.id, c.value + Number(b.dataset.dir) * (c.step || 1)); }
      const inp = e.target.closest && e.target.closest('[data-role=value-input]');
      if (inp && e.key === 'Enter') inp.blur();
      if (inp && e.key === 'Escape') { const c = cheat(inp.closest('[data-cheat]').dataset.cheat); inp.value = fmt(c.value); inp.blur(); }
      if (inp && (e.key === 'ArrowUp' || e.key === 'ArrowDown')) { e.preventDefault(); const c = cheat(inp.closest('[data-cheat]').dataset.cheat); setValue(c.id, c.value + (e.key === 'ArrowUp' ? 1 : -1) * (c.step || 1)); inp.value = fmt(c.value); }
    });
    R.addEventListener('input', (e) => {
      const tg = e.target;
      if (tg.matches('[data-role=search]')) { state.query = tg.value; renderLibrary(); }
      if (tg.matches('[data-role=value-input]')) tg.value = tg.value.replace(/[^\d.,-]/g, '');
      if (tg.matches('[data-role=slider]')) setValue(tg.closest('[data-cheat]').dataset.cheat, Number(tg.value), { commit: false });
    });
    R.addEventListener('change', (e) => {
      if (e.target.matches('[data-role=slider]')) setValue(e.target.closest('[data-cheat]').dataset.cheat, Number(e.target.value), { immediate: true });
      if (e.target.matches('[data-role=category]')) { state.category = e.target.value; renderLibrary(); }
    });
    R.addEventListener('focusout', (e) => {
      const tg = e.target;
      if (tg.matches && tg.matches('[data-role=value-input]')) {
        const c = cheat(tg.closest('[data-cheat]').dataset.cheat); if (!c) return;
        const raw = tg.value.replace(root.I18N.lang === 'en' ? /,/g : /\./g, '').replace(',', '.');
        const v = parseFloat(raw);
        if (!isNaN(v) && v !== c.value) setValue(c.id, v, { immediate: true });
        tg.value = fmt(c.value);
      }
    });
    document.addEventListener('keydown', (e) => {
      if (state.capture) {
        e.preventDefault(); e.stopPropagation();
        if (e.key === 'Escape') return setCapture(null);
        const combo = comboOf(e); if (!combo) return;          // modifier alone: wait for the key
        const key = state.capture;
        const clash = hotkeyRows().find((r) => r.key !== key && (r.value || '').toLowerCase() === combo.toLowerCase());
        if (clash) { state.log = { time: '', text: t('hk.inuse', { k: combo, c: clash.cheat.name }), level: 'error' }; patchLog(); return; }
        saveHotkey(key, combo);
        return setCapture(null);
      }
      if (state.modal && e.key === 'Escape') return closeModal();
      if ((e.ctrlKey || e.metaKey) && e.key.toLowerCase() === 'k') { e.preventDefault(); const s = $('[data-role=search]'); s.focus(); s.select(); return; }
      if (e.key === 'Escape' && e.target.matches && e.target.matches('[data-role=search]')) { e.target.value = ''; state.query = ''; renderLibrary(); return; }
      if (PROD) return;                                         // real app: global hotkeys are handled by the host
      if (e.repeat || (e.target.matches && e.target.matches('input'))) return;
      const combo = (comboOf(e) || '').toLowerCase();
      const c = cheatList().find((x) => x.type === 'toggle' && x.hotkey && x.hotkey.toLowerCase() === combo);
      if (c) { e.preventDefault(); flash(c.id); toggle(c.id); }
    }, true);

    // host -> UI
    root.Bridge.on('game', (m) => {
      const g = m.game; if (!g) return;
      const idx = D.games.findIndex((x) => x.id === g.id);
      const prevEntry = idx >= 0 ? D.games[idx] : {};
      const merged = Object.assign({}, prevEntry, g, { lazy: false, group: prevEntry.group || 'all' });
      if (idx >= 0) D.games[idx] = merged; else D.games.push(merged);
      const old = state.cheats[g.id] || {};
      state.cheats[g.id] = {};
      (g.cheats || []).forEach((c) => {
        const p = old[c.id];
        state.cheats[g.id][c.id] = Object.assign({}, c, p ? { value: p.value ?? c.value, hint: p.value != null ? null : (p.hint ?? c.hint), error: p.error } : {});
      });
      if (g.id === state.selectedId) { if ($('[data-slot=hero]').dataset.key !== heroKey(merged)) renderHero(merged); renderTab(); patchStatus(); const lib = $(`[data-game="${g.id}"] .lib-meta`); if (!lib) renderLibrary(); }
    });
    root.Bridge.on('state', (m) => {
      const gid = m.gameId || state.selectedId;
      const map = state.cheats[gid]; if (!map) return;
      (m.cheats || []).forEach((p) => { const c = map[p.id]; if (!c) return; Object.assign(c, p); c._pending = false; if (gid === state.selectedId) patchCheat(c); });
      if (gid === state.selectedId) patchCounts();
    });
    root.Bridge.on('status', (m) => {
      const gid = m.gameId || state.selectedId;
      state.status[gid] = { process: m.process, detail: m.detail || null };
      if (gid === state.selectedId) patchStatus();
      const el = $(`[data-game="${gid}"]`); if (el) el.dataset.status = m.process;
      if (hooks.onStatus) hooks.onStatus(m.process);
    });
    root.Bridge.on('focusGame', (m) => select(m.gameId, { silent: true }));
    root.Bridge.on('log', (m) => { state.log = { time: m.time, text: m.text, level: m.level }; patchLog(); });
    root.Bridge.on('hotkey', (m) => { if (!m.gameId || m.gameId === state.selectedId) flash(m.id); });
    root.Bridge.on('update', (m) => {
      const r = m.release || {};
      state.update = { state: 'available', version: r.version, notes: r.notes, url: r.url, size: r.size };
      onUpdateStatus({ state: 'available', version: r.version });
      if (state.modal === 'settings') renderModal();
      renderToast();
    });
    root.Bridge.on('updateStatus', (m) => onUpdateStatus(typeof m.state === 'object' ? m.state : m));
    root.Bridge.on('settings', (m) => {
      state.settings = m.settings; state.catalog = m.catalog; state.dataDir = m.dataDir || '';
      if (m.settings && m.settings.language && m.settings.language !== root.I18N.lang) { root.I18N.lang = m.settings.language; relabel(); }
      if (state.modal) renderModal();
    });
  }

  // ---------- dev/screenshot params (ignored in the real app) ----------
  //  ?game=id &status=notfound &tab=hotkeys|notes &settings=1 &query=x &cat=survival &flash=id &hover=cheat:id &capture=inf_battery &xp_value=1234 &many=1200 &lang=en
  function applyParams() {
    if (PROD) return;
    const p = new URLSearchParams(location.search);
    if ([...p.keys()].length) document.documentElement.classList.add('shot');
    if (p.get('lang')) { root.I18N.lang = p.get('lang'); relabel(); }
    if (p.get('game')) select(p.get('game'));
    const after = () => {
      if (p.get('status')) root.Bridge._receive({ type: 'status', gameId: state.selectedId, process: p.get('status'), detail: p.get('detail') || null });
      if (p.get('query')) { const s = $('[data-role=search]'); s.value = p.get('query'); state.query = s.value; renderLibrary(); }
      if (p.get('cat')) { state.category = p.get('cat'); renderCategories(); renderLibrary(); }
      cheatList().forEach((c) => { if (p.has(c.id)) root.Bridge._receive({ type: 'state', gameId: state.selectedId, cheats: [{ id: c.id, value: Number(p.get(c.id)), hint: null, enabled: c.type === 'toggle' ? true : undefined }] }); });
      if (p.get('error')) { const [id, ...msg] = p.get('error').split(':'); root.Bridge._receive({ type: 'state', gameId: state.selectedId, cheats: [{ id, enabled: false, error: msg.join(':') }] }); }
      if (p.get('tab')) { state.tab = p.get('tab'); renderTab(); }
      if (p.get('capture')) { state.capture = p.get('capture'); renderTab(); }
      if (p.get('settings')) openSettings();
      (p.get('hover') || '').split(',').filter(Boolean).forEach((h) => { const [k, v] = h.split(':'); const el = k === 'cheat' ? $(`[data-cheat="${v}"]`) : k === 'game' ? $(`[data-game="${v}"]`) : $(h); if (el) el.classList.add('is-hover'); });
      if (p.get('flash')) p.get('flash').split(',').forEach((id) => { const el = $(`[data-cheat="${id}"]`); if (el) el.classList.add('is-flash', 'is-flash-static'); });
      if (p.get('log')) { state.log = { time: '23:44', text: p.get('log'), level: p.get('loglevel') || 'info' }; patchLog(); }
      document.documentElement.classList.add('params-done');
    };
    setTimeout(after, 400);
  }

  root.TrainerCore = {
    mount({ root: el, templates, onGameChange, onStatus }) {
      R = el; T = templates; hooks = { onGameChange, onStatus };
      if (PROD) document.documentElement.classList.add('is-production');
      document.title = root.Brand.name;
      root.I18N.apply(R);
      renderCategories(); renderLibrary(); renderGame(); patchFooter(); patchLog(); bind();
      R.dataset.transport = root.Bridge.transport;
      root.Bridge.send({ type: 'ready' });
      applyParams();
      document.documentElement.classList.add('is-ready');
    },
    util: { esc, fmt, STATUS, artUrl, keyName, comboOf },
    state, toggle, flash, setValue, select, production: PROD,
  };
})(window);
