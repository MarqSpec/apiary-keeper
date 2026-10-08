import { describe, expect, it } from 'vitest';
import { ApiError } from '../api/client';
import { shouldRetry } from './query';

describe('shouldRetry', () => {
  it('should retry when the server could not be reached and attempts remain', () => {
    expect(shouldRetry(0, new ApiError({ kind: 'unreachable' }))).toBe(true);
  });

  it('should stop when the server could not be reached and two retries are spent', () => {
    expect(shouldRetry(2, new ApiError({ kind: 'unreachable' }))).toBe(false);
  });

  it('should not retry when the server answered with a failure', () => {
    expect(shouldRetry(0, new ApiError({ kind: 'unavailable', message: 'No account is set up.' }))).toBe(false);
  });

  it('should not retry when the error is not from the API', () => {
    expect(shouldRetry(0, new Error('bug'))).toBe(false);
  });
});
