import { describe, expect, it } from 'vitest';
import type { Inspection } from '../api/inspections';
import { lineageEvent } from '../test/fixtures';
import { historyOf } from './history';

const inspection = (id: string, inspectedOn: string, recordedAt = `${inspectedOn}T15:00:00+00:00`): Inspection => ({
  id,
  colonyId: 'colony-1',
  inspectedOn,
  recordedAt,
  findings: {},
  notes: null,
});

// Newest first, as the screen holds them.
const events = [
  lineageEvent({ id: 'e3', type: 'move', occurredOn: '2026-06-01' }),
  lineageEvent({ id: 'e2', type: 'split', occurredOn: '2026-05-01' }),
  lineageEvent({ id: 'e1', type: 'registered', occurredOn: '2026-04-01' }),
];

const ids = (entries: ReturnType<typeof historyOf>) => entries.map((entry) => entry.id);

describe('historyOf', () => {
  it('should interleave inspections and lineage newest first when every inspection is loaded', () => {
    const inspections = [inspection('i2', '2026-05-15'), inspection('i1', '2026-04-10')];

    expect(ids(historyOf(events, inspections, true))).toEqual(['e3', 'i2', 'e2', 'i1', 'e1']);
  });

  it('should hold back lineage older than the oldest loaded inspection when older inspections are still to load', () => {
    const inspections = [inspection('i2', '2026-05-15')];

    expect(ids(historyOf(events, inspections, false))).toEqual(['e3', 'i2']);
  });

  it('should keep lineage on the oldest loaded inspection day when older inspections are still to load', () => {
    const inspections = [inspection('i2', '2026-05-01', '2026-05-01T18:00:00+00:00')];

    expect(ids(historyOf(events, inspections, false))).toEqual(['e3', 'i2', 'e2']);
  });

  it('should order a day by when each was recorded, newest first, when an inspection and an event share it', () => {
    const before = [inspection('i0', '2026-05-01', '2026-05-01T06:00:00+00:00')];

    expect(ids(historyOf(events, before, true))).toEqual(['e3', 'e2', 'i0', 'e1']);
  });

  it('should show every lineage event when the colony has no inspections', () => {
    expect(ids(historyOf(events, [], true))).toEqual(['e3', 'e2', 'e1']);
  });
});
