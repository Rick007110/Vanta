/* PLACEHOLDER ART GENERATOR. Procedural SVG scenes built from a game's palette in data.js.
 * NOT game art: abstract horizon, sun, ridges and sea shapes only. Replace with licensed covers or heroes in production
 * (keep the same slots: hero 16:6, cover 3:4, thumb 1:1).
 */
(function (root) {
  'use strict';
  let uid = 0;
  function rng(seed) { let s = 0; for (const ch of seed) s = (s * 31 + ch.charCodeAt(0)) >>> 0; return () => ((s = (s * 1664525 + 1013904223) >>> 0) / 4294967296); }
  function hex2rgb(h) { h = h.replace('#', ''); return [0, 2, 4].map((i) => parseInt(h.substr(i, 2), 16)); }
  function mix(a, b, t) { const A = hex2rgb(a), B = hex2rgb(b); return '#' + A.map((v, i) => Math.round(v + (B[i] - v) * t).toString(16).padStart(2, '0')).join(''); }

  function ridgePath(r, w, h, baseY, amp, steps) {
    const pts = []; for (let i = 0; i <= steps; i++) pts.push([(w / steps) * i, baseY - r() * amp - (Math.sin(i * 1.3 + r() * 3) * amp) / 3]);
    let d = `M0 ${h} L0 ${pts[0][1].toFixed(1)}`;
    for (let i = 0; i < pts.length - 1; i++) { const mx = (pts[i][0] + pts[i + 1][0]) / 2, my = (pts[i][1] + pts[i + 1][1]) / 2; d += ` Q${pts[i][0].toFixed(1)} ${pts[i][1].toFixed(1)} ${mx.toFixed(1)} ${my.toFixed(1)}`; }
    return d + ` L${w} ${pts[pts.length - 1][1].toFixed(1)} L${w} ${h} Z`;
  }

  /** scene(game, w, h, opts): full SVG string. opts.sunX/sunY are fractions, opts.grain toggles grain, opts.cls sets a class. */
  // games without an "art" block get a deterministic palette from their id
  const PALETTES = [
    { sky: ['#0B1022', '#2B2F6B'], horizon: '#F29E6D', ground: '#07080F', tint: '#8B7CFF', motif: 'ridge' },
    { sky: ['#07131F', '#17465A'], horizon: '#F0A56E', ground: '#061015', tint: '#5CC8B8', motif: 'sea' },
    { sky: ['#140B1C', '#4A2150'], horizon: '#FFB86B', ground: '#0B0610', tint: '#E879F9', motif: 'planet' },
    { sky: ['#0A1410', '#1F4A3A'], horizon: '#E8D38A', ground: '#050A08', tint: '#4ADE80', motif: 'ridge' },
    { sky: ['#1A0F0A', '#5A2A18'], horizon: '#FFC58A', ground: '#0C0604', tint: '#FB923C', motif: 'ridge' },
  ];
  function artOf(game) {
    if (game.art && game.art.sky && game.art.horizon) return Object.assign({ ground: '#07080F', tint: '#8B7CFF', motif: 'ridge' }, game.art);
    let h = 0; for (const ch of String(game.id)) h = (h * 31 + ch.charCodeAt(0)) >>> 0;
    return PALETTES[h % PALETTES.length];
  }
  function scene(game, w, h, opts = {}) {
    const a = artOf(game), r = rng(game.id + (opts.seed || '')), id = 'art' + ++uid;
    const sunX = (opts.sunX ?? 0.68) * w, horizonY = (opts.horizon ?? 0.62) * h, sunR = Math.min(w, h) * (opts.sunR ?? 0.13);
    const sunY = opts.sunY != null ? opts.sunY * h : horizonY - sunR * 0.35;
    let s = `<svg class="${opts.cls || ''}" viewBox="0 0 ${w} ${h}" preserveAspectRatio="xMidYMid slice" xmlns="http://www.w3.org/2000/svg" role="img" aria-label="Placeholder art">
<!-- PLACEHOLDER ART: procedurally generated, not real game art -->
<defs>
 <linearGradient id="${id}s" x1="0" y1="0" x2="0" y2="1"><stop offset="0" stop-color="${a.sky[0]}"/><stop offset=".62" stop-color="${a.sky[1]}"/><stop offset="1" stop-color="${mix(a.sky[1], a.horizon, 0.55)}"/></linearGradient>
 <radialGradient id="${id}g" cx="${sunX / w}" cy="${sunY / h}" r=".55"><stop offset="0" stop-color="${a.horizon}" stop-opacity=".55"/><stop offset=".35" stop-color="${a.horizon}" stop-opacity=".14"/><stop offset="1" stop-color="${a.horizon}" stop-opacity="0"/></radialGradient>
 <linearGradient id="${id}w" x1="0" y1="0" x2="0" y2="1"><stop offset="0" stop-color="${mix(a.sky[1], a.ground, 0.35)}"/><stop offset="1" stop-color="${a.ground}"/></linearGradient>
 <filter id="${id}n"><feTurbulence type="fractalNoise" baseFrequency=".85" numOctaves="2" stitchTiles="stitch"/><feColorMatrix type="saturate" values="0"/><feComponentTransfer><feFuncA type="table" tableValues="0 .07"/></feComponentTransfer></filter>
</defs>
<rect width="${w}" height="${h}" fill="url(#${id}s)"/>`;
    // stars
    for (let i = 0; i < 70; i++) { const x = r() * w, y = r() * horizonY * 0.6; s += `<circle cx="${x.toFixed(1)}" cy="${y.toFixed(1)}" r="${(r() * 1.1 + 0.3).toFixed(2)}" fill="#fff" opacity="${(r() * 0.5 + 0.1).toFixed(2)}"/>`; }
    s += `<rect width="${w}" height="${h}" fill="url(#${id}g)"/>`;
    if (a.motif === 'planet') s += `<circle cx="${w * 0.22}" cy="${h * 0.26}" r="${sunR * 1.6}" fill="${mix(a.sky[1], a.horizon, 0.35)}" opacity=".9"/><ellipse cx="${w * 0.22}" cy="${h * 0.26}" rx="${sunR * 2.6}" ry="${sunR * 0.5}" fill="none" stroke="${a.horizon}" stroke-opacity=".45" stroke-width="${Math.max(1.5, sunR * 0.06)}"/>`;
    s += `<circle cx="${sunX}" cy="${sunY}" r="${sunR}" fill="${mix(a.horizon, '#ffffff', 0.25)}"/>`;
    if (a.motif === 'sea') {
      // distant islands
      s += `<path d="${ridgePath(r, w, horizonY + 1, horizonY, h * 0.05, 14)}" fill="${mix(a.sky[1], a.ground, 0.45)}" opacity=".9"/>`;
      s += `<rect y="${horizonY}" width="${w}" height="${h - horizonY}" fill="url(#${id}w)"/>`;
      for (let i = 0; i < 26; i++) { const y = horizonY + 4 + i * i * ((h - horizonY) / 700); const ww = sunR * (2.2 - i * 0.06) * (0.6 + r() * 0.6); s += `<rect x="${(sunX - ww / 2 + (r() - 0.5) * sunR * 0.6).toFixed(1)}" y="${y.toFixed(1)}" width="${ww.toFixed(1)}" height="${(1.2 + i * 0.12).toFixed(1)}" rx="1" fill="${a.horizon}" opacity="${(0.55 - i * 0.02).toFixed(2)}"/>`; }
      // abstract structure silhouette (platform + mast)
      const bx = w * (opts.structX ?? 0.2), by = horizonY + h * 0.03, u = h / 60, dk = mix(a.ground, '#000000', 0.2);
      s += `<g fill="${dk}"><rect x="${bx - 9 * u}" y="${by - 2 * u}" width="${18 * u}" height="${2.4 * u}"/><rect x="${bx - 6 * u}" y="${by - 8 * u}" width="${7 * u}" height="${6 * u}"/><rect x="${bx + 2 * u}" y="${by - 5 * u}" width="${4 * u}" height="${3 * u}"/><rect x="${bx - 3.4 * u}" y="${by - 22 * u}" width="${1.2 * u}" height="${14 * u}"/><rect x="${bx - 7 * u}" y="${by + 0.4 * u}" width="${0.9 * u}" height="${5 * u}"/><rect x="${bx + 6 * u}" y="${by + 0.4 * u}" width="${0.9 * u}" height="${5 * u}"/></g>`;
      s += `<circle cx="${bx - 2.8 * u}" cy="${by - 22 * u}" r="${0.7 * u}" fill="${a.tint}"/><circle cx="${bx - 4 * u}" cy="${by - 6 * u}" r="${0.35 * u}" fill="${a.horizon}"/><circle cx="${bx - 1.5 * u}" cy="${by - 6 * u}" r="${0.35 * u}" fill="${a.horizon}"/>`;
    } else {
      const n = 4;
      for (let i = 0; i < n; i++) {
        const t = i / (n - 1);
        const col = mix(mix(a.sky[1], a.horizon, 0.25), a.ground, 0.35 + t * 0.65);
        s += `<path d="${ridgePath(r, w, h, horizonY + t * h * 0.24, h * (0.2 - t * 0.08), 9 + i * 3)}" fill="${col}"/>`;
      }
    }
    if (opts.grain !== false) s += `<rect width="${w}" height="${h}" filter="url(#${id}n)"/>`;
    return s + '</svg>';
  }

  root.Art = {
    hero: (g, o = {}) => scene(g, 1600, 600, Object.assign({ sunX: 0.7, horizon: 0.64, sunR: 0.16, structX: 0.6 }, o)),
    cover: (g, o = {}) => scene(g, 300, 400, Object.assign({ sunX: 0.62, horizon: 0.6, sunR: 0.2 }, o)),
    thumb: (g, o = {}) => scene(g, 96, 96, Object.assign({ sunX: 0.6, horizon: 0.62, sunR: 0.22, grain: false }, o)),
    mix, artOf,
  };
})(window);
