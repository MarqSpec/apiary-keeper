import { ThemeProvider } from '@mui/material/styles';
import { render, screen } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { afterEach, describe, expect, it } from 'vitest';
import { honeyTheme } from '../theme/honeyTheme';
import { prefersDark } from '../test/setup';
import { ColorSchemeToggle } from './ColorSchemeToggle';

function renderToggle() {
  return render(
    <ThemeProvider theme={honeyTheme} defaultMode="system">
      <ColorSchemeToggle />
    </ThemeProvider>,
  );
}

const scheme = () => document.documentElement.getAttribute('data-mui-scheme');

describe('ColorSchemeToggle', () => {
  afterEach(() => {
    prefersDark(false);
  });

  it('should follow the device when it prefers light and nothing was chosen', () => {
    prefersDark(false);
    renderToggle();

    expect(screen.getByRole('button', { name: 'Switch to dark theme' })).toBeInTheDocument();
  });

  it('should follow the device when it prefers dark and nothing was chosen', () => {
    prefersDark(true);
    renderToggle();

    expect(screen.getByRole('button', { name: 'Switch to light theme' })).toBeInTheDocument();
  });

  it('should switch to dark when it is pressed in the light theme', async () => {
    prefersDark(false);
    renderToggle();

    await userEvent.click(screen.getByRole('button', { name: 'Switch to dark theme' }));

    expect(screen.getByRole('button', { name: 'Switch to light theme' })).toBeInTheDocument();
    expect(scheme()).toBe('dark');
  });

  it('should override a dark device when it is pressed', async () => {
    prefersDark(true);
    renderToggle();

    await userEvent.click(screen.getByRole('button', { name: 'Switch to light theme' }));

    expect(screen.getByRole('button', { name: 'Switch to dark theme' })).toBeInTheDocument();
    expect(scheme()).toBe('light');
  });

  it('should remember the choice when the app is opened again', async () => {
    prefersDark(false);
    const { unmount } = renderToggle();
    await userEvent.click(screen.getByRole('button', { name: 'Switch to dark theme' }));
    unmount();

    renderToggle();

    expect(screen.getByRole('button', { name: 'Switch to light theme' })).toBeInTheDocument();
  });
});
