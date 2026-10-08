import { screen, within } from '@testing-library/react';
import { http } from 'msw';
import { describe, expect, it } from 'vitest';
import { colonyA01, colonyA03, colonyB01, mergedA02, noAccount, pecanBottom, stubApi } from '../test/fixtures';
import { renderScreen } from '../test/render';
import { server } from '../test/server';
import { YardDetail } from './YardDetail';

describe('YardDetail', () => {
  it('should show the yard and its landowner when the yard exists', async () => {
    stubApi({ yards: [pecanBottom] });
    renderScreen(<YardDetail yardId="yard-1" />, '#/yards/yard-1');

    expect(await screen.findByRole('heading', { name: 'Pecan Bottom' })).toBeInTheDocument();
    expect(screen.getByText('Example County, TX')).toBeInTheDocument();
    expect(screen.getByText('Sample Landowner, 555-0100')).toBeInTheDocument();
    expect(screen.getByText('Honey')).toBeInTheDocument();
    expect(screen.getByText('Gate code is on the family chat')).toBeInTheDocument();
    expect(screen.getByText('Huisache in spring')).toBeInTheDocument();
    expect(screen.getByText('30.1234, -97.5678')).toBeInTheDocument();
  });

  it('should list only the colonies in this yard, active first, when colonies are recorded', async () => {
    stubApi({ yards: [pecanBottom], colonies: [mergedA02, colonyA01, colonyB01, colonyA03] });
    renderScreen(<YardDetail yardId="yard-1" />, '#/yards/yard-1');

    const hives = await screen.findByRole('list', { name: 'Hives' });
    const links = within(hives).getAllByRole('link');
    expect(links.map((link) => link.getAttribute('href'))).toEqual([
      '#/colonies/colony-1',
      '#/colonies/colony-3',
      '#/colonies/colony-2',
    ]);
    expect(links[2]).toHaveTextContent('Merged');
  });

  it('should say there are no hives when none is recorded in the yard', async () => {
    stubApi({ yards: [pecanBottom], colonies: [colonyB01] });
    renderScreen(<YardDetail yardId="yard-1" />, '#/yards/yard-1');

    expect(await screen.findByText('No hives recorded in this yard yet.')).toBeInTheDocument();
  });

  it('should link to edit the yard and to add a colony to it when the yard is shown', async () => {
    stubApi({ yards: [pecanBottom] });
    renderScreen(<YardDetail yardId="yard-1" />, '#/yards/yard-1');

    expect(await screen.findByRole('link', { name: 'Edit yard' })).toHaveAttribute('href', '#/yards/yard-1/edit');
    expect(screen.getByRole('link', { name: 'Register colony' })).toHaveAttribute(
      'href',
      '#/colonies/new?yard=yard-1',
    );
    expect(screen.getByRole('link', { name: 'Capture swarm' })).toHaveAttribute(
      'href',
      '#/colonies/swarm?yard=yard-1',
    );
  });

  it('should say the yard is not there when the id is unknown', async () => {
    stubApi({ yards: [] });
    renderScreen(<YardDetail yardId="yard-404" />, '#/yards/yard-404');

    expect(await screen.findByRole('heading', { name: 'Not found' })).toBeInTheDocument();
    expect(screen.getByText('No yard with that id.')).toBeInTheDocument();
  });

  it('should explain the server is not ready when the server has no sign-in (so no account)', async () => {
    server.use(http.get('/api/yards/:id', noAccount), http.get('/api/colonies', noAccount));
    renderScreen(<YardDetail yardId="yard-1" />, '#/yards/yard-1');

    expect(await screen.findByRole('heading', { name: 'The server is not ready' })).toBeInTheDocument();
  });
});
