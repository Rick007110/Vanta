/* Vanta UI templates (design: Nocturne variant A). All behaviour lives in shared/core.js. */
(function () {
  'use strict';
  const { esc, fmt } = TrainerCore.util;
  const I = window.Icon;
  const t = window.t;
  document.querySelectorAll('[data-icon]').forEach((el) => (el.outerHTML = I(el.dataset.icon, el.closest('.winbtn') ? 14 : 16)));
  document.querySelectorAll('[data-brand-mark]').forEach((el) => (el.innerHTML = Brand.mark(26)));
  document.querySelectorAll('[data-brand-name]').forEach((el) => (el.textContent = Brand.name));
  document.querySelectorAll('[data-bind=brand-tag]').forEach((el) => (el.textContent = 'v' + String(TrainerData.app.version).replace(/\.0$/, '')));

  const keys = (hk) => esc(hk).replace(/\+(?!$)/g, '<i>+</i>');
  const chip = (hk, title) => hk ? `<button class="kbd" type="button" data-action="hotkey-chip" title="${esc(title || t('row.hk', { k: hk }))}">${keys(hk)}</button>` : '';
  const img = (src, cls) => src ? `<img class="${cls}" src="${esc(src)}" alt="" loading="lazy" decoding="async" referrerpolicy="no-referrer" onload="vantaArt.ok(this)" onerror="vantaArt.fail(this)">` : '';
  const updLine = (u) => !u ? '' : ({ checking: t('upd.checking'), uptodate: t('upd.uptodate'), available: t('upd.available', { v: u.version }), offline: t('upd.offline'),
    ratelimited: t('upd.ratelimited'), norelease: t('upd.norelease'), pending: t('upd.pending', { v: u.version }), downloading: t('upd.downloading', { v: u.version }), error: t('upd.error') + (u.message ? ': ' + u.message : '') }[u.state] || '');
  const storeChip = (g) => {
    const ins = g.install || {}, p = ins.primary;
    if (p) return `<span class="meta-store" data-bind="store" data-store="${esc(p.store)}" title="${esc(p.dir || '')}"><span class="meta-k">${esc(t('meta.store'))}</span><span class="meta-v">${esc(p.storeName)}</span></span>`;
    const o = (ins.owned || [])[0];
    return o ? `<span class="meta-store is-owned" data-bind="store" data-store="${esc(o.store)}" title="${esc(o.note || '')}"><span class="meta-k">${esc(o.storeName)}</span><span class="meta-v">${esc(t('meta.ownedOnly'))}</span></span>` : '';
  };
  const conf = (c) => c.confidence && c.confidence !== 'confirmed' ? '' : `<span class="conf conf-ok${c.localStatus === 'works' ? ' conf-local' : ''}"${c.localStatus === 'works' ? ` title="${esc(t('conf.local.works'))}"` : ''}>${I('check', 11)}</span>`;
  const tryBtn = () => `<button class="try" type="button" data-action="try-anyway" title="${esc(t('row.try.title'))}">${I('flask', 12)}${esc(t('row.try'))}</button>`;
  const swatch = (g) => { const a = Art.artOf(g); return `background:linear-gradient(160deg, ${a.sky[1]}, ${a.sky[0]} 60%, ${a.ground})`; };

  // "12 gebruikers melden: werkt niet" (community counts for this game version)
  const commLine = (e) => {
    if (!e) return '';
    if (e.status === 'fixed') return e.fixedInVersion ? t('comm.fixed', { v: e.fixedInVersion }) : t('comm.fixed0');
    if (e.status === 'cant_reproduce' && !e.broken) return t('comm.cnr');
    const b = e.broken || 0, w = e.works || 0;
    if (!b && !w) return '';
    const bt = b === 1 ? t('comm.broken1') : t('comm.broken', { n: b }), wt = w === 1 ? t('comm.works1') : t('comm.works', { n: w });
    return b >= w ? bt + (w ? ` · ${w}× ${t('rep.works').toLowerCase()}` : '') : wt + (b ? ` · ${b}× ${t('rep.broken').toLowerCase()}` : '');
  };
  const accErr = (code) => code ? (window.I18N.has && window.I18N.has('acc.err.' + code) ? t('acc.err.' + code) : t('acc.err', { c: code })) : '';

  const T = {
    libraryGroup: (g, items, n) => `
      <div class="lib-group"><div class="lib-head"><span>${esc(g.title)}</span><span class="lib-count">${n}</span></div>
      <ul class="lib-list" role="list">${items}</ul></div>`,
    libraryItem: (g, { selected, cover }) => `
      <li><button class="lib-item${selected ? ' is-selected' : ''}${g.antiCheat || g.onlineOnly ? ' is-blocked' : ''}" data-action="select-game" data-game="${esc(g.id)}" aria-current="${selected}">
        <span class="lib-cover" style="${swatch(g)}"><span class="lib-initials">${esc(g.short || '')}</span>${img(cover, 'lib-img')}</span>
        <span class="lib-text"><span class="lib-name">${esc(g.name)}</span>
          <span class="lib-meta"><span class="lib-dot" aria-hidden="true"></span>${g.antiCheat || g.onlineOnly ? `<span class="lib-ban">${I('ban', 11)}</span>` : ''}<span class="lib-ver">${esc(g.badge || '')}</span>${g.badge ? '<span class="lib-sep">·</span>' : ''}${t('lib.cheats', { n: g.cheatCount || 0 })}</span></span>
      </button></li>`,
    libraryMore: (n) => `<li class="lib-more">${esc(t('lib.more', { n }))}</li>`,
    libraryEmpty: (q) => `
      <div class="lib-empty"><div class="lib-empty-ic">${I('search', 20)}</div>
        <p class="lib-empty-title">${esc(t('lib.empty.title'))}</p>
        <p class="lib-empty-body">${t('lib.empty.body', { q })}</p>
        <button class="btn-ghost sm" data-action="clear-search">${esc(t('lib.empty.clear'))}</button></div>`,
    hero: (g, art) => `
      <div class="hero-art" aria-hidden="true">${Art.hero(g)}${img(art.hero, 'hero-img')}</div>
      <div class="hero-body">
        <div class="hero-cover">${Art.cover(g)}${img(art.cover, 'cover-img')}<span class="hero-cover-title">${esc(g.name)}</span></div>
        <div class="hero-info">
          <div class="hero-eyebrow"><span>${esc((g.categories || []).slice(0, 3).join(' · ') || t('hero.library'))}</span></div>
          <div class="hero-titlewrap">${img(art.logo, 'hero-logo')}<h1 class="hero-title">${esc(g.name)}</h1></div>
          <div class="hero-meta">
            <span class="pill" data-bind="status-pill"><i class="pill-dot"></i><span data-bind="status-label"></span></span>
            ${g.version ? `<span class="meta-ver"><span class="meta-k">${esc(t('meta.version'))}</span><span class="meta-v">${esc(g.version)}</span></span>` : ''}
            ${storeChip(g)}
          </div>
          ${g.scope ? `<p class="hero-scope" data-bind="scope">${I('shield', 13)}<span>${esc(g.scope)}</span></p>` : ''}
        </div>
        <div class="hero-actions">
          <button class="btn" type="button" data-action="primary"><span data-bind="primary-icon"></span><span data-bind="primary-label"></span></button>
          <p class="hero-hint" data-bind="status-hint"></p>
        </div>
      </div>`,
    layout: (parts) => {
      if (parts.length < 2) return `<div class="col">${parts.map((p) => p.html).join('')}</div><div class="col"></div>`;
      const h = parts.map((p) => p.items.length + 1);
      let best = 1, bestMax = Infinity;
      for (let k = 1; k < parts.length; k++) {
        const a = h.slice(0, k).reduce((x, y) => x + y, 0), b = h.slice(k).reduce((x, y) => x + y, 0);
        if (Math.max(a, b) < bestMax) { bestMax = Math.max(a, b); best = k; }
      }
      return [parts.slice(0, best), parts.slice(best)].map((c) => `<div class="col">${c.map((p) => p.html).join('')}</div>`).join('');
    },
    section: (s, rows) => `
      <section class="sec" aria-labelledby="sec-${s.id}">
        <header class="sec-head"><span class="sec-ic">${I(s.icon, 15)}</span><h2 id="sec-${s.id}">${esc(s.title)}</h2>
          <span class="sec-count" data-bind="section-count" data-section="${s.id}"></span></header>
        <div class="sec-rows">${rows}</div>
      </section>`,
    cheat: {
      toggle: (c) => `
        <div class="row" data-cheat="${c.id}" data-type="toggle">
          <span class="row-ic">${I(c.icon, 16)}<span class="row-ic-err">${I('alert', 16)}</span></span>
          <div class="row-main"><span class="row-name" id="n-${c.id}">${esc(c.name)}${conf(c)}</span><span class="row-err" data-bind="error" hidden></span></div>
          <button class="retry" type="button" data-action="retry">${I('refresh', 13)}${esc(t('row.retry'))}</button>
          ${tryBtn()}
          ${chip(c.hotkey)}
          <span class="sr" data-bind="state-label"></span>
          <button class="switch" type="button" role="switch" aria-checked="false" aria-labelledby="n-${c.id}" data-action="toggle"><span class="knob"></span></button>
        </div>`,
      number: (c) => {
        const hk = [c.hotkeyInc, c.hotkeyDec].filter(Boolean).join(' / ');
        return `
        <div class="row" data-cheat="${c.id}" data-type="number">
          <span class="row-ic">${I(c.icon, 16)}</span>
          <div class="row-main"><label class="row-name" for="v-${c.id}">${esc(c.name)}</label><span class="row-sub" data-bind="sub" data-default="${esc(c.sub || `${t('row.step', { s: fmt(c.step) })} · ${hk || t('row.hold')}`)}"></span><span class="row-err" data-bind="error" hidden></span></div>
          ${tryBtn()}
          <div class="stepper">
            <button type="button" class="step" data-action="step" data-dir="-1" aria-label="${esc(t('row.dec', { n: c.name }))}">${I('minus', 14)}</button>
            <input id="v-${c.id}" class="step-val" data-role="value-input" inputmode="decimal" autocomplete="off">
            <button type="button" class="step" data-action="step" data-dir="1" aria-label="${esc(t('row.inc', { n: c.name }))}">${I('plus', 14)}</button>
          </div>
        </div>`;
      },
      slider: (c) => `
        <div class="row" data-cheat="${c.id}" data-type="slider">
          <span class="row-ic">${I(c.icon, 16)}</span>
          <div class="row-main"><label class="row-name" for="s-${c.id}">${esc(c.name)}</label><span class="row-sub" data-bind="sub" data-default="${esc(c.sub || '')}" hidden></span><span class="row-err" data-bind="error" hidden></span></div>
          ${tryBtn()}
          <div class="slider"><input id="s-${c.id}" type="range" data-role="slider" min="${c.min}" max="${c.max}" step="${c.step}">
            <output class="slider-out" data-bind="slider-out" for="s-${c.id}"></output></div>
        </div>`,
      button: (c) => `
        <div class="row" data-cheat="${c.id}" data-type="button">
          <span class="row-ic">${I(c.icon, 16)}</span>
          <div class="row-main"><span class="row-name">${esc(c.name)}</span><span class="row-sub" data-bind="sub" data-default="${esc(c.sub || '')}" hidden></span><span class="row-err" data-bind="error" hidden></span></div>
          ${tryBtn()}
          ${chip(c.hotkey)}
          <button class="btn-ghost run" type="button" data-action="run">${I('zap', 14)}${esc(c.buttonLabel || t('row.run'))}</button>
        </div>`,
    },
    loading: () => `<div class="table-empty is-loading"><div class="te-ic">${I('refresh', 22)}</div><p>${esc(t('loading'))}</p></div>`,
    tableEmpty: (g) => `
      <div class="table-empty"><div class="te-ic">${I('table', 22)}</div>
        <h3>${esc(t('empty.title'))}</h3><p>${esc(t('empty.body', { g: g.name }))}</p></div>`,
    hotkeys: (g, rows, capture) => `
      <div class="pane">
        <div class="pane-head"><div><h3>${esc(t('hk.title', { g: g.name }))}</h3><p>${esc(t('hk.body'))}</p></div></div>
        <div class="hk-list">${rows.map((r) => {
          const cap = capture === r.key, custom = (r.value || '') !== (r.def || '');
          return `<div class="hk-row${cap ? ' is-capture' : ''}" data-hk="${esc(r.key)}">
            <span class="row-ic">${I(r.cheat.icon, 15)}</span>
            <span class="hk-name">${esc(r.cheat.name)}${r.kind ? ` <span class="hk-kind">${esc(t('hk.' + r.kind))}</span>` : ''}</span>
            <span class="hk-val">${cap ? `<span class="hk-press">${esc(t('hk.press'))}</span>` : r.value ? `<span class="kbd static">${keys(r.value)}</span>` : `<span class="hk-none">${esc(t('hk.none'))}</span>`}${custom && !cap ? '<i class="hk-custom" title="aangepast"></i>' : ''}</span>
            <span class="hk-acts">
              <button class="btn-ghost sm" type="button" data-action="hk-change" data-key="${esc(r.key)}">${esc(cap ? t('set.cancel') : t('hk.change'))}</button>
              <button class="btn-ghost sm" type="button" data-action="hk-clear" data-key="${esc(r.key)}"${r.value ? '' : ' disabled'}>${esc(t('hk.clear'))}</button>
              <button class="btn-ghost sm" type="button" data-action="hk-default" data-key="${esc(r.key)}"${custom ? '' : ' disabled'}>${esc(t('hk.default'))}</button>
            </span></div>`;
        }).join('')}</div>
      </div>`,
    notes: (g, list, comm) => `
      <div class="pane">
        <div class="pane-head"><div><h3>${esc(t('notes.title'))}</h3>
          <p>${g.version ? `${esc(t('notes.version'))}: <b>${esc(g.version)}</b>` : ''}</p></div></div>
        ${(g.notes || []).length ? `<ul class="notes">${g.notes.map((n) => `<li>${I('info', 14)}<span>${esc(n)}</span></li>`).join('')}</ul>` : `<p class="muted">${esc(t('notes.none'))}</p>`}
        <h4 class="pane-sub">${esc(t('notes.cheats'))}</h4>
        <div class="conf-list">${list.map((c) => `
          <div class="conf-row" data-cheat-note="${esc(c.id)}" data-conf="${esc(c.confidence || 'untested')}"><span class="row-ic">${I(c.icon, 15)}</span><span class="conf-name">${esc(c.name)}</span>
            <span class="badge badge-${esc(c.confidence || 'untested')}${c.localStatus ? ' is-local' : ''}"${c.localStatus ? ` title="${esc(t('conf.local') + ' · ' + t('conf.base', { c: t('conf.' + (c.baseConfidence || 'untested')) }))}"` : ''}>${c.localStatus ? '<i class="badge-dot"></i>' : ''}${esc(t('conf.' + (c.confidence || 'untested')))}</span>
            <span class="conf-note">${esc(c.description || '')}${c.note || c.confidence === 'broken' ? ` <em class="conf-warn">${esc(c.note || t('conf.warn.broken'))}</em>` : ''}${comm && commLine(comm[c.id]) ? `<span class="conf-comm" data-bind="community" data-comm="${esc((comm[c.id] || {}).status || '')}"${(comm[c.id] || {}).broken > (comm[c.id] || {}).works ? ' data-hot=""' : ''}>${I('globe', 12)}${esc(commLine(comm[c.id]))}</span>` : ''}</span></div>`).join('')}</div>
      </div>`,
    updLine: (u) => updLine(u),
    contextMenu: (c) => {
      const cur = c.localStatus || 'default';
      const item = (s, ic) => `<button class="ctx-item${cur === s ? ' is-current' : ''}" type="button" role="menuitemradio" aria-checked="${cur === s}" data-action="set-status" data-status="${s}">
          <span class="ctx-ic ctx-${s}">${I(ic, 13)}</span><span class="ctx-label">${esc(t('ctx.' + s))}</span>${cur === s ? `<span class="ctx-cur">${I('check', 12)}</span>` : ''}</button>`;
      return `
      <div class="ctx-menu" role="menu" aria-label="${esc(t('ctx.title'))}" data-cheat-menu="${esc(c.id)}">
        <div class="ctx-head"><span class="ctx-name">${esc(c.name)}</span><span class="ctx-sub">${esc(t('ctx.title'))} · ${esc(t('conf.base', { c: t('conf.' + (c.baseConfidence || c.confidence || 'untested')) }))}</span></div>
        ${item('works', 'check')}${item('broken', 'ban')}${item('untested', 'info')}
        <div class="ctx-sep"></div>
        ${item('default', 'refresh')}
        ${c.canReport ? `<div class="ctx-sep"></div><button class="ctx-item ctx-report-item" type="button" role="menuitem" data-action="report-open"><span class="ctx-ic ctx-report">${I('globe', 13)}</span><span class="ctx-label">${esc(t('ctx.report'))}</span></button>` : ''}
      </div>`;
    },
    // The toast root is created once (enter animation plays once); state changes swap only its children, progress
    // only touches the bar transform and the percentage text (see core.js renderToast).
    updateToast: (u) => `<div class="toast" role="status" data-state="${esc(u.state)}">${T.updateToastInner(u)}</div>`,
    updateToastInner: (u) => {
      const busy = u.state === 'downloading' || u.state === 'installing';
      const title = { available: t('upd.available', { v: u.version }), downloading: t('upd.downloading', { v: u.version }), installing: t('upd.installing'),
        pending: t('upd.pending', { v: u.version }), updated: t('upd.updated', { v: u.version }), failed: t('upd.failed', { v: u.version }), error: t('upd.error') }[u.state] || '';
      const body = u.state === 'available' ? (u.notes || '') : u.state === 'error' ? (u.message || '') : u.state === 'pending' ? t('upd.pending.body') : u.state === 'failed' ? t('upd.failed.body') : u.state === 'installing' ? t('upd.installing.body') : '';
      const p = u.state === 'installing' ? 1 : Math.max(0, Math.min(1, u.progress || 0));
      return `
        <span class="toast-ic">${I(u.state === 'error' || u.state === 'failed' ? 'alert' : u.state === 'updated' ? 'check' : 'sparkles', 18)}</span>
        <div class="toast-main">
          <p class="toast-title"><span class="toast-title-text">${esc(title)}</span>${u.state === 'downloading' ? `<span class="toast-pct" data-bind="toast-pct">${Math.round(p * 100)}%</span>` : ''}</p>
          ${body ? `<div class="toast-body">${esc(body).replace(/\n/g, '<br>')}</div>` : ''}
          ${busy ? `<div class="toast-bar${u.state === 'installing' ? ' is-indeterminate' : ''}"><i data-bind="toast-bar" style="transform:scaleX(${p})"></i></div>` : ''}
          ${u.state === 'available' ? `<div class="toast-actions">
            <button class="btn" data-kind="primary" type="button" data-action="update-now">${I('refresh', 15)}<span>${esc(t('upd.now'))}</span></button>
            <button class="btn-ghost" type="button" data-action="update-later">${esc(t('upd.later'))}</button>
            ${u.url ? `<button class="btn-ghost" type="button" data-action="open-url" data-url="${esc(u.url)}">${esc(t('upd.notes'))}</button>` : ''}</div>` : ''}
        </div>
        ${busy ? '' : `<button class="winbtn toast-x" type="button" data-action="update-close" aria-label="${esc(t('win.close'))}">${I('x', 14)}</button>`}`;
    },
    settings: (s, info) => `
      <div class="modal-backdrop" data-action="modal-backdrop">
        <div class="modal" role="dialog" aria-modal="true" aria-labelledby="set-title">
          <header class="modal-head"><span class="modal-logo">${Brand.mark(28)}</span><h3 id="set-title">${esc(t('set.title'))}</h3>
            <button class="winbtn modal-x" type="button" data-action="modal-close" aria-label="${esc(t('win.close'))}">${I('x', 16)}</button></header>
          <div class="modal-body">
            <label class="field"><span class="field-k">${esc(t('set.lang'))}</span>
              <select class="input" data-set="language"><option value="nl"${s.language === 'nl' ? ' selected' : ''}>Nederlands</option><option value="en"${s.language === 'en' ? ' selected' : ''}>English</option></select></label>
            <label class="field"><span class="field-k">${esc(t('set.catalog'))}</span>
              <input class="input mono" data-set="catalogDir" value="${esc(s.catalogDir || '')}" placeholder="…\\Vanta\\games" spellcheck="false">
              <span class="field-help">${esc(t('set.catalog.help'))}${info.catalog ? ` · ${esc(t('set.games', { n: info.catalog.games }))}` : ''}</span></label>
            <label class="field"><span class="field-k">${esc(t('set.url'))}</span>
              <input class="input mono" data-set="catalogUrl" value="${esc(s.catalogUrl || '')}" placeholder="https://…/catalog" spellcheck="false">
              <span class="field-help">${esc(t('set.url.help'))}</span></label>
            <label class="check"><input type="checkbox" data-set="autoAttach"${s.autoAttach !== false ? ' checked' : ''}><span>${esc(t('set.auto'))}</span></label>
            <div class="field acct-field"><span class="field-k">${esc(t('acc.title'))}</span><div data-slot="account">${T.account(info.account || {}, info.confirmDelete)}</div></div>
            ${info.account && info.account.configured ? `<label class="check"><input type="checkbox" data-set="shareUsage"${s.shareUsage ? ' checked' : ''}><span>${esc(t('acc.usage'))}</span></label>
            <span class="field-help check-help">${esc(t('acc.usage.help'))}</span>` : ''}
            <div class="field"><span class="field-k">${esc(t('set.data'))}</span><span class="input mono ro">${esc(info.dataDir || '%LOCALAPPDATA%\\Vanta')}</span></div>
            <div class="field"><span class="field-k">${esc(t('upd.title'))}</span>
              <div class="upd-row"><span class="input mono ro">${esc(Brand.name)} ${esc(info.version)}</span>
                <button class="btn-ghost sm" type="button" data-action="check-update"${info.update && info.update.state === 'checking' ? ' disabled' : ''}>${I('refresh', 13)}${esc(t('upd.check'))}</button></div>
              <span class="field-help" data-bind="update-status">${esc(updLine(info.update))}</span></div>
            <div class="field"><span class="field-k">${esc(t('set.status'))}</span>
              <div class="upd-row"><span class="field-help grow">${esc(t('set.status.help'))}</span>
                <button class="btn-ghost sm" type="button" data-action="export-status">${I('copy', 13)}${esc(t('set.status.export'))}</button></div>
              <span class="field-help mono" data-bind="export-status" hidden></span></div>
            <div class="about">${I('shield', 15)}<span><b>${esc(Brand.name)} ${esc(info.version)}</b> · ${esc(t('set.scope'))}</span></div>
          </div>
          <footer class="modal-foot">
            <button class="btn-ghost" type="button" data-action="modal-close">${esc(t('set.cancel'))}</button>
            <button class="btn" data-kind="primary" type="button" data-action="modal-save">${I('check', 16)}<span>${esc(t('set.save'))}</span></button>
          </footer>
        </div>
      </div>`,
    account: (a, confirmDelete) => {
      if (!a.configured) return `<span class="field-help" data-bind="acct-state" data-state="off">${esc(t('acc.notConfigured'))}</span>`;
      const err = a.error ? `<span class="field-help acct-err" data-bind="acct-error">${esc(accErr(a.error))}</span>` : '';
      if (!a.loggedIn) return `
        <div class="acct" data-bind="acct-state" data-state="${a.busy ? 'busy' : 'out'}">
          <div class="upd-row"><span class="field-help grow">${esc(a.busy ? t('acc.waiting') : t('acc.help'))}</span>
            ${a.busy ? `<button class="btn-ghost sm" type="button" data-action="account-cancel">${esc(t('acc.cancel'))}</button>`
                     : `<button class="btn sm acct-login" data-kind="primary" type="button" data-action="account-login">${I('user', 14)}<span>${esc(t('acc.login'))}</span></button>`}${a.privacyUrl ? `<button class="btn-ghost sm acct-privacy" type="button" data-action="open-url" data-url="${esc(a.privacyUrl)}">${esc(t('acc.privacy'))}</button>` : ''}</div>
          ${err}</div>`;
      const u = a.user || {};
      return `
        <div class="acct" data-bind="acct-state" data-state="in">
          <div class="acct-user"><span class="acct-avatar">${u.avatarUrl ? `<img src="${esc(u.avatarUrl)}" alt="" referrerpolicy="no-referrer" onerror="this.remove()">` : ''}${I('user', 16)}</span>
            <span class="acct-name" data-bind="acct-name">${esc(t('acc.as', { n: u.username || '' }))}</span>
            ${confirmDelete ? '' : `<button class="btn-ghost sm" type="button" data-action="account-logout"${a.busy ? ' disabled' : ''}>${esc(t('acc.logout'))}</button>
            <button class="btn-ghost sm acct-del" type="button" data-action="account-delete"${a.busy ? ' disabled' : ''}>${esc(t('acc.delete'))}</button>
            ${a.privacyUrl ? `<button class="btn-ghost sm acct-privacy" type="button" data-action="open-url" data-url="${esc(a.privacyUrl)}">${esc(t('acc.privacy'))}</button>` : ''}`}</div>
          ${confirmDelete ? `<div class="acct-confirm" role="alertdialog"><span>${esc(t('acc.confirm'))}</span>
            <button class="btn-ghost sm" type="button" data-action="account-delete-cancel">${esc(t('acc.cancel'))}</button>
            <button class="btn sm acct-del-yes" data-kind="danger" type="button" data-action="account-delete-confirm"${a.busy ? ' disabled' : ''}>${esc(t('acc.confirm.yes'))}</button></div>` : ''}
          ${a.pending ? `<span class="field-help">${esc(t('acc.pending', { n: a.pending }))}</span>` : ''}
          ${err}</div>`;
    },
    report: (c, r, a, comm, g) => `
      <div class="modal-backdrop" data-action="modal-backdrop">
        <div class="modal modal-report" role="dialog" aria-modal="true" aria-labelledby="rep-title">
          <header class="modal-head"><span class="modal-logo">${Brand.mark(28)}</span><h3 id="rep-title">${esc(t('rep.title', { n: c.name }))}</h3>
            <button class="winbtn modal-x" type="button" data-action="modal-close" aria-label="${esc(t('win.close'))}">${I('x', 16)}</button></header>
          <div class="modal-body">
            ${g && g.version ? `<span class="field-help">${esc(t('rep.version', { v: g.version }))}</span>` : ''}
            <div class="rep-seg" role="radiogroup" aria-label="${esc(t('ctx.title'))}">
              ${['works', 'broken'].map((s) => `<button type="button" role="radio" class="rep-opt" data-action="report-pick" data-status="${s}" aria-checked="${r.status === s}"><span class="ctx-ic ctx-${s}">${I(s === 'works' ? 'check' : 'ban', 13)}</span>${esc(t('rep.' + s))}</button>`).join('')}
            </div>
            ${a.loggedIn ? `<label class="field" data-bind="rep-note-field"${r.status === 'broken' ? '' : ' hidden'}><span class="field-k">${esc(t('rep.note'))}</span>
              <textarea class="input rep-note" data-role="report-note" maxlength="300" rows="3" placeholder="${esc(t('rep.note.ph'))}" spellcheck="true">${esc(r.note || '')}</textarea>
              <span class="field-help"><span data-bind="rep-count">${(r.note || '').length}</span>/300 · ${esc(t('rep.note.help'))}</span></label>`
            : `<p class="rep-login" data-bind="rep-login">${esc(t('rep.login'))}</p>`}
            ${commLine(comm) ? `<p class="conf-comm">${I('globe', 12)}${esc(commLine(comm))}</p>` : ''}
            ${r.error ? `<span class="field-help acct-err" data-bind="rep-error">${esc(accErr(r.error))}</span>` : ''}
          </div>
          <footer class="modal-foot">
            <button class="btn-ghost" type="button" data-action="modal-close">${esc(t(a.loggedIn ? 'rep.skip' : 'set.cancel'))}</button>
            ${a.loggedIn ? `<button class="btn" data-kind="primary" type="button" data-action="report-send"${r.sending ? ' disabled' : ''}>${I('check', 16)}<span>${esc(t(r.sending ? 'rep.sending' : 'rep.send'))}</span></button>`
              : `<button class="btn acct-login" data-kind="primary" type="button" data-action="account-login"${a.busy ? ' disabled' : ''}>${I('user', 16)}<span>${esc(a.busy ? t('acc.waiting') : t('acc.login'))}</span></button>`}
          </footer>
        </div>
      </div>`,
  };

  TrainerCore.mount({
    root: document.getElementById('app'),
    templates: T,
    onGameChange: (g) => document.documentElement.style.setProperty('--game-tint', Art.artOf(g).tint),
  });
})();
