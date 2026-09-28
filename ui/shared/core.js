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
    status: {}, cheats: {}, dismissedUpdates: new Set(), log: D.log, settings: null, catalog: null, dataDir: '', capture: null, modal: null,
    account: { configured: false, loggedIn: false, user: null, busy: false, error: null, pending: 0 }, community: {}, report: null, confirmDelete: false,
    requests: null, update: null,
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
  // Steam header image for any app id (game requests): through the host's art cache in the app (CSP allows no CDN),
  // straight from the CDN in the browser preview.
  function headerArt(appid, fallback) {
    if (!(appid > 0)) return null;
    return PROD ? `https://vanta.example/art/${appid}/header` : (fallback || `https://cdn.cloudflare.steamstatic.com/steam/apps/${appid}/header.jpg`);
  }
  const inCatalog = (appid) => appid > 0 && D.games.some((g) => Number(g.steamAppId) === Number(appid));
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
      panel.innerHTML = g.lazy ? T.loading() : T.notes(g, cheatList(), (state.community[g.id] || {}).cheats || null);
      if (!g.lazy) requestCommunity(g.id);
    }
  }

  // ---------- patching ----------
  function patchCheat(c) {
    const el = $(`[data-cheat="${c.id}"]`); if (!el) return;
    const isVal = c.type === 'number' || c.type === 'slider';
    const st = c.error ? 'error' : isVal && c.hint ? 'hint' : c.type === 'toggle' ? (c.enabled ? 'on' : 'off') : 'value';
    el.dataset.state = st;
    el.classList.toggle('is-pending', !!c._pending);
    const broken = isBroken(c);
    el.dataset.conf = c.confidence || 'untested';
    el.toggleAttribute('data-local', !!c.localStatus);
    el.toggleAttribute('data-broken', broken);
    const dis = !interactive() || (isVal && (!!c.hint && c.value == null)) || broken;
    el.toggleAttribute('data-disabled', dis);
    const sw = $('[role=switch]', el);
    if (sw) { sw.setAttribute('aria-checked', String(!!c.enabled)); sw.disabled = dis; }
    const run = $('[data-action=run]', el); if (run) run.disabled = dis;
    const tr = $('[data-action=try-anyway]', el); if (tr) tr.disabled = !interactive();
    const lbl = $('[data-bind=state-label]', el);
    if (lbl) lbl.textContent = c.error ? t('row.err') : c.enabled ? t('row.on') : t('row.off');
    const err = $('[data-bind=error]', el);
    if (err) { err.textContent = c.error || ''; err.hidden = !c.error; }
    el.title = c.error ? `${c.name}: ${c.error}` : broken ? t('row.broken', { n: c.name }) : (c.note || '');
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

  // ---------- test status (local override of game.json confidence) ----------
  const STATUS_CONF = { works: 'confirmed', broken: 'broken', untested: 'untested' };
  const baseConf = (c) => c.baseConfidence || (c.baseConfidence = c.confidence || 'untested');
  // broken = greyed + locked, unless it is already on (so it can be switched off) or "toch proberen" was chosen
  function isBroken(c) { return c.confidence === 'broken' && !c.enabled && !c._tryAnyway; }
  function noteFor(conf) { return conf === 'confirmed' ? null : conf === 'broken' ? t('conf.warn.broken') : conf === 'experimental' ? t('conf.warn.experimental') : t('conf.warn.untested'); }
  function setStatus(id, status /* works|broken|untested|null */, opts) {
    const c = cheat(id); if (!c) return;
    const base = baseConf(c);
    c.localStatus = status || null;
    c.confidence = status ? STATUS_CONF[status] : base;
    c.note = noteFor(c.confidence);
    if (c.confidence !== 'broken') c._tryAnyway = false;
    root.Bridge.send({ type: 'setStatus', gameId: state.selectedId, id, status: status || null });
    state.log = { time: '', text: t('ctx.saved', { n: c.name, s: t('ctx.' + (status || 'default')) }), level: 'info' }; patchLog();
    renderTab();
    if (!(opts && opts.local)) shareStatus(id, status);
  }

  // ---------- community reports (optional Discord account) ----------
  // Logged in: "Werkt niet" opens the report dialog (optional note), "Werkt" is sent right away,
  // "Niet getest" / "Standaard" withdraws a previous report. Not logged in: local status only.
  function shareStatus(id, status) {
    const a = state.account; if (!a.configured || !a.loggedIn) return;
    if (status === 'broken') return openReport(id, 'broken');
    if (status === 'works') return sendReport(id, 'works', null);
    root.Bridge.send({ type: 'withdraw', gameId: state.selectedId, id });
  }
  function openReport(id, status) {
    const c = cheat(id); if (!c) return;
    state.report = { id, gameId: state.selectedId, status: status || (c.localStatus === 'works' ? 'works' : 'broken'), note: '', sending: false, error: null };
    state.modal = 'report'; renderModal();
    if (state.account.configured) requestCommunity(state.selectedId);
  }
  function sendReport(id, status, note) {
    const r = state.report && state.report.id === id ? state.report : null;
    if (r) { r.sending = true; r.error = null; renderModal(); }
    root.Bridge.send({ type: 'report', gameId: (r && r.gameId) || state.selectedId, id, status, note: note || null }).then((ack) => {
      if (ack && ack.ok === false && state.report === r && r) { r.sending = false; r.error = ack.error || 'failed'; renderModal(); }
    });
  }
  function sendReportFromModal() {
    const r = state.report; if (!r || r.sending) return;
    const c = cheat(r.id);
    const note = (($('[data-role=report-note]') || {}).value || '').trim().slice(0, 300);
    r.note = note;
    if (c && c.localStatus !== r.status) setStatus(r.id, r.status, { local: true });   // keep the local mark in line with what is reported
    sendReport(r.id, r.status, r.status === 'broken' ? note : null);
  }
  const communityAsked = {};
  function requestCommunity(gid, force) {
    if (!state.account.configured || !gid) return;
    const now = Date.now();
    if (!force && communityAsked[gid] && now - communityAsked[gid] < 60000) return;
    communityAsked[gid] = now;
    root.Bridge.send({ type: 'getCommunity', gameId: gid, force: !!force });
  }
  const accErrText = (code) => code ? (root.I18N.has('acc.err.' + code) ? t('acc.err.' + code) : t('acc.err', { c: code })) : '';
  function patchAccount() {
    if (state.modal === 'settings') {
      const slot = $('[data-slot=account]'); if (slot && T.account) slot.innerHTML = T.account(state.account, state.confirmDelete);
    } else if (state.modal === 'report') renderModal();
  }
  function onReportResult(m) {
    const map = state.cheats[m.gameId] || {}, c = map[m.id], name = c ? c.name : m.id;
    if (m.community) {
      const e = state.community[m.gameId] || (state.community[m.gameId] = { fingerprint: m.fingerprint, cheats: {} });
      if (!e.cheats) e.cheats = {};
      e.cheats[m.id] = m.community;
    } else if (m.ok && m.status == null && state.community[m.gameId]) communityAsked[m.gameId] = 0;
    const r = state.report;
    const text = m.ok ? (m.status == null ? t('rep.withdrawn', { n: name }) : t('rep.sent', { n: name })) : m.queued ? t('rep.queued', { n: name }) : t('rep.failed', { e: accErrText(m.error) });
    if (r && r.id === m.id && r.gameId === m.gameId && m.status != null) {
      if (m.ok || m.queued) { state.report = null; closeModal(); }
      else { r.sending = false; r.error = m.error || 'failed'; renderModal(); }
    }
    state.log = { time: '', text, level: m.ok ? 'info' : m.queued ? 'warn' : 'error' }; patchLog();
    if (m.gameId === state.selectedId && state.tab === 'notes') renderTab();
    if (m.error === 'not_logged_in' || m.error === 'session_expired') { state.account.loggedIn = false; patchAccount(); }
  }
  let ctxFor = null;
  function openContextMenu(id, x, y) {
    const c = cheat(id); if (!c || !T.contextMenu) return;
    closeContextMenu();
    ctxFor = id;
    const host = document.createElement('div');
    host.className = 'ctx-slot'; host.innerHTML = T.contextMenu(Object.assign({ baseConfidence: baseConf(c), canReport: !!state.account.configured }, c));
    R.appendChild(host);
    const m = host.firstElementChild, r = m.getBoundingClientRect();
    const vw = window.innerWidth, vh = window.innerHeight;
    m.style.left = Math.max(8, Math.min(x, vw - r.width - 8)) + 'px';
    m.style.top = Math.max(8, Math.min(y, vh - r.height - 8)) + 'px';
    $$(`[data-cheat]`).forEach((e) => e.classList.toggle('is-ctx', e.dataset.cheat === id));
    const first = $('.ctx-item.is-current', m) || $('.ctx-item', m); if (first) first.focus({ preventScroll: true });
  }
  function closeContextMenu() {
    if (!ctxFor) return;
    ctxFor = null;
    $$('.ctx-slot').forEach((e) => e.remove());
    $$('.is-ctx').forEach((e) => e.classList.remove('is-ctx'));
  }
  function exportStatus() {
    const el = $('[data-bind=export-status]');
    if (el) { el.hidden = false; el.textContent = t('set.status.busy'); }
    root.Bridge.send({ type: 'exportStatus' });
  }
  function copyText(text) {
    const fallback = () => {
      try {
        const ta = document.createElement('textarea');
        ta.value = text; ta.setAttribute('readonly', ''); ta.style.position = 'fixed'; ta.style.opacity = '0';
        document.body.appendChild(ta); ta.select();
        const ok = document.execCommand('copy'); ta.remove(); return ok;
      } catch (_) { return false; }
    };
    try {
      if (navigator.clipboard && navigator.clipboard.writeText) return navigator.clipboard.writeText(text).then(() => true, () => fallback());
    } catch (_) { /* fall through */ }
    return Promise.resolve(fallback());
  }
  function onStatusExport(m) {
    copyText(m.json || '').then((ok) => {
      const msg = ok ? t('set.status.copied', { p: m.path || '' }) : t('set.status.saved', { p: m.path || '' });
      const el = $('[data-bind=export-status]'); if (el) { el.hidden = false; el.textContent = msg; el.title = m.path || ''; el.dataset.copied = String(!!ok); }
      state.lastExport = { json: m.json, path: m.path, copied: !!ok };
      state.log = { time: '', text: msg, level: 'info' }; patchLog();
    });
  }

  // ---------- actions ----------
  function toggle(id, force, opts) {
    const c = cheat(id); if (!c || c.type !== 'toggle' || !interactive()) return;
    const enabled = force ?? !(c.enabled && !c.error);
    const tryAnyway = !!(opts && opts.tryAnyway);
    if (enabled && c.confidence === 'broken' && !tryAnyway && !c._tryAnyway) {
      state.log = { time: '', text: t('row.broken', { n: c.name }), level: 'error' }; patchLog(); flash(id);
      return;
    }
    if (tryAnyway) c._tryAnyway = true;
    c.enabled = enabled; c.error = null; c._pending = true;
    patchCheat(c); patchCounts();
    const msg = { type: 'toggle', gameId: state.selectedId, id, enabled };
    if (enabled && c.confidence === 'broken') msg.force = true;
    root.Bridge.send(msg).then(() => { c._pending = false; if (!c.enabled && c.confidence === 'broken') c._tryAnyway = false; patchCheat(c); });
  }
  function tryAnyway(id) {
    const c = cheat(id); if (!c || !interactive()) return;
    if (c.type === 'toggle') return toggle(id, true, { tryAnyway: true });
    c._tryAnyway = true; patchCheat(c);
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
    if (state.modal === 'whatsnew' && T.whatsNew) {
      const u = state.update;
      if (!u) { state.modal = null; slot.innerHTML = ''; slot.hidden = true; return; }
      const body = $('[data-role=notes]', slot), top = body ? body.scrollTop : 0;
      slot.innerHTML = T.whatsNew(u, root.VantaMd ? root.VantaMd.render(root.VantaMd.pick(u.notes || '', root.I18N.lang)) : esc(u.notes || ''));
      const nb = $('[data-role=notes]', slot);
      if (body) nb.scrollTop = top; else { nb.focus({ preventScroll: true }); }
      return;
    }
    if (state.modal === 'requests' && T.requests) return renderRequests(slot);
    if (state.modal === 'report' && T.report) {
      const r = state.report, c = r && (state.cheats[r.gameId] || {})[r.id];
      if (!c) { state.modal = null; slot.innerHTML = ''; slot.hidden = true; return; }
      const prevNote = $('[data-role=report-note]', slot);
      if (prevNote) r.note = prevNote.value;
      const g = D.games.find((x) => x.id === r.gameId);
      slot.innerHTML = T.report(c, r, state.account, ((state.community[r.gameId] || {}).cheats || {})[r.id], g);
      const f = $('[data-role=report-note]', slot) || $('[data-action=report-send]', slot) || $('.acct-login', slot);
      if (f) { f.focus(); if (f.setSelectionRange && f.value) f.setSelectionRange(f.value.length, f.value.length); }
      return;
    }
    slot.innerHTML = T.settings(state.settings || { language: root.I18N.lang, catalogDir: '', catalogUrl: '', autoAttach: true }, { catalog: state.catalog, dataDir: state.dataDir, version: D.app.version, update: state.updateCheck, account: state.account, confirmDelete: state.confirmDelete });
    const first = $('[data-set=language]', slot); if (first) first.focus();
  }
  function closeModal() {
    const back = state.modalOpener; state.modalOpener = null;
    if (state.modal === 'requests' && state.requests) { clearTimeout(state.requests.timer); state.requests.search = null; state.requests.term = ''; }
    state.modal = null; state.report = null; state.confirmDelete = false; renderModal();
    if (back && back.isConnected && back.focus) back.focus({ preventScroll: true });   // e.g. back to the toast's "What's new"
  }
  function openModal(name, opener) { state.modalOpener = opener || null; state.modal = name; renderModal(); }

  // ---------- game requests ----------
  function openRequests(opener) {
    if (!state.requests) state.requests = { items: null, error: null, loggedIn: false, term: '', search: null, busy: new Set(), timer: 0 };
    state.requests.error = null;
    openModal('requests', opener);
    root.Bridge.send({ type: 'getRequests' });
  }
  function renderRequests(slot) {
    const r = state.requests, a = state.account;
    if (!$('.modal-requests', slot)) {
      slot.innerHTML = T.requests(r, a);
      const inp = $('[data-role=req-search]', slot); if (inp) inp.focus();
      return;
    }
    const patch = (bind, html) => { const el = $(`[data-bind=${bind}]`, slot); if (el && el._html !== html) { el.innerHTML = html; el._html = html; } };
    patch('req-results', T.reqResults(r, a));
    patch('req-login', T.reqLogin(r, a));
    patch('req-list', T.reqList(r, a));
  }
  function patchRequests() { if (state.modal === 'requests') renderModal(); }
  function onReqSearchInput(v) {
    const r = state.requests; if (!r) return;
    r.term = v;
    clearTimeout(r.timer);
    const term = v.trim();
    if (term.length < 2 && !/^\d+$/.test(term)) { r.search = null; return patchRequests(); }
    r.search = { term, items: (r.search && r.search.items) || [], loading: true, error: null };
    patchRequests();
    r.timer = setTimeout(() => root.Bridge.send({ type: 'steamSearch', term }), 350);
  }
  function voteRequest(btn) {
    const r = state.requests; if (!r) return;
    if (!state.account.loggedIn && !r.loggedIn) {
      if (state.account.configured) { state.account = Object.assign({}, state.account, { busy: true, error: null }); patchAccount(); patchRequests(); root.Bridge.send({ type: 'accountLogin' }); }
      return;
    }
    const appid = Number(btn.dataset.appid), unvote = btn.getAttribute('aria-pressed') === 'true';
    if (!(appid > 0) || r.busy.has(appid)) return;
    r.busy.add(appid);
    // optimistic: flip the vote locally; the host answers with requestVoteResult and a fresh list
    const known = (r.items || []).find((x) => x.appid === appid);
    if (known) { known.voted = !unvote; known.votes = Math.max(0, (known.votes || 0) + (unvote ? -1 : 1)); }
    else if (!unvote && r.items) r.items.push({ appid, name: btn.dataset.name, cover: btn.dataset.cover || null, status: 'open', votes: 1, voted: true, _local: true });
    patchRequests();
    root.Bridge.send(unvote ? { type: 'requestUnvote', appid } : { type: 'requestVote', appid, name: btn.dataset.name, cover: btn.dataset.cover || null });
  }
  function onRequests(m) {
    if (!state.requests) return;
    const r = state.requests;
    if (m.ok) { r.items = (m.items || []).filter((x) => x && x.appid > 0); r.error = null; r.loggedIn = !!m.loggedIn; }
    else { r.error = m.error || 'failed'; if (!r.items) r.items = null; }
    patchRequests();
  }
  function onSteamSearch(m) {
    const r = state.requests; if (!r || !r.search || m.term !== r.search.term) return;   // stale answer
    r.search = { term: m.term, items: m.ok ? (m.items || []).filter((x) => x && x.appid > 0) : [], loading: false, error: m.ok ? null : (m.error || 'steam_error') };
    patchRequests();
  }
  function onRequestVoteResult(m) {
    const r = state.requests; if (!r) return;
    r.busy.delete(m.appid);
    const x = (r.items || []).find((i) => i.appid === m.appid);
    if (m.ok) {
      if (m.request && x) Object.assign(x, m.request, { _local: false });
      state.log = { time: '', text: t(m.unvote ? 'req.removed' : 'req.sent', { n: (m.request && m.request.name) || (x && x.name) || m.appid }), level: 'info' }; patchLog();
    } else {
      if (x) { if (x._local) r.items = r.items.filter((i) => i !== x); else { x.voted = !!m.unvote; x.votes = Math.max(0, (x.votes || 0) + (m.unvote ? 1 : -1)); } }
      state.log = { time: '', text: (root.I18N.has('req.err.' + m.error) ? t('req.err.' + m.error) : accErrText(m.error)), level: 'warn' }; patchLog();
      if (m.error === 'session_expired' || m.error === 'not_logged_in') r.loggedIn = false;
    }
    patchRequests();
  }
  function saveSettingsFromModal() {
    const slot = $('[data-slot=modal]');
    const s = {
      language: $('[data-set=language]', slot).value,
      catalogDir: $('[data-set=catalogDir]', slot).value.trim(),
      catalogUrl: $('[data-set=catalogUrl]', slot).value.trim(),
      autoAttach: $('[data-set=autoAttach]', slot).checked,
    };
    const su = $('[data-set=shareUsage]', slot); if (su) s.shareUsage = su.checked;
    const langChanged = s.language !== root.I18N.lang;
    state.settings = Object.assign({}, state.settings, s);
    root.Bridge.send({ type: 'saveSettings', settings: s });
    closeModal();
    if (langChanged) { root.I18N.lang = s.language; relabel(); }
    state.log = { time: state.log.time, text: t('set.saved'), level: 'info' }; patchLog();
  }

  // ---------- updates ----------
  // The toast element is created once per appearance: its enter animation must not restart on every progress message
  // (that caused the flicker/jumping while downloading). A state change swaps only the children; progress updates only
  // the bar transform and the percentage text.
  const toastKey = (u) => [u.state, u.version, u.message || '', u.url || ''].join('|');   // notes live in the "What's new" modal, not in the toast
  function renderToast() {
    const slot = $('[data-slot=toast]'); if (!slot || !T.updateToast) return;
    const u = state.update;
    let el = slot.firstElementChild;
    if (!u) { slot.innerHTML = ''; slot.hidden = true; return; }
    slot.hidden = false;
    if (!el || !el.classList.contains('toast')) {
      slot.innerHTML = T.updateToast(u);
      el = slot.firstElementChild; el._key = toastKey(u);
      el.addEventListener('animationend', () => el.classList.add('is-in'), { once: true });
      return;
    }
    const key = toastKey(u);
    if (el._key !== key) {
      el._key = key; el.dataset.state = u.state;
      el.innerHTML = T.updateToastInner ? T.updateToastInner(u) : T.updateToast(u);
      return;
    }
    patchToastProgress(el, u);
  }
  function patchToastProgress(el, u) {
    const p = u.state === 'installing' ? 1 : clamp(Number(u.progress) || 0, 0, 1);
    const bar = $('[data-bind=toast-bar]', el); if (bar) bar.style.transform = `scaleX(${p})`;
    const pct = $('[data-bind=toast-pct]', el); if (pct) { const txt = Math.round(p * 100) + '%'; if (pct.textContent !== txt) pct.textContent = txt; }
  }
  function dismissUpdate() {
    const v = state.update && state.update.version;
    if (v) state.dismissedUpdates.add(v);
    return v;
  }
  function onUpdateStatus(s) {
    state.updateCheck = s;
    if (state.modal === 'settings') {
      const el = $('[data-bind=update-status]'); if (el && T.updLine) el.textContent = T.updLine(s);
      const b = $('[data-action=check-update]'); if (b) b.disabled = s.state === 'checking';
    }
    if (['downloading', 'installing', 'pending', 'updated', 'failed', 'error'].includes(s.state) && (state.update || ['updated', 'failed', 'pending'].includes(s.state))) {
      if (!state.update && s.state === 'pending' && s.version && state.dismissedUpdates.has(s.version)) return;   // already postponed this session
      state.update = Object.assign({}, state.update || {}, s);
      renderToast();
      if (state.modal === 'whatsnew' && s.state !== 'downloading') renderModal();
    }
  }

  // ---------- events ----------
  function bind() {
    R.addEventListener('click', (e) => {
      const act = e.target.closest('[data-action]');
      const a = act && act.dataset.action;
      if (ctxFor) {
        const id = ctxFor;
        if (a === 'set-status') { closeContextMenu(); return setStatus(id, act.dataset.status === 'default' ? null : act.dataset.status); }
        if (a === 'report-open') { closeContextMenu(); return openReport(id); }
        closeContextMenu();
        return;                                                  // a click outside the menu only closes it
      }
      if (a === 'try-anyway') { e.stopPropagation(); return tryAnyway(act.closest('[data-cheat]').dataset.cheat); }
      if (a === 'export-status') return exportStatus();
      if (a === 'select-game') return select(act.dataset.game);
      if (a === 'primary') return primary(act);
      if (a === 'tab') { state.tab = act.dataset.value; if (state.capture) setCapture(null); return renderTab(); }
      if (a === 'open-hotkeys') { state.tab = 'hotkeys'; return renderTab(); }
      if (a === 'open-settings') return openSettings();
      if (a === 'open-requests') return openRequests(act);
      if (a === 'whats-new') return openModal('whatsnew', act);
      if (a === 'req-vote') return voteRequest(act);
      if (a === 'modal-close') return closeModal();
      if (a === 'account-login') { state.account = Object.assign({}, state.account, { busy: true, error: null }); patchAccount(); return root.Bridge.send({ type: 'accountLogin' }); }
      if (a === 'account-cancel') return root.Bridge.send({ type: 'accountCancel' });
      if (a === 'account-logout') return root.Bridge.send({ type: 'accountLogout' });
      if (a === 'account-delete') { state.confirmDelete = true; return patchAccount(); }
      if (a === 'account-delete-cancel') { state.confirmDelete = false; return patchAccount(); }
      if (a === 'account-delete-confirm') { state.confirmDelete = false; state.account = Object.assign({}, state.account, { busy: true }); patchAccount(); return root.Bridge.send({ type: 'accountDelete' }); }
      if (a === 'report-pick' && state.report) {
        state.report.status = act.dataset.status;
        $$('[data-action=report-pick]').forEach((b) => b.setAttribute('aria-checked', String(b.dataset.status === state.report.status)));
        const nf = $('[data-bind=rep-note-field]'); if (nf) nf.hidden = state.report.status !== 'broken';
        return;
      }
      if (a === 'report-send') return sendReportFromModal();
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
      if (a === 'open-url') { e.preventDefault(); return root.Bridge.send({ type: 'openUrl', url: act.dataset.url }); }
      if (a === 'check-update') { onUpdateStatus({ state: 'checking' }); return root.Bridge.send({ type: 'checkUpdate' }); }
      if (a === 'update-now') { if (state.modal === 'whatsnew') { state.modalOpener = null; closeModal(); } if (!state.update || state.update.state !== 'available') return; state.update = Object.assign({}, state.update, { state: 'downloading', progress: 0 }); renderToast(); return root.Bridge.send({ type: 'updateNow' }); }
      if (a === 'update-later') { const v = dismissUpdate(); state.update = null; renderToast(); return root.Bridge.send({ type: 'updateLater', version: v }); }
      if (a === 'update-close') { const v = dismissUpdate(); state.update = null; renderToast(); if (v) root.Bridge.send({ type: 'updateDismiss', version: v }); return; }
      if (a === 'step') return;
      const card = e.target.closest('[data-cheat][data-type=toggle]');
      if (card && !card.hasAttribute('data-disabled') && (!act || a === 'toggle')) toggle(card.dataset.cheat);
    });
    R.addEventListener('contextmenu', (e) => {
      const row = e.target.closest('[data-cheat], [data-cheat-note]');
      if (!row || e.target.closest('input')) { if (ctxFor) closeContextMenu(); return; }
      e.preventDefault();
      const id = row.dataset.cheat || row.dataset.cheatNote;
      let x = e.clientX, y = e.clientY;
      if (!x && !y) { const r = row.getBoundingClientRect(); x = r.left + 48; y = r.bottom - 4; }   // keyboard (Shift+F10 / menu key)
      openContextMenu(id, x, y);
    });
    window.addEventListener('blur', closeContextMenu);
    window.addEventListener('resize', closeContextMenu);
    R.addEventListener('scroll', closeContextMenu, true);
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
      if (e.target.matches && e.target.matches('[data-role=report-note]') && e.key === 'Enter' && (e.ctrlKey || e.metaKey)) { e.preventDefault(); return sendReportFromModal(); }
      const inp = e.target.closest && e.target.closest('[data-role=value-input]');
      if (inp && e.key === 'Enter') inp.blur();
      if (inp && e.key === 'Escape') { const c = cheat(inp.closest('[data-cheat]').dataset.cheat); inp.value = fmt(c.value); inp.blur(); }
      if (inp && (e.key === 'ArrowUp' || e.key === 'ArrowDown')) { e.preventDefault(); const c = cheat(inp.closest('[data-cheat]').dataset.cheat); setValue(c.id, c.value + (e.key === 'ArrowUp' ? 1 : -1) * (c.step || 1)); inp.value = fmt(c.value); }
    });
    R.addEventListener('input', (e) => {
      const tg = e.target;
      if (tg.matches('[data-role=search]')) { state.query = tg.value; renderLibrary(); }
      if (tg.matches('[data-role=req-search]')) onReqSearchInput(tg.value);
      if (tg.matches('[data-role=report-note]')) { const n = $('[data-bind=rep-count]'); if (n) n.textContent = String(tg.value.length); if (state.report) state.report.note = tg.value; }
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
      if (ctxFor) {
        if (e.key === 'Escape') { e.preventDefault(); const id = ctxFor; closeContextMenu(); const row = $(`[data-cheat="${id}"]`); if (row) row.focus && row.focus(); return; }
        if (e.key === 'ArrowDown' || e.key === 'ArrowUp') {
          e.preventDefault();
          const items = $$('.ctx-menu .ctx-item'), i = items.indexOf(document.activeElement);
          const n = items[(i + (e.key === 'ArrowDown' ? 1 : items.length - 1)) % items.length]; if (n) n.focus();
          return;
        }
        if (e.key === 'Tab') { closeContextMenu(); }
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
      if (g.id === state.selectedId) closeContextMenu();
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
      if (!m.manual && r.version && state.dismissedUpdates.has(r.version)) return;              // "Later" = not again this session
      if (state.update && state.update.version === r.version && state.update.state !== 'available' && !m.manual) return;   // already downloading/installing
      state.update = { state: 'available', version: r.version, notes: r.notes, url: r.url, size: r.size };
      onUpdateStatus({ state: 'available', version: r.version });
      if (state.modal === 'settings' || state.modal === 'whatsnew') renderModal();
      renderToast();
    });
    root.Bridge.on('statusExport', onStatusExport);
    root.Bridge.on('account', (m) => {
      const was = state.account;
      state.account = { configured: !!m.configured, loggedIn: !!m.loggedIn, user: m.user || null, busy: !!m.busy, error: m.error || null, pending: m.pending || 0, shareUsage: !!m.shareUsage, privacyUrl: /^https:\/\//.test(m.privacyUrl || '') ? m.privacyUrl : '' };
      if (!was.loggedIn && state.account.loggedIn && was.configured) { state.log = { time: '', text: t('acc.loggedin', { n: (m.user || {}).username || '' }), level: 'info' }; patchLog(); }
      else if (was.loggedIn && !state.account.loggedIn && !m.error) { state.log = { time: '', text: t('acc.loggedout'), level: 'info' }; patchLog(); }
      else if (m.error && m.error !== was.error) { state.log = { time: '', text: accErrText(m.error), level: 'warn' }; patchLog(); }
      patchAccount();
      if (state.account.configured && !was.configured && state.tab === 'notes') requestCommunity(state.selectedId);
      if (state.modal === 'requests') { if (was.loggedIn !== state.account.loggedIn) root.Bridge.send({ type: 'getRequests' }); if (!state.account.loggedIn) state.requests.loggedIn = false; patchRequests(); }
    });
    root.Bridge.on('accountDeleted', () => { state.community = {}; state.log = { time: '', text: t('acc.deleted'), level: 'info' }; patchLog(); if (state.tab === 'notes') renderTab(); });
    root.Bridge.on('reportResult', onReportResult);
    root.Bridge.on('requests', onRequests);
    root.Bridge.on('steamSearch', onSteamSearch);
    root.Bridge.on('requestVoteResult', onRequestVoteResult);
    root.Bridge.on('community', (m) => {
      if (!m.available) return;
      state.community[m.gameId] = { fingerprint: m.fingerprint, cheats: m.cheats || {} };
      if (m.gameId === state.selectedId && state.tab === 'notes') renderTab();
      if (state.modal === 'report' && state.report && state.report.gameId === m.gameId) renderModal();
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
      if (p.get('update')) root.Bridge._receive({ type: 'update', manual: true, release: { version: p.get('update'), notes: root.VantaMockReleaseNotes || '', url: 'https://github.com/Rick007110/Vanta/releases', size: 71000000 } });
      if (p.get('whatsnew')) openModal('whatsnew');
      if (p.get('requests')) { openRequests(); if (p.get('reqsearch')) setTimeout(() => { const i = $('[data-role=req-search]'); if (i) { i.value = p.get('reqsearch'); onReqSearchInput(i.value); } }, 300); }
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
    util: { esc, fmt, STATUS, artUrl, headerArt, inCatalog, keyName, comboOf },
    state, toggle, flash, setValue, select, setStatus, openContextMenu, closeContextMenu, openReport, production: PROD,
  };
})(window);
