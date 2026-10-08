import { http, HttpResponse } from 'msw';
import { describe, expect, it } from 'vitest';
import { problem } from '../test/fixtures';
import { server } from '../test/server';
import { inspectionSchema, recordInspection } from './inspections';

const recorded = {
  id: 'i1',
  colonyId: 'colony-1',
  inspectedOn: '2026-05-20',
  recordedAt: '2026-05-20T15:00:00+00:00',
  findings: { queenSeen: true, broodFrames: 6, foodStores: 'adequate', diseaseSigns: ['varroa-seen'], actionsTaken: [] },
  notes: null,
};

// reference: INTERFACES.md D.1 *Inspection endpoints*
describe('inspectionSchema', () => {
  it('should accept a flag, a count, a choice and a list of choices when they are the findings', () => {
    expect(inspectionSchema.safeParse(recorded).success).toBe(true);
  });

  it('should accept a field it has never heard of when its value is one of the four kinds', () => {
    expect(inspectionSchema.safeParse({ ...recorded, findings: { supersOn: 2 } }).success).toBe(true);
  });

  it('should refuse a finding that is none of the four kinds when the API changes the shape', () => {
    expect(inspectionSchema.safeParse({ ...recorded, findings: { queenSeen: { yes: true } } }).success).toBe(false);
    expect(inspectionSchema.safeParse({ ...recorded, findings: { queenSeen: null } }).success).toBe(false);
    expect(inspectionSchema.safeParse({ ...recorded, findings: { broodFrames: 6.5 } }).success).toBe(false);
  });

  it('should refuse an inspection day that is not a yyyy-MM-dd date', () => {
    expect(inspectionSchema.safeParse({ ...recorded, inspectedOn: '20 May 2026' }).success).toBe(false);
  });

  it('should refuse an inspection without the field notes, which is null when there are none', () => {
    const { notes: _notes, ...withoutNotes } = recorded;
    expect(inspectionSchema.safeParse(withoutNotes).success).toBe(false);
  });
});

describe('recordInspection', () => {
  it('should post the day, findings and notes and read the inspection back when it is recorded', async () => {
    let sent: unknown;
    server.use(
      http.post('/api/colonies/c%2F1/inspections', async ({ request }) => {
        sent = await request.json();
        return HttpResponse.json(recorded, { status: 201 });
      }),
    );

    const result = await recordInspection('c/1', { inspectedOn: '2026-05-20', findings: { queenSeen: true }, notes: null });

    expect(sent).toEqual({ inspectedOn: '2026-05-20', findings: { queenSeen: true }, notes: null });
    expect(result.id).toBe('i1');
  });

  it('should fail with the field errors when the API refuses the day', async () => {
    server.use(
      http.post('/api/colonies/c1/inspections', () =>
        problem(400, 'Validation: inspectedOn is later than today.', { inspectedOn: ['Validation: later than today.'] }),
      ),
    );

    await expect(recordInspection('c1', { inspectedOn: '2999-01-01', findings: {}, notes: null })).rejects.toMatchObject({
      failure: { kind: 'validation', errors: { inspectedOn: ['later than today.'] } },
    });
  });
});
