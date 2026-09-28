/* Vanta: data model.
 * Production: the host injects window.vantaHost.library (built from the catalog index: games/index.json).
 * Games arrive "lazy" (no cheats); the full definition is sent by the host as a {type:'game'} message
 * when a game is selected. That keeps 1000+ games fast.
 * Dev/browser: shared/devdata.js (generated from the real catalog by `vanta-tool uifixture`) + mock bridge.
 */
(function (root) {
  'use strict';
  const sections = ['speler', 'wapens', 'punten', 'extra', 'wereld', 'voertuig', 'inventaris'].map((id) => ({
    id, icon: { speler: 'user', wapens: 'crosshair', punten: 'star', extra: 'sparkles', wereld: 'globe', voertuig: 'car', inventaris: 'box' }[id],
  }));
  const groups = [{ id: 'recent' }, { id: 'all' }];

  const host = root.vantaHost && root.vantaHost.library;
  const dev = root.VantaDev && root.VantaDev.library;
  const lib = host || dev || { games: [], app: {} };
  const games = lib.games.map((g) => Object.assign({ cheats: [], lazy: true, categories: [] }, g));

  // dev only: ?many=1200 adds synthetic games to try the library at catalog scale
  const many = !host && Number(new URLSearchParams(root.location.search).get('many'));
  if (many > 0) {
    const cats = ['survival', 'rpg', 'shooter', 'strategy', 'racing', 'sim', 'horror', 'indie'];
    for (let i = 0; i < many; i++) games.push({ id: `demo-${i}`, name: `Demo Game ${String(i + 1).padStart(4, '0')}`, short: 'DG', group: 'all', badge: 'v1.0',
      version: '1.0', cheatCount: 3 + (i % 12), categories: [cats[i % cats.length]], cheats: [], lazy: true, process: `Demo${i}.exe` });
  }

  const app = Object.assign({ name: 'Vanta', version: '0.1.0', production: false, lang: 'en', ackTimeout: 5000 }, lib.app || {});
  root.I18N.lang = app.lang || 'en';
  root.TrainerData = {
    app, sections, groups, games, hosted: !!host,
    selectedGameId: lib.selected || (games[0] && games[0].id),
    log: { time: '', text: host ? '' : root.t('sb.dev'), level: 'info' },
  };
})(window);
