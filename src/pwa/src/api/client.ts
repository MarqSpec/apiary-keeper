// The one place the PWA talks to the API: every response is parsed with zod, every failure becomes an ApiFailure.
// reference: INTERFACES.md D.1; CONVENTIONS.md §4.3
import { z } from 'zod';

/** Why a call to the API failed, in the categories the screens explain (CONVENTIONS.md §4.3). */
export type ApiFailure =
  | {
      readonly kind: 'validation';
      readonly message: string;
      /** Each invalid field's messages, keyed by its JSON path (`landowner.terms`). */
      readonly errors: Readonly<Record<string, readonly string[]>>;
    }
  | { readonly kind: 'not-found'; readonly message: string }
  | { readonly kind: 'unavailable'; readonly message: string }
  | { readonly kind: 'unreachable' }
  | { readonly kind: 'unexpected'; readonly status: number };

/** Thrown by every API call that does not succeed; `failure` says why. */
export class ApiError extends Error {
  readonly failure: ApiFailure;

  constructor(failure: ApiFailure) {
    super(`API call failed: ${failure.kind}`);
    this.name = 'ApiError';
    this.failure = failure;
  }
}

const problemSchema = z.object({
  title: z.string().optional(),
  errors: z.record(z.string(), z.array(z.string())).optional(),
});

/** The API prefixes each message with its category (`Validation:`); the screens show the rest, capitalised. */
export function withoutCategory(message: string): string {
  const rest = message.replace(/^[A-Za-z]+:\s*/, '');
  return rest.charAt(0).toUpperCase() + rest.slice(1);
}

/** A message without its category prefix, as written (`Warning: introducedOn …` → `introducedOn …`). */
export function messageWithoutCategory(message: string): string {
  return message.replace(/^[A-Za-z]+:\s*/, '');
}

async function failureOf(response: Response): Promise<ApiFailure> {
  const isProblem = response.headers.get('Content-Type')?.includes('application/problem+json') ?? false;
  // Not a problem from the API: the proxy's 502 page or similar, so the API was never reached.
  if (!isProblem) return { kind: 'unreachable' };

  const body: unknown = await response.json().catch(() => undefined);
  const problem = problemSchema.safeParse(body);
  const message = withoutCategory(problem.data?.title ?? '');
  switch (response.status) {
    case 400:
      return {
        kind: 'validation',
        message,
        errors: Object.fromEntries(
          Object.entries(problem.data?.errors ?? {}).map(([field, messages]) => [field, messages.map(messageWithoutCategory)]),
        ),
      };
    case 404:
      return { kind: 'not-found', message };
    case 503:
      return { kind: 'unavailable', message };
    default:
      return { kind: 'unexpected', status: response.status };
  }
}

/** What a call sends: the method, an optional JSON body and the caller's cancellation. */
export interface ApiRequestInit {
  readonly method?: 'GET' | 'POST' | 'PUT';
  readonly body?: unknown;
  readonly signal?: AbortSignal;
}

/**
 * Calls the API at `path` on this origin and parses the success body with `schema`; throws ApiError otherwise. A write
 * carries `X-CSRF: 1`, which the API requires of any write sent with the sign-in session cookie
 * (INTERFACES.md D; ).
 */
export async function apiRequest<T>(path: string, schema: z.ZodType<T>, init: ApiRequestInit = {}): Promise<T> {
  const method = init.method ?? 'GET';
  const headers: Record<string, string> = { Accept: 'application/json' };
  if (init.body !== undefined) headers['Content-Type'] = 'application/json';
  if (method !== 'GET') headers['X-CSRF'] = '1';

  let response: Response;
  try {
    response = await fetch(new URL(path, window.location.origin), {
      method,
      headers,
      body: init.body === undefined ? undefined : JSON.stringify(init.body),
      signal: init.signal,
    });
  } catch (error) {
    // The caller cancelled: not a failure to explain, so it passes through as the abort it is.
    if (init.signal?.aborted === true) throw error;
    throw new ApiError({ kind: 'unreachable' });
  }

  if (!response.ok) throw new ApiError(await failureOf(response));

  const parsed = schema.safeParse(await response.json().catch(() => undefined));
  if (!parsed.success) throw new ApiError({ kind: 'unexpected', status: response.status });
  return parsed.data;
}

/** The largest page the API serves (INTERFACES.md D.1 *Paging*). */
export const maxPageSize = 100;

/** One page of a list and the cursor for the next, null on the last (INTERFACES.md D.1 *Paging*). */
export interface Page<T> {
  readonly items: T[];
  readonly nextCursor: string | null;
}

/** Reads one page of `limit` items from `cursor`, or the first page when `cursor` is null. */
export function fetchPage<T>(
  path: string,
  item: z.ZodType<T>,
  { limit, cursor, signal }: { readonly limit: number; readonly cursor: string | null; readonly signal?: AbortSignal },
): Promise<Page<T>> {
  const page = z.object({ items: z.array(item), nextCursor: z.string().nullable() });
  const query = new URLSearchParams({ limit: String(limit) });
  if (cursor !== null) query.set('cursor', cursor);
  return apiRequest(`${path}?${query.toString()}`, page, { signal });
}

/** Reads every page of a list, following `nextCursor` until the last page. */
export async function fetchAllPages<T>(path: string, item: z.ZodType<T>, signal?: AbortSignal): Promise<T[]> {
  const items: T[] = [];
  let cursor: string | null = null;
  do {
    const result: Page<T> = await fetchPage(path, item, { limit: maxPageSize, cursor, signal });
    items.push(...result.items);
    cursor = result.nextCursor;
  } while (cursor !== null);
  return items;
}
