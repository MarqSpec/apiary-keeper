import { screen } from '@testing-library/react';
import { http } from 'msw';
import { describe, expect, it } from 'vitest';
import { colonyA01, colonyA03, colonyB01, creekYard, mergedA02, noAccount, pecanBottom, stubApi } from '../test/fixtures';
import { renderScreen } from '../test/render';
import { server } from '../test/server';
import { YardList } from './YardList';

describe('YardList', () => {
  it('should link each yard to its detail when there are yards', async () => {
    stubApi({ yards: [creekYard, pecanBottom] });
    renderScreen(<YardList />, '#/yards');

    expect(await screen.findByRole('link', { name: /Pecan Bottom/ })).toHaveAttribute('href', '#/yards/yard-1');
    expect(screen.getByRole('link', { name: /Creek Yard/ })).toHaveAttribute('href', '#/yards/yard-2');
  });

  it('should count only the active hives in each yard when colonies have been recorded', async () => {
    stubApi({ yards: [pecanBottom], colonies: [colonyA01, colonyA03, mergedA02, colonyB01] });
    renderScreen(<YardList />, '#/yards');

    expect(await screen.findByRole('link', { name: /Pecan Bottom/ })).toHaveTextContent(
      'Example County, TX · 2 active hives',
    );
  });

  it('should invite the first yard when there are none', async () => {
    stubApi({ yards: [] });
    renderScreen(<YardList />, '#/yards');

    expect(await screen.findByText('No yards yet. Add the first one.')).toBeInTheDocument();
  });

  it('should offer to add a yard when the list is shown', async () => {
    stubApi({ yards: [] });
    renderScreen(<YardList />, '#/yards');

    expect(await screen.findByRole('link', { name: 'Add yard' })).toHaveAttribute('href', '#/yards/new');
  });

  it('should explain the server is not ready when the server has no sign-in (so no account)', async () => {
    server.use(http.get('/api/yards', noAccount), http.get('/api/colonies', noAccount));
    renderScreen(<YardList />, '#/yards');

    expect(await screen.findByRole('heading', { name: 'The server is not ready' })).toBeInTheDocument();
  });
});
