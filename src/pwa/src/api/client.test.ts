import { http, HttpResponse } from 'msw';
import { describe, expect, it } from 'vitest';
import { z } from 'zod';
import { server } from '../test/server';
import { ApiError, apiRequest, fetchAllPages, fetchPage } from './client';

const thing = z.object({ id: z.string() });

function problem(status: number, title: string, errors?: Record<string, string[]>) {
  return HttpResponse.json(
    { type: 'about:blank', title, status, ...(errors ? { errors } : {}) },
    { status, headers: { 'Content-Type': 'application/problem+json' } },
  );
}

async function failure(promise: Promise<unknown>) {
  const error = await promise.then(
    () => undefined,
    (reason: unknown) => reason,
  );
  if (!(error instanceof ApiError)) throw new Error(`expected an ApiError, got ${String(error)}`);
  return error.failure;
}

describe('apiRequest', () => {
  it('should return the parsed body when the response is a success', async () => {
    server.use(http.get('/api/things/1', () => HttpResponse.json({ id: '1', extra: true })));

    await expect(apiRequest('/api/things/1', thing)).resolves.toEqual({ id: '1' });
  });

  it('should send the body as JSON when one is given', async () => {
    let received: unknown;
    server.use(
      http.post('/api/things', async ({ request }) => {
        received = await request.json();
        return HttpResponse.json({ id: '2' }, { status: 201 });
      }),
    );

    await apiRequest('/api/things', thing, { method: 'POST', body: { name: 'Pecan Bottom' } });

    expect(received).toEqual({ name: 'Pecan Bottom' });
  });

  it.each(['POST', 'PUT'] as const)('should send the X-CSRF header when it writes with %s', async (method) => {
    let csrf: string | null = null;
    server.use(
      http.all('/api/things', ({ request }) => {
        csrf = request.headers.get('X-CSRF');
        return HttpResponse.json({ id: '3' });
      }),
    );

    await apiRequest('/api/things', thing, { method, body: { name: 'Pecan Bottom' } });

    expect(csrf).toBe('1');
  });

  it('should send no X-CSRF header when it reads', async () => {
    let csrf: string | null = 'unset';
    server.use(
      http.get('/api/things/1', ({ request }) => {
        csrf = request.headers.get('X-CSRF');
        return HttpResponse.json({ id: '1' });
      }),
    );

    await apiRequest('/api/things/1', thing);

    expect(csrf).toBeNull();
  });

  it('should report each invalid field without its category prefix when the response is a 400', async () => {
    server.use(
      http.post('/api/things', () =>
        problem(400, 'Validation: one or more fields are invalid; correct them and retry.', {
          'landowner.terms': ['Validation: landowner.terms must be honey, fee or free.'],
        }),
      ),
    );

    expect(await failure(apiRequest('/api/things', thing, { method: 'POST', body: {} }))).toEqual({
      kind: 'validation',
      message: 'One or more fields are invalid; correct them and retry.',
      errors: { 'landowner.terms': ['landowner.terms must be honey, fee or free.'] },
    });
  });

  it('should report not found when the response is a 404 problem', async () => {
    server.use(http.get('/api/things/9', () => problem(404, 'NotFound: no yard with that id.')));

    expect(await failure(apiRequest('/api/things/9', thing))).toEqual({
      kind: 'not-found',
      message: 'No yard with that id.',
    });
  });

  it('should report the server as unavailable with its message when the response is a 503 problem', async () => {
    server.use(
      http.get('/api/things', () =>
        problem(503, 'Unavailable: no signed-in account: sign-in is not set up on this server; nothing can be read or written.'),
      ),
    );

    expect(await failure(apiRequest('/api/things', thing))).toEqual({
      kind: 'unavailable',
      message: 'No signed-in account: sign-in is not set up on this server; nothing can be read or written.',
    });
  });

  it('should report the server as unreachable when the response is not a problem', async () => {
    server.use(http.get('/api/things', () => new HttpResponse('<html>Bad Gateway</html>', { status: 502 })));

    expect(await failure(apiRequest('/api/things', thing))).toEqual({ kind: 'unreachable' });
  });

  it('should report the server as unreachable when the request never gets a response', async () => {
    server.use(http.get('/api/things', () => HttpResponse.error()));

    expect(await failure(apiRequest('/api/things', thing))).toEqual({ kind: 'unreachable' });
  });

  it('should report an unexpected failure when the response is a 500 problem', async () => {
    server.use(http.get('/api/things', () => problem(500, 'An error occurred while processing your request.')));

    expect(await failure(apiRequest('/api/things', thing))).toEqual({ kind: 'unexpected', status: 500 });
  });

  it('should report an unexpected failure when a success body does not match the contract', async () => {
    server.use(http.get('/api/things/1', () => HttpResponse.json({ name: 'no id' })));

    expect(await failure(apiRequest('/api/things/1', thing))).toEqual({ kind: 'unexpected', status: 200 });
  });

  it('should let an abort through untouched when the caller cancels', async () => {
    server.use(http.get('/api/things', () => HttpResponse.json({ id: '1' })));
    const controller = new AbortController();
    controller.abort();

    await expect(apiRequest('/api/things', thing, { signal: controller.signal })).rejects.toHaveProperty(
      'name',
      'AbortError',
    );
  });
});

describe('fetchAllPages', () => {
  it('should follow nextCursor until the last page when there are several', async () => {
    const seen: (string | null)[] = [];
    server.use(
      http.get('/api/things', ({ request }) => {
        const url = new URL(request.url);
        const cursor = url.searchParams.get('cursor');
        seen.push(url.searchParams.get('limit'));
        return HttpResponse.json(
          cursor === null
            ? { items: [{ id: '1' }], nextCursor: 'c2' }
            : { items: [{ id: '2' }], nextCursor: null },
        );
      }),
    );

    await expect(fetchAllPages('/api/things', thing)).resolves.toEqual([{ id: '1' }, { id: '2' }]);
    expect(seen).toEqual(['100', '100']);
  });
});

describe('fetchPage', () => {
  it('should ask for one page of the given size from the given cursor when a cursor is passed', async () => {
    let asked: URLSearchParams | undefined;
    server.use(
      http.get('/api/things', ({ request }) => {
        asked = new URL(request.url).searchParams;
        return HttpResponse.json({ items: [{ id: '3' }], nextCursor: 'c4' });
      }),
    );

    await expect(fetchPage('/api/things', thing, { limit: 20, cursor: 'c3' })).resolves.toEqual({
      items: [{ id: '3' }],
      nextCursor: 'c4',
    });
    expect(asked?.get('limit')).toBe('20');
    expect(asked?.get('cursor')).toBe('c3');
  });

  it('should send no cursor when it reads the first page', async () => {
    let asked: URLSearchParams | undefined;
    server.use(
      http.get('/api/things', ({ request }) => {
        asked = new URL(request.url).searchParams;
        return HttpResponse.json({ items: [], nextCursor: null });
      }),
    );

    await fetchPage('/api/things', thing, { limit: 20, cursor: null });

    expect(asked?.has('cursor')).toBe(false);
  });
});
