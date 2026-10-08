import { screen, waitFor, within } from '@testing-library/react';
import { http, HttpResponse } from 'msw';
import { describe, expect, it } from 'vitest';
import type { ColonyInput } from '../api/colonies';
import { colonyA01, creekYard, noAccount, pecanBottom, problem, stubApi } from '../test/fixtures';
import { renderScreen } from '../test/render';
import { server } from '../test/server';
import { ChangeNoticeView } from '../components/ChangeNotice';
import { ColonyForm } from './ColonyForm';

type User = ReturnType<typeof renderScreen>['user'];

async function choose(user: User, label: string, option: string) {
  await user.click(screen.getByRole('combobox', { name: label }));
  await user.click(within(screen.getByRole('listbox')).getByRole('option', { name: option }));
}

async function fillColony(user: User) {
  await user.type(await screen.findByLabelText(/^Tag ID/), 'A-05');
  await user.type(screen.getByLabelText(/^Hive number/), 'H-105');
  await user.type(screen.getByLabelText(/^Hive type/), 'Langstroth 10-frame');
  await user.type(screen.getByLabelText(/^Boxes/), '2');
  const day = screen.getByLabelText(/^Date/);
  await user.clear(day);
  await user.type(day, '2026-05-01');
}

/** Answers `path` with a new colony and records what was posted. */
function acceptAt(path: string) {
  const sent: { body?: ColonyInput } = {};
  server.use(
    http.post(path, async ({ request }) => {
      sent.body = (await request.json()) as ColonyInput;
      return HttpResponse.json({ ...colonyA01, id: 'colony-5', tagId: 'A-05' }, { status: 201 });
    }),
  );
  return sent;
}

describe('ColonyForm', () => {
  it('should register the colony in the chosen yard and open it when the form is saved', async () => {
    stubApi({ yards: [creekYard, pecanBottom] });
    const sent = acceptAt('/api/colonies');
    const { user } = renderScreen(<ColonyForm kind="registered" yardId={null} />, '#/colonies/new');

    await fillColony(user);
    await choose(user, 'Yard', 'Pecan Bottom');
    await user.click(screen.getByRole('button', { name: 'Save colony' }));

    await waitFor(() => expect(window.location.hash).toBe('#/colonies/colony-5'));
    expect(sent.body).toEqual({
      tagId: 'A-05',
      hiveNumber: 'H-105',
      yardId: 'yard-1',
      hiveType: 'Langstroth 10-frame',
      boxCount: 2,
      occurredOn: '2026-05-01',
      queen: null,
    });
  });

  it('should carry the warning to the new colony when the API records it with one', async () => {
    stubApi({ yards: [pecanBottom] });
    server.use(
      http.post('/api/colonies', () =>
        HttpResponse.json(
          { ...colonyA01, id: 'colony-5', tagId: 'A-05', warnings: { occurredOn: ['Warning: occurredOn is odd.'] } },
          { status: 201 },
        ),
      ),
    );
    const { user } = renderScreen(
      <>
        <ColonyForm kind="registered" yardId="yard-1" />
        <ChangeNoticeView colonyId="colony-5" />
      </>,
      '#/colonies/new',
    );

    await fillColony(user);
    await user.click(screen.getByRole('button', { name: 'Save colony' }));

    await waitFor(() => expect(window.location.hash).toBe('#/colonies/colony-5'));
    expect(await screen.findByRole('alert')).toHaveTextContent('occurredOn is odd.');
  });

  it('should start in the yard it was opened from when a yard is given', async () => {
    stubApi({ yards: [creekYard, pecanBottom] });
    renderScreen(<ColonyForm kind="registered" yardId="yard-2" />, '#/colonies/new?yard=yard-2');

    expect(await screen.findByRole('combobox', { name: 'Yard' })).toHaveTextContent('Creek Yard');
  });

  it('should record a swarm capture when the form is for a swarm', async () => {
    stubApi({ yards: [pecanBottom] });
    const sent = acceptAt('/api/colonies/swarm-captures');
    const { user } = renderScreen(<ColonyForm kind="swarm-capture" yardId="yard-1" />, '#/colonies/swarm');

    expect(await screen.findByRole('heading', { name: 'Capture swarm' })).toBeInTheDocument();
    await fillColony(user);
    await user.click(screen.getByRole('button', { name: 'Save colony' }));

    await waitFor(() => expect(sent.body?.tagId).toBe('A-05'));
  });

  it('should send the queen with her mark when the queen is known', async () => {
    stubApi({ yards: [pecanBottom] });
    const sent = acceptAt('/api/colonies');
    const { user } = renderScreen(<ColonyForm kind="registered" yardId="yard-1" />, '#/colonies/new');

    await fillColony(user);
    await user.click(screen.getByRole('checkbox', { name: 'The queen is known' }));
    await user.type(screen.getByLabelText(/^Queen source/), 'Sample Breeder');
    await choose(user, 'Mark colour', 'Yellow');
    await user.type(screen.getByLabelText(/^Mark year/), '2026');
    await user.click(screen.getByRole('button', { name: 'Save colony' }));

    await waitFor(() =>
      expect(sent.body?.queen).toEqual({
        source: 'Sample Breeder',
        markColour: 'yellow',
        markYear: 2026,
        introducedOn: '2026-05-01',
      }),
    );
  });

  it('should split the new colony from its parent, in the parent yard by default, when the form is a split', async () => {
    stubApi({ yards: [creekYard, pecanBottom], colonies: [colonyA01] });
    const sent = acceptAt('/api/colonies/colony-1/splits');
    const { user } = renderScreen(<ColonyForm kind="split" parentId="colony-1" />, '#/colonies/colony-1/split');

    expect(await screen.findByRole('heading', { name: 'Split from A-01' })).toBeInTheDocument();
    await fillColony(user);
    await user.click(screen.getByRole('button', { name: 'Save colony' }));

    await waitFor(() => expect(sent.body?.yardId).toBe('yard-1'));
  });

  it('should show the API message on the tag field when the tag is taken', async () => {
    stubApi({ yards: [pecanBottom] });
    server.use(
      http.post('/api/colonies', () =>
        problem(400, 'Validation: one or more fields are invalid; correct them and retry.', {
          tagId: ['Validation: tagId A-05 is already used by another colony.'],
        }),
      ),
    );
    const { user } = renderScreen(<ColonyForm kind="registered" yardId="yard-1" />, '#/colonies/new');

    await fillColony(user);
    await user.click(screen.getByRole('button', { name: 'Save colony' }));

    expect(await screen.findByLabelText(/^Tag ID/)).toHaveAccessibleDescription(
      'tagId A-05 is already used by another colony.',
    );
  });

  it('should explain the server is not ready when the yards cannot be read', async () => {
    server.use(http.get('/api/yards', noAccount));
    renderScreen(<ColonyForm kind="registered" yardId={null} />, '#/colonies/new');

    expect(await screen.findByRole('heading', { name: 'The server is not ready' })).toBeInTheDocument();
  });
});
