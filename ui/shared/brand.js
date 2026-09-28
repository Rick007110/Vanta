/* Branding in one place (see BRANDING.md). The host sets document.title from Brand.name too. */
(function (root) {
  'use strict';
  const name = 'Vanta';
  root.Brand = {
    name,
    tagline: { en: 'Trainer for single-player games', nl: 'Trainer voor singleplayer-games' },
    // Logo mark: rounded square with a violet->blue gradient and a stylised "V" (same artwork as app.ico)
    mark: (size = 24) => `<svg class="brand-svg" width="${size}" height="${size}" viewBox="0 0 64 64" aria-hidden="true">
      <defs><linearGradient id="vg" x1="0" y1="0" x2="1" y2="1"><stop offset="0" stop-color="#A78BFA"/><stop offset=".55" stop-color="#6D5BFF"/><stop offset="1" stop-color="#3B82F6"/></linearGradient>
      <linearGradient id="vs" x1="0" y1="0" x2="0" y2="1"><stop offset="0" stop-color="#fff"/><stop offset="1" stop-color="#DCE3FF"/></linearGradient></defs>
      <rect x="2" y="2" width="60" height="60" rx="17" fill="url(#vg)"/>
      <rect x="2.5" y="2.5" width="59" height="59" rx="16.5" fill="none" stroke="#fff" stroke-opacity=".22"/>
      <path d="M17 18h9.2L32 36.5 37.8 18H47L36.4 46.5h-8.8z" fill="url(#vs)"/>
    </svg>`,
  };
})(window);
