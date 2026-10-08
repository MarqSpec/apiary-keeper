import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { ThemeProvider } from '@mui/material/styles';
import { render } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import type { ReactElement } from 'react';
import { ChangeNoticeProvider } from '../components/ChangeNotice';
import { honeyTheme } from '../theme/honeyTheme';

/**
 * Renders a screen as the app does — the honey theme, a fresh query client and the change notices — at `hash`, with a
 * user to drive it.
 */
export function renderScreen(ui: ReactElement, hash = '#/') {
  window.location.hash = hash;
  const client = new QueryClient({ defaultOptions: { queries: { retry: false }, mutations: { retry: false } } });
  // No pause between keystrokes: the screens' forms are long, and the suite runs its files in parallel.
  const user = userEvent.setup({ delay: null });
  const view = render(
    <ThemeProvider theme={honeyTheme}>
      <QueryClientProvider client={client}>
        <ChangeNoticeProvider>{ui}</ChangeNoticeProvider>
      </QueryClientProvider>
    </ThemeProvider>,
  );
  return { ...view, user, client };
}
