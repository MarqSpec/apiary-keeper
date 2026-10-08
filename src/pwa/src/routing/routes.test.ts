import { describe, expect, it } from 'vitest';
import { hrefFor, parseRoute, type Route } from './routes';

describe('parseRoute', () => {
  it.each<[string, Route]>([
    ['', { name: 'yards' }],
    ['#', { name: 'yards' }],
    ['#/', { name: 'yards' }],
    ['#/yards', { name: 'yards' }],
    ['#/yards/new', { name: 'yard-new' }],
    ['#/yards/y-1', { name: 'yard', id: 'y-1' }],
    ['#/yards/y-1/edit', { name: 'yard-edit', id: 'y-1' }],
    ['#/colonies', { name: 'colonies' }],
    ['#/colonies/new', { name: 'colony-new', origin: 'registered', yardId: null }],
    ['#/colonies/new?yard=y-1', { name: 'colony-new', origin: 'registered', yardId: 'y-1' }],
    ['#/colonies/swarm?yard=y-1', { name: 'colony-new', origin: 'swarm-capture', yardId: 'y-1' }],
    ['#/colonies/c-1', { name: 'colony', id: 'c-1' }],
    ['#/colonies/c-1/split', { name: 'colony-split', id: 'c-1' }],
    ['#/colonies/c-1/inspect', { name: 'colony-inspect', id: 'c-1' }],
  ])('should read %s as its screen when the fragment names one', (hash, route) => {
    expect(parseRoute(hash)).toEqual(route);
  });

  it.each(['#/nowhere', '#/yards/y-1/delete', '#/colonies/c-1/split/again', '#xyards', '#/yards/%E0'])(
    'should be not found when the fragment %s names no screen',
    (hash) => {
      expect(parseRoute(hash)).toEqual({ name: 'not-found' });
    },
  );

  it('should decode an id when it was encoded', () => {
    expect(parseRoute('#/yards/a%2Fb')).toEqual({ name: 'yard', id: 'a/b' });
  });
});

describe('hrefFor', () => {
  it.each<Route>([
    { name: 'yards' },
    { name: 'yard-new' },
    { name: 'yard', id: 'a/b' },
    { name: 'yard-edit', id: 'y-1' },
    { name: 'colonies' },
    { name: 'colony-new', origin: 'registered', yardId: null },
    { name: 'colony-new', origin: 'swarm-capture', yardId: 'y 1' },
    { name: 'colony', id: 'c-1' },
    { name: 'colony-split', id: 'c-1' },
    { name: 'colony-inspect', id: 'c/1' },
  ])('should round-trip through parseRoute when the route is %o', (route) => {
    expect(parseRoute(hrefFor(route))).toEqual(route);
  });
});
