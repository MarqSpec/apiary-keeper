import react from '@vitejs/plugin-react';
import { VitePWA } from 'vite-plugin-pwa';
import { defineConfig } from 'vitest/config';
import { manifest } from './src/pwa/manifest.ts';

// https://vite.dev/config/
export default defineConfig({
  plugins: [
    react(),
    // The service worker precaches the app shell only; offline data is Phase 1 (FR-9).
    VitePWA({
      registerType: 'autoUpdate',
      injectRegister: 'script-defer',
      // A manifest is fetched without credentials unless asked, and behind the staging gate that is a 401.
      useCredentials: true,
      includeAssets: ['favicon.ico', 'icon.svg', 'apple-touch-icon-180x180.png'],
      manifest,
      workbox: {
        globPatterns: ['**/*.{js,css,html,ico,png,svg,webmanifest}'],
        navigateFallback: 'index.html',
        // The API host serves these itself; the shell must never answer for them.
        navigateFallbackDenylist: [/^\/health/, /^\/metrics/, /^\/openapi/, /^\/api\//],
      },
    }),
  ],
  // `npm run dev` sends API calls to the API on its own port (README.md *Local development*), so the app calls
  // `/api/...` on its own origin in development as it does when the API host serves it.
  server: {
    proxy: { '/api': 'http://localhost:5080' },
  },
  test: {
    environment: 'jsdom',
    setupFiles: ['./src/test/setup.ts'],
    // A form test types every field as a user does; with the files run in parallel that can pass 5 s.
    testTimeout: 20_000,
  },
});
