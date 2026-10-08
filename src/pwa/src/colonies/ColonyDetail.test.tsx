import { screen, waitFor, within } from '@testing-library/react';
import { http, HttpResponse } from 'msw';
import { describe, expect, it } from 'vitest';
import type { Colony } from '../api/colonies';
import {
  colonyA01,
  colonyA03,
  creekYard,
  inspectionOf,
  lineageEvent,
  mergedA02,
  noAccount,
  pecanBottom,
  problem,
  queenA01,
  stubApi,
} from '../test/fixtures';
import { renderScreen } from '../test/render';
import { server } from '../test/server';
import { ColonyDetail } from './ColonyDetail';

type User = ReturnType<typeof renderScreen>['user'];

const history = {
  'colony-1': [
    lineageEvent({ id: 'e1', type: 'registered', occurredOn: '2026-04-01', yardId: 'yard-1' }),
    lineageEvent({
      id: 'e2',
      type: 'split',
      occurredOn: '2026-05-01',
      relatedColonyId: 'colony-3',
      yardId: 'yard-1',
    }),
    lineageEvent({ id: 'e3', type: 'move', occurredOn: '2026-06-01', fromYardId: 'yard-1', yardId: 'yard-2' }),
  ],
};

const replaced = {
  ...queenA01,
  id: 'queen-0',
  source: 'Swarm',
  markColour: null,
  markYear: null,
  introducedOn: '2025-05-01',
  replacedOn: '2026-04-01',
};

function stubColonies(colonies: readonly Colony[] = [colonyA01, colonyA03, mergedA02]) {
  stubApi({
    yards: [pecanBottom, creekYard],
    colonies,
    events: history,
    queens: { 'colony-1': [queenA01, replaced] },
  });
}

async function choose(user: User, label: string, option: string) {
  await user.click(screen.getByRole('combobox', { name: label }));
  await user.click(within(screen.getByRole('listbox')).getByRole('option', { name: option }));
}

