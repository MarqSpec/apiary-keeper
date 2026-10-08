import { screen, within } from '@testing-library/react';
import { http } from 'msw';
import { describe, expect, it } from 'vitest';
import { colonyA01, colonyB01, creekYard, mergedA02, noAccount, pecanBottom, stubApi } from '../test/fixtures';
import { renderScreen } from '../test/render';
import { server } from '../test/server';
import { ColonyList } from './ColonyList';

describe('ColonyList', () => {
  it('should list every colony with its yard, active first, when colonies are recorded', async () => {
    stubApi({ yards: [pecanBottom, creekYard], colonies: [colonyA01, mergedA02, colonyB01] });
    renderScreen(<ColonyList />, '#/colonies');

    const rows = within(await screen.findByRole('list', { name: 'Hives' })).getAllByRole('link');
    expect(rows.map((row) => row.getAttribute('href'))).toEqual([
      '#/colonies/colony-1',
      '#/colonies/colony-4',
      '#/colonies/colony-2',
    ]);
    expect(rows[1]).toHaveTextContent('Creek Yard');
  });

  it('should invite the first colony when none is recorded', async () => {
    stubApi({ yards: [pecanBottom], colonies: [] });
    renderScreen(<ColonyList />, '#/colonies');

    expect(await screen.findByText('No hives recorded yet.')).toBeInTheDocument();
    expect(screen.getByRole('link', { name: 'Register colony' })).toHaveAttribute('href', '#/colonies/new');
  });

  it('should explain the server is not ready when the server has no sign-in (so no account)', async () => {
    server.use(http.get('/api/colonies', noAccount), http.get('/api/yards', noAccount));
    renderScreen(<ColonyList />, '#/colonies');

    expect(await screen.findByRole('heading', { name: 'The server is not ready' })).toBeInTheDocument();
  });
});
