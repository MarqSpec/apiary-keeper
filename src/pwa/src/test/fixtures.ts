// Synthetic records only: invented yards, people and hives.
import { http, HttpResponse } from 'msw';
import type { Colony, ColonyEvent, Queen } from '../api/colonies';
import type { Inspection } from '../api/inspections';
import type { Yard } from '../api/yards';
import { server } from './server';

export const pecanBottom: Yard = {
  id: 'yard-1',
  name: 'Pecan Bottom',
  location: { latitude: 30.1234, longitude: -97.5678 },
  state: 'TX',
  county: 'Example County',
  accessNotes: 'Gate code is on the family chat',
  forageNotes: 'Huisache in spring',
  landowner: {
    contact: 'Sample Landowner, 555-0100',
    terms: 'honey',
    nearbyCrops: 'Cotton',
    sprayContact: null,
  },
};

export const creekYard: Yard = {
  id: 'yard-2',
  name: 'Creek Yard',
  location: { latitude: 30.5, longitude: -97.9 },
  state: 'TX',
  county: 'Other County',
  accessNotes: null,
  forageNotes: null,
  landowner: { contact: 'Test Farmer', terms: 'fee', nearbyCrops: null, sprayContact: null },
};

export const queenA01: Queen = {
  id: 'queen-1',
  source: 'Sample Breeder',
  markColour: 'yellow',
  markYear: 2026,
  introducedOn: '2026-04-01',
  replacedOn: null,
  replacedByQueenId: null,
};

export const colonyA01: Colony = {
  id: 'colony-1',
  tagId: 'A-01',
  hiveNumber: 'H-101',
  yardId: 'yard-1',
  hiveType: 'Langstroth 10-frame',
  boxCount: 2,
  status: 'active',
  origin: 'registered',
  parentColonyId: null,
  mergedIntoColonyId: null,
  queen: queenA01,
};

export const colonyA03: Colony = {
  ...colonyA01,
  id: 'colony-3',
  tagId: 'A-03',
  hiveNumber: 'H-103',
  origin: 'split',
  parentColonyId: 'colony-1',
  queen: null,
};

export const colonyB01: Colony = {
  ...colonyA01,
  id: 'colony-4',
  tagId: 'B-01',
  hiveNumber: 'H-201',
  yardId: 'yard-2',
  queen: null,
};

export const mergedA02: Colony = {
  ...colonyA01,
  id: 'colony-2',
  tagId: 'A-02',
  hiveNumber: 'H-102',
  status: 'merged',
  mergedIntoColonyId: 'colony-1',
  queen: null,
};

/** A lineage event with every optional id empty; tests set what their event carries. */
export function lineageEvent(event: Partial<ColonyEvent> & Pick<ColonyEvent, 'id' | 'type' | 'occurredOn'>): ColonyEvent {
  return {
    colonyId: 'colony-1',
    relatedColonyId: null,
    recordedAt: `${event.occurredOn}T12:00:00+00:00`,
    yardId: null,
    fromYardId: null,
    queenId: null,
    replacedQueenId: null,
    ...event,
  };
}

/** An inspection of colony-1 with nothing found; tests set what theirs recorded. */
export function inspectionOf(inspection: Partial<Inspection> & Pick<Inspection, 'id' | 'inspectedOn'>): Inspection {
  return {
    colonyId: 'colony-1',
    recordedAt: `${inspection.inspectedOn}T15:00:00+00:00`,
    findings: {},
    notes: null,
    ...inspection,
  };
}

const page = <T>(items: readonly T[]) => HttpResponse.json({ items, nextCursor: null });

/** The API as a test needs it: lists and reads answered from the records given. */
export function stubApi(records: {
  yards?: readonly Yard[];
  colonies?: readonly Colony[];
  events?: Readonly<Record<string, readonly ColonyEvent[]>>;
  queens?: Readonly<Record<string, readonly Queen[]>>;
  inspections?: Readonly<Record<string, readonly Inspection[]>>;
}): void {
  const yards = records.yards ?? [];
  const colonies = records.colonies ?? [];
  const notFound = (what: string) =>
    HttpResponse.json(
      { title: `NotFound: no ${what} with that id.`, status: 404 },
      { status: 404, headers: { 'Content-Type': 'application/problem+json' } },
    );
  server.use(
    http.get('/api/yards', () => page(yards)),
    http.get('/api/yards/:id', ({ params }) => {
      const yard = yards.find((y) => y.id === params.id);
      return yard ? HttpResponse.json(yard) : notFound('yard');
    }),
    http.get('/api/colonies', () => page(colonies)),
    http.get('/api/colonies/:id', ({ params }) => {
      const colony = colonies.find((c) => c.id === params.id);
      return colony ? HttpResponse.json(colony) : notFound('colony');
    }),
    http.get('/api/colonies/:id/events', ({ params }) => page(records.events?.[String(params.id)] ?? [])),
    http.get('/api/colonies/:id/queens', ({ params }) => page(records.queens?.[String(params.id)] ?? [])),
    http.get('/api/colonies/:id/inspections', ({ params }) => page(records.inspections?.[String(params.id)] ?? [])),
  );
}

/** A problem-details response, as the API sends one (CONVENTIONS.md §4.3). */
export function problem(status: number, title: string, errors?: Record<string, string[]>) {
  return HttpResponse.json(
    { type: 'about:blank', title, status, ...(errors ? { errors } : {}) },
    { status, headers: { 'Content-Type': 'application/problem+json' } },
  );
}

export const noAccount = () =>
  problem(503, 'Unavailable: no signed-in account: sign-in is not set up on this server; nothing can be read or written.');
