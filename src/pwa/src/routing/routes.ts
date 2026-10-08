// The screens live in the URL fragment (`/#/yards/…`), so the server only ever sees `/`: no path falls back to
// index.html and an unknown path stays a 404 (INTERFACES.md D.1).

/** Every screen the PWA can show, and what it needs to show it. */
export type Route =
  | { readonly name: 'yards' }
  | { readonly name: 'yard-new' }
  | { readonly name: 'yard'; readonly id: string }
  | { readonly name: 'yard-edit'; readonly id: string }
  | { readonly name: 'colonies' }
  | {
      readonly name: 'colony-new';
      readonly origin: 'registered' | 'swarm-capture';
      readonly yardId: string | null;
    }
  | { readonly name: 'colony'; readonly id: string }
  | { readonly name: 'colony-split'; readonly id: string }
  | { readonly name: 'colony-inspect'; readonly id: string }
  | { readonly name: 'not-found' };

const notFound: Route = { name: 'not-found' };

/** Reads the screen from a location hash (`#/yards/…`); an empty hash is the yards list. */
export function parseRoute(hash: string): Route {
  if (hash === '' || hash === '#' || hash === '#/') return { name: 'yards' };
  if (!hash.startsWith('#/')) return notFound;

  const [path = '', query = ''] = hash.slice(2).split('?', 2);
  const yardId = new URLSearchParams(query).get('yard');
  let segments: string[];
  try {
    segments = path.split('/').map(decodeURIComponent);
  } catch {
    return notFound; // a malformed escape, such as a lone %
  }
  const [first, second, third, ...rest] = segments;
  if (rest.length > 0) return notFound;

  if (first === 'yards') {
    if (second === undefined) return { name: 'yards' };
    if (second === 'new' && third === undefined) return { name: 'yard-new' };
    if (third === undefined) return { name: 'yard', id: second };
    if (third === 'edit') return { name: 'yard-edit', id: second };
    return notFound;
  }

  if (first === 'colonies') {
    if (second === undefined) return { name: 'colonies' };
    if (second === 'new' && third === undefined) return { name: 'colony-new', origin: 'registered', yardId };
    if (second === 'swarm' && third === undefined) return { name: 'colony-new', origin: 'swarm-capture', yardId };
    if (third === undefined) return { name: 'colony', id: second };
    if (third === 'split') return { name: 'colony-split', id: second };
    if (third === 'inspect') return { name: 'colony-inspect', id: second };
    return notFound;
  }

  return notFound;
}

const segment = encodeURIComponent;

/** The link to a screen, as an `href` (`#/yards/…`). */
export function hrefFor(route: Route): string {
  switch (route.name) {
    case 'yards':
      return '#/yards';
    case 'yard-new':
      return '#/yards/new';
    case 'yard':
      return `#/yards/${segment(route.id)}`;
    case 'yard-edit':
      return `#/yards/${segment(route.id)}/edit`;
    case 'colonies':
      return '#/colonies';
    case 'colony-new': {
      const path = route.origin === 'registered' ? '#/colonies/new' : '#/colonies/swarm';
      return route.yardId === null ? path : `${path}?${new URLSearchParams({ yard: route.yardId }).toString()}`;
    }
    case 'colony':
      return `#/colonies/${segment(route.id)}`;
    case 'colony-split':
      return `#/colonies/${segment(route.id)}/split`;
    case 'colony-inspect':
      return `#/colonies/${segment(route.id)}/inspect`;
    case 'not-found':
      return '#/not-found';
    default: {
      const unreachable: never = route;
      return unreachable;
    }
  }
}
