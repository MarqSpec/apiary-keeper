import { screen } from '@testing-library/react';
import { describe, expect, it, vi } from 'vitest';
import { ApiError } from '../api/client';
import { renderScreen } from '../test/render';
import { ProblemView } from './ProblemView';

describe('ProblemView', () => {
  it('should explain the server has no account when the API answers unavailable', () => {
    renderScreen(
      <ProblemView
        error={
          new ApiError({
            kind: 'unavailable',
            message: 'No signed-in account: sign-in is not set up on this server; nothing can be read or written.',
          })
        }
        onRetry={vi.fn()}
      />,
    );

    expect(screen.getByRole('heading', { name: 'The server is not ready' })).toBeInTheDocument();
    expect(
      screen.getByText('No signed-in account: sign-in is not set up on this server; nothing can be read or written.'),
    ).toBeInTheDocument();
  });

  it('should say the record is not there when the API answers not found', () => {
    renderScreen(<ProblemView error={new ApiError({ kind: 'not-found', message: 'No yard with that id.' })} />);

    expect(screen.getByRole('heading', { name: 'Not found' })).toBeInTheDocument();
    expect(screen.getByText('No yard with that id.')).toBeInTheDocument();
  });

  it('should say the server cannot be reached when no answer came', () => {
    renderScreen(<ProblemView error={new ApiError({ kind: 'unreachable' })} onRetry={vi.fn()} />);

    expect(screen.getByRole('heading', { name: 'Cannot reach the server' })).toBeInTheDocument();
  });

  it('should say something went wrong when the failure was unexpected', () => {
    renderScreen(<ProblemView error={new Error('a bug')} />);

    expect(screen.getByRole('heading', { name: 'Something went wrong' })).toBeInTheDocument();
  });

  it('should call onRetry when the user taps try again', async () => {
    const onRetry = vi.fn();
    const { user } = renderScreen(<ProblemView error={new ApiError({ kind: 'unreachable' })} onRetry={onRetry} />);

    await user.click(screen.getByRole('button', { name: 'Try again' }));

    expect(onRetry).toHaveBeenCalledOnce();
  });

  it('should offer no retry when retrying cannot help', () => {
    renderScreen(
      <ProblemView error={new ApiError({ kind: 'not-found', message: 'No yard with that id.' })} onRetry={vi.fn()} />,
    );

    expect(screen.queryByRole('button', { name: 'Try again' })).not.toBeInTheDocument();
  });
});
