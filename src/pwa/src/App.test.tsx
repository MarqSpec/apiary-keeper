import { act, render, screen } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { beforeEach, describe, expect, it } from 'vitest';
import App from './App';
import { colonyA01, pecanBottom, stubApi } from './test/fixtures';

function at(hash: string) {
  window.location.hash = hash;
  return render(<App />);
}

describe('App', () => {
  beforeEach(() => stubApi({ yards: [pecanBottom], colonies: [colonyA01] }));

  it('should show the app name when it starts', () => {
    at('');

    expect(screen.getByRole('heading', { name: 'Apiary-Keeper' })).toBeInTheDocument();
  });

  it('should offer the theme toggle when it starts', () => {
    at('');

    expect(screen.getByRole('button', { name: /Switch to (dark|light) theme/ })).toBeInTheDocument();
  });

  it('should open on the yards list when no screen is named', async () => {
    at('');

    expect(await screen.findByRole('heading', { name: 'Yards' })).toBeInTheDocument();
  });

  it('should show the screen the fragment names when the app is opened at a deep link', async () => {
    at('#/colonies/colony-1');

    expect(await screen.findByRole('heading', { name: 'A-01' })).toBeInTheDocument();
  });

  it('should follow the fragment when it changes', async () => {
    at('#/yards');
    await screen.findByRole('heading', { name: 'Yards' });

    act(() => {
      window.location.hash = '#/yards/yard-1';
      window.dispatchEvent(new HashChangeEvent('hashchange'));
    });

    expect(await screen.findByRole('heading', { name: 'Pecan Bottom' })).toBeInTheDocument();
  });

  it('should say the screen does not exist when the fragment names none', () => {
    at('#/nowhere');

    expect(screen.getByRole('heading', { name: 'Page not found' })).toBeInTheDocument();
  });

  it('should mark the hives tab as current when a colony is shown', async () => {
    at('#/colonies/colony-1');

    expect(await screen.findByRole('link', { name: 'Hives' })).toHaveAttribute('aria-current', 'page');
    expect(screen.getByRole('link', { name: 'Yards' })).not.toHaveAttribute('aria-current');
  });

  it('should show the capture screen and mark the hives tab when opened at an inspect link', async () => {
    at('#/colonies/colony-1/inspect');

    expect(await screen.findByRole('heading', { name: 'Inspect A-01' })).toBeInTheDocument();
    expect(screen.getByRole('link', { name: 'Hives' })).toHaveAttribute('aria-current', 'page');
  });

  it('should go to the hives list when the hives tab is tapped', async () => {
    at('#/yards');
    const user = userEvent.setup({ delay: null });

    await user.click(screen.getByRole('link', { name: 'Hives' }));

    expect(await screen.findByRole('heading', { name: 'Hives' })).toBeInTheDocument();
  });
});
