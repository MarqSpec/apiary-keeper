import type { ManifestOptions } from 'vite-plugin-pwa';

/**
 * The web app manifest that makes the family PWA installable (D-4). Its colours are the honey theme's light
 * primary and background; the icons are generated from `public/icon.svg` by `npm run generate-pwa-assets`.
 */
export const manifest: Partial<ManifestOptions> = {
  name: 'Apiary-Keeper',
  short_name: 'Apiary-Keeper',
  description:
    'Apiary-Keeper is an offline-first web app for owner-operator Texas apiarists that tracks yards, hives, inspections, treatments, harvests, and sales, and includes a Telegram family chat, a customer storefront, and AI agents planned for a later phase.',
  start_url: '/',
  scope: '/',
  display: 'standalone',
  theme_color: '#8A5100',
  background_color: '#F3E3C6',
  icons: [
    { src: 'pwa-64x64.png', sizes: '64x64', type: 'image/png' },
    { src: 'pwa-192x192.png', sizes: '192x192', type: 'image/png' },
    { src: 'pwa-512x512.png', sizes: '512x512', type: 'image/png' },
    { src: 'maskable-icon-512x512.png', sizes: '512x512', type: 'image/png', purpose: 'maskable' },
  ],
};