describe('ColonyDetail', () => {
  it('should show the colony and its current queen when the colony exists', async () => {
    stubColonies();
    renderScreen(<ColonyDetail colonyId="colony-1" />, '#/colonies/colony-1');

    expect(await screen.findByRole('heading', { name: 'A-01' })).toBeInTheDocument();
    expect(screen.getByText('Active')).toBeInTheDocument();
    expect(screen.getByText('Langstroth 10-frame')).toBeInTheDocument();
    const queen = screen.getByRole('region', { name: 'Queen' });
    expect(within(queen).getByText('Sample Breeder')).toBeInTheDocument();
    expect(within(queen).getByText('Marked yellow, 2026')).toBeInTheDocument();
  });

  it('should list its lineage newest first when events are recorded', async () => {
    stubColonies();
    renderScreen(<ColonyDetail colonyId="colony-1" />, '#/colonies/colony-1');

    const items = within(await screen.findByRole('list', { name: 'History' })).getAllByRole('listitem');
    expect(items.map((item) => item.textContent)).toEqual([
      'Jun 1, 2026 · MovedFrom Pecan Bottom to Creek Yard',
      'May 1, 2026 · SplitSplit off A-03',
      'Apr 1, 2026 · RegisteredIn Pecan Bottom',
    ]);
  });

  it('should show the inspections in the history newest first, among the lineage, when some are recorded', async () => {
    stubApi({
      yards: [pecanBottom, creekYard],
      colonies: [colonyA01, colonyA03],
      events: history,
      inspections: {
        'colony-1': [
          inspectionOf({
            id: 'i2',
            inspectedOn: '2026-05-20',
            findings: { queenSeen: true, broodFrames: 6, foodStores: 'adequate', diseaseSigns: [] },
            notes: 'Calm, heavy with nectar',
          }),
          inspectionOf({ id: 'i1', inspectedOn: '2026-04-15', findings: { temperament: 'defensive' } }),
        ],
      },
    });
    renderScreen(<ColonyDetail colonyId="colony-1" />, '#/colonies/colony-1');

    const items = within(await screen.findByRole('list', { name: 'History' })).getAllByRole('listitem');
    await waitFor(() => expect(items).toHaveLength(5));
    expect(items.map((item) => item.textContent)).toEqual([
      'Jun 1, 2026 · MovedFrom Pecan Bottom to Creek Yard',
      'May 20, 2026 · InspectionQueen seen: yes · Brood frames: 6 · Food stores: adequate · Disease signs: noneCalm, heavy with nectar',
      'May 1, 2026 · SplitSplit off A-03',
      'Apr 15, 2026 · InspectionTemperament: defensive',
      'Apr 1, 2026 · RegisteredIn Pecan Bottom',
    ]);
    expect(screen.queryByText('No inspections recorded yet.')).not.toBeInTheDocument();
  });

  it('should read older inspections a page at a time when the family asks for them', async () => {
    const asked: (string | null)[] = [];
    const sizes = new Set<string | null>();
    stubColonies();
    server.use(
      http.get('/api/colonies/colony-1/inspections', ({ request }) => {
        const query = new URL(request.url).searchParams;
        asked.push(query.get('cursor'));
        sizes.add(query.get('limit'));
        return HttpResponse.json(
          query.get('cursor') === null
            ? { items: [inspectionOf({ id: 'i2', inspectedOn: '2026-05-20', notes: 'Newer' })], nextCursor: 'page-2' }
            : { items: [inspectionOf({ id: 'i1', inspectedOn: '2026-04-15', notes: 'Older' })], nextCursor: null },
        );
      }),
    );
    const { user } = renderScreen(<ColonyDetail colonyId="colony-1" />, '#/colonies/colony-1');

    const list = await screen.findByRole('list', { name: 'History' });
    await within(list).findByText('Newer');
    expect(within(list).queryByText('Older')).not.toBeInTheDocument();
    // Lineage older than the oldest inspection read so far waits for the inspections before it.
    expect(within(list).queryByText(/Registered/)).not.toBeInTheDocument();

    await user.click(screen.getByRole('button', { name: 'Show older' }));

    expect(await within(list).findByText('Older')).toBeInTheDocument();
    expect(within(list).getByText(/Registered/)).toBeInTheDocument();
    expect(screen.queryByRole('button', { name: 'Show older' })).not.toBeInTheDocument();
    expect(asked).toEqual([null, 'page-2']);
    expect([...sizes]).toEqual(['20']);
  });

  it('should say the inspections could not be read and still show the lineage when only they fail', async () => {
    stubColonies();
    server.use(http.get('/api/colonies/colony-1/inspections', () => problem(500, 'boom')));
    const { user } = renderScreen(<ColonyDetail colonyId="colony-1" />, '#/colonies/colony-1');

    const section = await screen.findByRole('region', { name: 'History' });
    expect(await within(section).findByRole('heading', { name: 'Something went wrong' })).toBeInTheDocument();
    expect(within(section).getByText(/The inspections could not be read/)).toBeInTheDocument();
    expect(within(screen.getByRole('list', { name: 'History' })).getAllByRole('listitem')).toHaveLength(3);

    server.use(http.get('/api/colonies/colony-1/inspections', () => HttpResponse.json({ items: [], nextCursor: null })));
    await user.click(within(section).getByRole('button', { name: 'Try again' }));
    await waitFor(() => expect(within(section).queryByText(/The inspections could not be read/)).not.toBeInTheDocument());
  });

  it('should say the server is not ready in the history when the inspections answer 503', async () => {
    stubColonies();
    server.use(http.get('/api/colonies/colony-1/inspections', noAccount));
    renderScreen(<ColonyDetail colonyId="colony-1" />, '#/colonies/colony-1');

    const section = await screen.findByRole('region', { name: 'History' });
    expect(await within(section).findByRole('heading', { name: 'The server is not ready' })).toBeInTheDocument();
  });

  it('should show no history list until the inspections have loaded when they are slow', async () => {
    stubColonies();
    let release: () => void = () => undefined;
    const held = new Promise<void>((resolve) => {
      release = resolve;
    });
    server.use(
      http.get('/api/colonies/colony-1/inspections', async () => {
        await held;
        return HttpResponse.json({ items: [], nextCursor: null });
      }),
    );
    renderScreen(<ColonyDetail colonyId="colony-1" />, '#/colonies/colony-1');

    expect(await screen.findByRole('heading', { name: 'History' })).toBeInTheDocument();
    await screen.findByText('Queens');
    expect(screen.getByRole('progressbar', { name: 'Loading the history' })).toBeInTheDocument();
    expect(screen.queryByRole('list', { name: 'History' })).not.toBeInTheDocument();
    release();
    expect(await screen.findByRole('list', { name: 'History' })).toBeInTheDocument();
  });

  it('should say no inspections are recorded yet when the colony has none', async () => {
    stubColonies();
    renderScreen(<ColonyDetail colonyId="colony-1" />, '#/colonies/colony-1');

    expect(await screen.findByText('No inspections recorded yet.')).toBeInTheDocument();
    expect(within(screen.getByRole('list', { name: 'History' })).getAllByRole('listitem')).toHaveLength(3);
  });

  it('should keep the line breaks of an inspection note when it has several lines', async () => {
    stubApi({
      yards: [pecanBottom, creekYard],
      colonies: [colonyA01],
      events: history,
      inspections: {
        'colony-1': [inspectionOf({ id: 'i1', inspectedOn: '2026-05-20', notes: 'Heavy supers\nNeeds a feed' })],
      },
    });
    renderScreen(<ColonyDetail colonyId="colony-1" />, '#/colonies/colony-1');

    const note = await screen.findByText(/Heavy supers/);
    expect(note).toHaveStyle({ whiteSpace: 'pre-wrap' });
    expect(note.textContent).toBe('Heavy supers\nNeeds a feed');
  });

  it('should keep the inspections read and offer the older ones again when only the next page fails', async () => {
    stubColonies();
    let failNext = true;
    server.use(
      http.get('/api/colonies/colony-1/inspections', ({ request }) => {
        if (new URL(request.url).searchParams.get('cursor') === null) {
          return HttpResponse.json({
            items: [inspectionOf({ id: 'i2', inspectedOn: '2026-05-20', notes: 'Newer' })],
            nextCursor: 'page-2',
          });
        }
        if (failNext) return problem(500, 'boom');
        return HttpResponse.json({
          items: [inspectionOf({ id: 'i1', inspectedOn: '2026-04-15', notes: 'Older' })],
          nextCursor: null,
        });
      }),
    );
    const { user } = renderScreen(<ColonyDetail colonyId="colony-1" />, '#/colonies/colony-1');
    const list = await screen.findByRole('list', { name: 'History' });
    await within(list).findByText('Newer');

    await user.click(screen.getByRole('button', { name: 'Show older' }));

    expect(await screen.findByText('Older inspections could not be read.')).toBeInTheDocument();
    expect(within(list).getByText('Newer')).toBeInTheDocument();
    failNext = false;
    await user.click(screen.getByRole('button', { name: 'Try again' }));
    expect(await within(list).findByText('Older')).toBeInTheDocument();
    expect(screen.queryByText('Older inspections could not be read.')).not.toBeInTheDocument();
  });

  it('should fail visibly when the inspections come back in a shape the PWA does not know', async () => {
    stubColonies();
    server.use(
      http.get('/api/colonies/colony-1/inspections', () =>
        HttpResponse.json({
          items: [
            {
              id: 'i1',
              colonyId: 'colony-1',
              inspectedOn: '2026-05-20',
              recordedAt: '2026-05-20T15:00:00Z',
              findings: { queenSeen: { yes: true } },
              notes: null,
            },
          ],
          nextCursor: null,
        }),
      ),
    );
    renderScreen(<ColonyDetail colonyId="colony-1" />, '#/colonies/colony-1');

    const section = await screen.findByRole('region', { name: 'History' });
    expect(await within(section).findByText(/The inspections could not be read/)).toBeInTheDocument();
    expect(within(section).getByRole('heading', { name: 'Something went wrong' })).toBeInTheDocument();
  });

  it('should link a split to the other colony when the history names one', async () => {
    stubColonies();
    renderScreen(<ColonyDetail colonyId="colony-1" />, '#/colonies/colony-1');

    expect(await screen.findByRole('link', { name: /Split off A-03/ })).toHaveAttribute(
      'href',
      '#/colonies/colony-3',
    );
  });

  it('should list every queen with the day she was replaced when she has been', async () => {
    stubColonies();
    renderScreen(<ColonyDetail colonyId="colony-1" />, '#/colonies/colony-1');

    const queens = within(await screen.findByRole('list', { name: 'Queens' })).getAllByRole('listitem');
    expect(queens[1]).toHaveTextContent('Swarm · Unmarked');
    expect(queens[1]).toHaveTextContent('Introduced May 1, 2025 · replaced Apr 1, 2026');
  });

  it('should offer no lineage actions and link the colony it joined when it has been merged', async () => {
    stubColonies();
    renderScreen(<ColonyDetail colonyId="colony-2" />, '#/colonies/colony-2');

    expect(await screen.findByRole('link', { name: 'A-01' })).toHaveAttribute('href', '#/colonies/colony-1');
    expect(screen.queryByRole('button', { name: 'Requeen' })).not.toBeInTheDocument();
    expect(screen.queryByRole('link', { name: 'Split' })).not.toBeInTheDocument();
  });

  it('should link to split it when it is active', async () => {
    stubColonies();
    renderScreen(<ColonyDetail colonyId="colony-1" />, '#/colonies/colony-1');

    expect(await screen.findByRole('link', { name: 'Split' })).toHaveAttribute('href', '#/colonies/colony-1/split');
  });

  it('should link to record an inspection when it is active', async () => {
    stubColonies();
    renderScreen(<ColonyDetail colonyId="colony-1" />, '#/colonies/colony-1');

    expect(await screen.findByRole('link', { name: 'Inspect' })).toHaveAttribute('href', '#/colonies/colony-1/inspect');
  });

  it('should still link to record an inspection when the colony has been merged', async () => {
    stubColonies();
    renderScreen(<ColonyDetail colonyId="colony-2" />, '#/colonies/colony-2');

    // An inspection is recorded for a colony of any status ().
    expect(await screen.findByRole('link', { name: 'Inspect' })).toHaveAttribute('href', '#/colonies/colony-2/inspect');
  });

  it('should record the new queen and show her when the colony is requeened', async () => {
    let current: Colony = colonyA01;
    let sent: unknown;
    stubColonies();
    server.use(
      http.get('/api/colonies/colony-1', () => HttpResponse.json(current)),
      http.post('/api/colonies/colony-1/requeens', async ({ request }) => {
        sent = await request.json();
        current = { ...colonyA01, queen: { ...queenA01, id: 'queen-2', source: 'Raised here' } };
        return HttpResponse.json(current);
      }),
    );
    const { user } = renderScreen(<ColonyDetail colonyId="colony-1" />, '#/colonies/colony-1');

    await user.click(await screen.findByRole('button', { name: 'Requeen' }));
    const dialog = screen.getByRole('dialog', { name: 'Requeen A-01' });
    await user.type(within(dialog).getByLabelText(/^Queen source/), 'Raised here');
    await user.click(within(dialog).getByRole('button', { name: 'Requeen' }));

    await waitFor(() => expect(screen.queryByRole('dialog')).not.toBeInTheDocument());
    expect(sent).toMatchObject({ source: 'Raised here', markColour: null, markYear: null });
    expect(await within(screen.getByRole('region', { name: 'Queen' })).findByText('Raised here')).toBeInTheDocument();
  });

  it('should show the warning and keep the change when the API records a requeen with one', async () => {
    stubColonies();
    server.use(
      http.post('/api/colonies/colony-1/requeens', () =>
        HttpResponse.json({
          ...colonyA01,
          warnings: {
            introducedOn: [
              'Warning: introducedOn 2026-03-01 is before 2026-04-01, the day the queen she replaces was introduced. It was recorded as given; recorded events are never changed.',
            ],
          },
        }),
      ),
    );
    const { user } = renderScreen(<ColonyDetail colonyId="colony-1" />, '#/colonies/colony-1');

    await user.click(await screen.findByRole('button', { name: 'Requeen' }));
    const dialog = screen.getByRole('dialog', { name: 'Requeen A-01' });
    await user.type(within(dialog).getByLabelText(/^Queen source/), 'Raised here');
    await user.click(within(dialog).getByRole('button', { name: 'Requeen' }));

    await waitFor(() => expect(screen.queryByRole('dialog')).not.toBeInTheDocument());
    const notice = await screen.findByRole('alert');
    expect(notice).toHaveTextContent('Recorded, with a warning');
    expect(notice).toHaveTextContent(
      'introducedOn 2026-03-01 is before 2026-04-01, the day the queen she replaces was introduced.',
    );
    expect(notice).not.toHaveTextContent('Warning:');

    await user.click(within(notice).getByRole('button', { name: 'Dismiss' }));
    expect(screen.queryByRole('alert')).not.toBeInTheDocument();
  });

  it('should show no notice when the change carries no warnings', async () => {
    stubColonies();
    server.use(http.post('/api/colonies/colony-1/requeens', () => HttpResponse.json({ ...colonyA01, warnings: {} })));
    const { user } = renderScreen(<ColonyDetail colonyId="colony-1" />, '#/colonies/colony-1');

    await user.click(await screen.findByRole('button', { name: 'Requeen' }));
    const dialog = screen.getByRole('dialog', { name: 'Requeen A-01' });
    await user.type(within(dialog).getByLabelText(/^Queen source/), 'Raised here');
    await user.click(within(dialog).getByRole('button', { name: 'Requeen' }));

    await waitFor(() => expect(screen.queryByRole('dialog')).not.toBeInTheDocument());
    expect(screen.queryByRole('alert')).not.toBeInTheDocument();
  });

  it('should show the API message on the day and keep the dialog open when the requeen is dated in the future', async () => {
    stubColonies();
    server.use(
      http.post('/api/colonies/colony-1/requeens', () =>
        problem(400, 'Validation: one or more fields are invalid.', {
          introducedOn: ['Validation: introducedOn 2099-01-01 is later than today.'],
        }),
      ),
    );
    const { user } = renderScreen(<ColonyDetail colonyId="colony-1" />, '#/colonies/colony-1');

    await user.click(await screen.findByRole('button', { name: 'Requeen' }));
    const dialog = screen.getByRole('dialog', { name: 'Requeen A-01' });
    await user.type(within(dialog).getByLabelText(/^Queen source/), 'Raised here');
    await user.click(within(dialog).getByRole('button', { name: 'Requeen' }));

    const day = await within(dialog).findByLabelText(/^Introduced on/);
    await waitFor(() => expect(day).toHaveAccessibleDescription('introducedOn 2099-01-01 is later than today.'));
    expect(day).toHaveAttribute('aria-invalid', 'true');
    expect(screen.getByRole('dialog', { name: 'Requeen A-01' })).toBeInTheDocument();
  });

  it('should mark the queen with no replacement day as current when a backdated requeen lists her below', async () => {
    const current = { ...queenA01, id: 'queen-2', source: 'Raised here', introducedOn: '2026-03-01' };
    const earlier = { ...queenA01, introducedOn: '2026-04-01', replacedOn: '2026-03-01', replacedByQueenId: 'queen-2' };
    stubApi({
      yards: [pecanBottom, creekYard],
      colonies: [{ ...colonyA01, queen: current }],
      queens: { 'colony-1': [earlier, current] },
    });
    renderScreen(<ColonyDetail colonyId="colony-1" />, '#/colonies/colony-1');

    const queens = within(await screen.findByRole('list', { name: 'Queens' })).getAllByRole('listitem');
    expect(queens[0]).not.toHaveTextContent('Current');
    expect(queens[1]).toHaveTextContent('Raised here · Marked yellow, 2026 · Current');
  });

  it('should move the colony to the chosen yard when a move is saved', async () => {
    let sent: unknown;
    stubColonies();
    server.use(
      http.post('/api/colonies/colony-1/moves', async ({ request }) => {
        sent = await request.json();
        return HttpResponse.json({ ...colonyA01, yardId: 'yard-2' });
      }),
    );
    const { user } = renderScreen(<ColonyDetail colonyId="colony-1" />, '#/colonies/colony-1');

    await user.click(await screen.findByRole('button', { name: 'Move' }));
    await choose(user, 'To yard', 'Creek Yard');
    await user.click(within(screen.getByRole('dialog')).getByRole('button', { name: 'Move' }));

    await waitFor(() => expect(sent).toMatchObject({ yardId: 'yard-2' }));
  });

  it('should offer only the other active colonies when merging', async () => {
    let sent: unknown;
    stubColonies();
    server.use(
      http.post('/api/colonies/colony-1/merges', async ({ request }) => {
        sent = await request.json();
        return HttpResponse.json({ ...colonyA03 });
      }),
    );
    const { user } = renderScreen(<ColonyDetail colonyId="colony-1" />, '#/colonies/colony-1');

    await user.click(await screen.findByRole('button', { name: 'Merge' }));
    await user.click(screen.getByRole('combobox', { name: 'Into colony' }));
    const options = within(screen.getByRole('listbox')).getAllByRole('option');
    expect(options.map((option) => option.textContent)).toEqual(['A-03']);
    await user.click(within(screen.getByRole('listbox')).getByRole('option', { name: 'A-03' }));
    await user.click(within(screen.getByRole('dialog')).getByRole('button', { name: 'Merge' }));

    await waitFor(() => expect(sent).toMatchObject({ intoColonyId: 'colony-3' }));
  });

  it('should keep the dialog open and say to retry when the colony changed during the action', async () => {
    stubColonies();
    server.use(
      http.post('/api/colonies/colony-1/moves', () =>
        problem(503, 'Unavailable: the colony changed while this was being recorded; read it again and retry.'),
      ),
    );
    const { user } = renderScreen(<ColonyDetail colonyId="colony-1" />, '#/colonies/colony-1');

    await user.click(await screen.findByRole('button', { name: 'Move' }));
    await choose(user, 'To yard', 'Creek Yard');
    await user.click(within(screen.getByRole('dialog')).getByRole('button', { name: 'Move' }));

    expect(
      await within(screen.getByRole('dialog')).findByText(
        'The colony changed while this was being recorded; read it again and retry.',
      ),
    ).toBeInTheDocument();
  });

  it('should say the colony is not there when the id is unknown', async () => {
    stubColonies();
    renderScreen(<ColonyDetail colonyId="colony-404" />, '#/colonies/colony-404');

    expect(await screen.findByRole('heading', { name: 'Not found' })).toBeInTheDocument();
  });

  it('should explain the server is not ready when the server has no sign-in (so no account)', async () => {
    server.use(
      http.get('/api/colonies/:id', noAccount),
      http.get('/api/colonies', noAccount),
      http.get('/api/yards', noAccount),
    );
    renderScreen(<ColonyDetail colonyId="colony-1" />, '#/colonies/colony-1');

    expect(await screen.findByRole('heading', { name: 'The server is not ready' })).toBeInTheDocument();
  });
});
