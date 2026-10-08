import '@testing-library/jest-dom/vitest';
import { cleanup } from '@testing-library/react';
import { afterAll, afterEach, beforeAll } from 'vitest';
import { server } from './server';

// Any request a test did not stub fails the test, so nothing reaches a real network.
beforeAll(() => server.listen({ onUnhandledRequest: 'error' }));
afterAll(() => server.close());

// jsdom has no matchMedia; MUI reads prefers-color-scheme through it.
let dark = false;
const listeners = new Set<(event: MediaQueryListEvent) => void>();

/** Sets the device's light/dark preference that `prefers-color-scheme` reports to the app. */
export function prefersDark(value: boolean): void {
  dark = value;
}

Object.defineProperty(window, 'matchMedia', {
  writable: true,
  value: (query: string): MediaQueryList =>
    ({
      matches: query.includes('prefers-color-scheme: dark') ? dark : false,
      media: query,
      onchange: null,
      addListener: (listener: (event: MediaQueryListEvent) => void) => listeners.add(listener),
      removeListener: (listener: (event: MediaQueryListEvent) => void) => listeners.delete(listener),
      addEventListener: () => {},
      removeEventListener: () => {},
      dispatchEvent: () => false,
    }) as unknown as MediaQueryList,
});

afterEach(() => {
  cleanup();
  server.resetHandlers();
  window.localStorage.clear();
  window.location.hash = '';
  listeners.clear();
  for (const attribute of [...document.documentElement.attributes]) {
    if (attribute.name.startsWith('data-')) document.documentElement.removeAttribute(attribute.name);
  }
});
