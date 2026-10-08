import { useSyncExternalStore } from 'react';
import { hrefFor, parseRoute, type Route } from './routes';

function subscribe(onChange: () => void): () => void {
  window.addEventListener('hashchange', onChange);
  return () => window.removeEventListener('hashchange', onChange);
}

const currentHash = () => window.location.hash;

/** The screen the URL fragment names; re-renders when it changes (a link, back or forward). */
export function useRoute(): Route {
  return parseRoute(useSyncExternalStore(subscribe, currentHash));
}

/** Shows another screen, as following a link would; back returns to this one. */
export function navigate(route: Route): void {
  window.location.hash = hrefFor(route);
}
