import { ApiError } from './client';

/** The API's message for each invalid field of a failed save, keyed by JSON path; empty unless it was a 400. */
export function fieldErrorsOf(error: unknown): Readonly<Record<string, readonly string[]>> {
  return error instanceof ApiError && error.failure.kind === 'validation' ? error.failure.errors : {};
}
