/* Small, safe Markdown renderer for release notes (GitHub release body).
 * Everything is escaped first; only a fixed set of tags is produced (h3-h5, p, ul/ol/li, strong, em, del, code, pre,
 * blockquote, hr, button.md-link). Links never navigate the WebView: they become [data-action=open-url] buttons that the host opens
 * in the external browser, and only http(s) URLs are kept.
 */
(function (root) {
  'use strict';
  const esc = (s) => String(s ?? '').replace(/[&<>"']/g, (c) => ({ '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;', "'": '&#39;' }[c]));
  const MAX = 30000;
  const safeUrl = (u) => { u = String(u || '').trim().replace(/^<|>$/g, ''); return /^https:\/\/[^\s<>"']+$/i.test(u) && u.length <= 2000 ? u : null; };
  // a button, not <a href>: nothing can navigate the WebView itself (also not via middle-click / Ctrl+click)
  const link = (url, html) => `<button type="button" class="md-link" data-action="open-url" data-url="${esc(url)}" title="${esc(url)}">${html}</button>`;

  // inline: code spans, links and bare URLs are pulled out as placeholders before escaping, then emphasis is applied
  function inline(src, depth = 0) {
    const keep = [];
    const put = (html) => `\u0000${keep.push(html) - 1}\u0000`;
    let s = String(src).replace(/\u0000/g, '');
    s = s.replace(/(`+)([^`]|[^`][\s\S]*?[^`])\1(?!`)/g, (m, t, code) => put(`<code>${esc(code.trim())}</code>`));
    s = s.replace(/!\[([^\]]*)\]\(((?:[^()\s]|\([^()\s]*\))+)(?:\s+"[^"]*")?\)/g, (m, alt, url) => { const u = safeUrl(url); return u ? put(link(u, esc(alt || u))) : esc(alt); });
    s = s.replace(/\[([^\]]+)\]\(((?:[^()\s]|\([^()\s]*\))+)(?:\s+"[^"]*")?\)/g, (m, text, url) => { const u = safeUrl(url); return u ? put(link(u, depth ? esc(text) : inline(text, 1))) : text; });
    s = s.replace(/<(https?:\/\/[^>\s]+)>/g, (m, url) => { const u = safeUrl(url); return u ? put(link(u, esc(u))) : m; });
    s = s.replace(/(^|[\s(])(https?:\/\/[^\s<>()]+[^\s<>().,;:!?'"])/g, (m, pre, url) => { const u = safeUrl(url); return u ? pre + put(link(u, esc(u.replace(/^https?:\/\//, '')))) : m; });
    s = esc(s);
    s = s.replace(/(\*\*|__)(?=\S)([\s\S]*?\S)\1/g, '<strong>$2</strong>')
         .replace(/(^|[^\w*])\*(?=\S)([^*]*?\S)\*(?!\*)/g, '$1<em>$2</em>')
         .replace(/(^|[^\w_])_(?=\S)([^_]*?\S)_(?!\w)/g, '$1<em>$2</em>')
         .replace(/~~(?=\S)([\s\S]*?\S)~~/g, '<del>$1</del>');
    return s.replace(/\u0000(\d+)\u0000/g, (m, i) => keep[+i] ?? '');
  }

  function render(md) {
    const lines = String(md || '').slice(0, MAX).replace(/\r\n?/g, '\n').replace(/<!--[\s\S]*?-->/g, '').split('\n');
    const out = [];
    let para = [], list = null;          // list: stack of { type, indent, items: [html] }
    const flushPara = () => { if (para.length) { out.push(`<p>${para.map((l) => inline(l)).join('<br>')}</p>`); para = []; } };
    const closeLists = (indent = -1) => {
      while (list && list.length && list[list.length - 1].indent > indent) {
        const l = list.pop();
        const html = `<${l.type}>${l.items.map((x) => `<li>${x}</li>`).join('')}</${l.type}>`;
        if (list.length) { const p = list[list.length - 1]; p.items[p.items.length - 1] += html; } else out.push(html);
      }
      if (list && !list.length) list = null;
    };
    for (let i = 0; i < lines.length; i++) {
      const raw = lines[i], line = raw.replace(/\s+$/, '');
      const fence = /^\s*(```|~~~)/.exec(line);
      if (fence) {
        flushPara(); closeLists();
        const code = [];
        while (++i < lines.length && !lines[i].trim().startsWith(fence[1])) code.push(lines[i]);
        out.push(`<pre><code>${esc(code.join('\n'))}</code></pre>`);
        continue;
      }
      if (!line.trim()) { flushPara(); if (list && !/^\s*([-*+]|\d+[.)])\s/.test(lines[i + 1] || '')) closeLists(); continue; }
      const h = /^\s{0,3}(#{1,6})\s+(.*?)\s*#*\s*$/.exec(line);
      if (h) { flushPara(); closeLists(); const lvl = Math.min(5, h[1].length + 2); out.push(`<h${lvl}>${inline(h[2])}</h${lvl}>`); continue; }
      if (/^\s{0,3}([-*_])(\s*\1){2,}\s*$/.test(line)) { flushPara(); closeLists(); out.push('<hr>'); continue; }
      const li = /^(\s*)([-*+]|\d{1,3}[.)])\s+(?:\[([ xX])\]\s+)?(.*)$/.exec(line);
      if (li) {
        flushPara();
        const indent = li[1].replace(/\t/g, '    ').length, type = /\d/.test(li[2]) ? 'ol' : 'ul';
        const box = li[3] ? `<span class="md-check${li[3] === ' ' ? '' : ' is-done'}"></span>` : '';
        if (list) closeLists(indent);
        if (list && list[list.length - 1].indent === indent && list[list.length - 1].type !== type) closeLists(indent - 1);
        if (!list) list = [];
        const top = list[list.length - 1];
        if (!top || indent > top.indent) list.push({ type, indent, items: [box + inline(li[4])] });
        else top.items.push(box + inline(li[4]));
        continue;
      }
      if (list && /^\s{2,}\S/.test(raw)) { const top = list[list.length - 1]; top.items[top.items.length - 1] += ' ' + inline(line.trim()); continue; }
      const q = /^\s{0,3}>\s?(.*)$/.exec(line);
      if (q) {
        flushPara(); closeLists();
        const body = [q[1]];
        while (i + 1 < lines.length && /^\s{0,3}>/.test(lines[i + 1])) body.push(lines[++i].replace(/^\s{0,3}>\s?/, ''));
        out.push(`<blockquote>${render(body.join('\n'))}</blockquote>`);
        continue;
      }
      closeLists();
      para.push(line.trim());
    }
    flushPara(); closeLists();
    return out.join('\n');
  }

  root.VantaMd = { render, inline, safeUrl };
})(window);
