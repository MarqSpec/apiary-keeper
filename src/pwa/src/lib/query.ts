// The PWA's wrapper of @tanstack/react-query (CONVENTIONS.md §3): screens import it from here.
import { QueryClient } from '@tanstack/react-query';
import { ApiError } from '../api/client';

export { QueryClientProvider, useInfiniteQuery, useMutation, useQuery, useQueryClient } from '@tanstack/react-query';

/**
 * Retries only a call that never reached the API, at most twice. An answer from the API — a 503 included — is shown
 * as it is, so the screen says why at once instead of after a silent wait.
 */
export function shouldRetry(failureCount: number, error: unknown): boolean {
  return error instanceof ApiError && error.failure.kind === 'unreachable' && failureCount < 2;
}

/** The app's query client: server state is fresh for 30 seconds and refetched when the app regains focus. */
export function createQueryClient(): QueryClient {
  return new QueryClient({
    defaultOptions: {
      queries: { retry: shouldRetry, staleTime: 30_000 },
      mutations: { retry: false },
    },
  });
}

/** The query keys, one place, so a write invalidates exactly what it changed. */
export const queryKeys = {
  yards: ['yards'] as const,
  yard: (id: string) => ['yards', id] as const,
  colonies: ['colonies'] as const,
  colony: (id: string) => ['colonies', id] as const,
  colonyEvents: (id: string) => ['colonies', id, 'events'] as const,
  queens: (id: string) => ['colonies', id, 'queens'] as const,
  inspections: (id: string) => ['colonies', id, 'inspections'] as const,
};
