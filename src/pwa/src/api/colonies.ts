// The colony endpoints (FR-4, FR-5, FR-6): reads and appends only, the history is append-only (D-2).
// reference: INTERFACES.md D.1 *Colony endpoints*
import { z } from 'zod';
import { apiRequest, fetchAllPages } from './client';

export const colonyStatuses = ['active', 'dead-out', 'merged', 'sold'] as const;
export const colonyOrigins = ['registered', 'swarm-capture', 'split'] as const;
export const markColours = ['white', 'yellow', 'red', 'green', 'blue'] as const;
export const colonyEventTypes = ['registered', 'swarm-capture', 'split', 'merge', 'requeen', 'move'] as const;

const day = z.iso.date();

export const queenSchema = z.object({
  id: z.string(),
  source: z.string(),
  markColour: z.enum(markColours).nullable(),
  markYear: z.number().int().nullable(),
  introducedOn: day,
  replacedOn: day.nullable(),
  replacedByQueenId: z.string().nullable(),
});

export const colonySchema = z.object({
  id: z.string(),
  tagId: z.string(),
  hiveNumber: z.string(),
  yardId: z.string(),
  hiveType: z.string(),
  boxCount: z.number().int(),
  status: z.enum(colonyStatuses),
  origin: z.enum(colonyOrigins),
  parentColonyId: z.string().nullable(),
  mergedIntoColonyId: z.string().nullable(),
  queen: queenSchema.nullable(),
  // Only on a change's result, and only when there is something to say; absent on every read.
  warnings: z.record(z.string(), z.array(z.string())).optional(),
});

export const colonyEventSchema = z.object({
  id: z.string(),
  type: z.enum(colonyEventTypes),
  colonyId: z.string(),
  relatedColonyId: z.string().nullable(),
  occurredOn: day,
  recordedAt: z.string(),
  yardId: z.string().nullable(),
  fromYardId: z.string().nullable(),
  queenId: z.string().nullable(),
  replacedQueenId: z.string().nullable(),
});

export type Queen = z.infer<typeof queenSchema>;
export type Colony = z.infer<typeof colonySchema>;
export type ColonyEvent = z.infer<typeof colonyEventSchema>;

/** A queen as the PWA records her when she is introduced (FR-5). */
export interface QueenInput {
  readonly source: string;
  readonly markColour: string | null;
  readonly markYear: number | null;
  readonly introducedOn: string;
}

/** A colony as the PWA records it when it is registered, captured or split off (FR-4). */
export interface ColonyInput {
  readonly tagId: string;
  readonly hiveNumber: string;
  readonly yardId: string;
  readonly hiveType: string;
  readonly boxCount: number | null;
  readonly occurredOn: string;
  readonly queen: QueenInput | null;
}

const colonyPath = (id: string) => `/api/colonies/${encodeURIComponent(id)}`;

/** Every colony of the account, by tag id, with its current queen. */
export function listColonies(signal?: AbortSignal): Promise<Colony[]> {
  return fetchAllPages('/api/colonies', colonySchema, signal);
}

export function getColony(id: string, signal?: AbortSignal): Promise<Colony> {
  return apiRequest(colonyPath(id), colonySchema, { signal });
}

/** The colony's lineage, oldest first. */
export function listColonyEvents(id: string, signal?: AbortSignal): Promise<ColonyEvent[]> {
  return fetchAllPages(`${colonyPath(id)}/events`, colonyEventSchema, signal);
}

/** The colony's queens by introduction day, newest first; the current one is the queen with no `replacedOn`. */
export function listQueens(id: string, signal?: AbortSignal): Promise<Queen[]> {
  return fetchAllPages(`${colonyPath(id)}/queens`, queenSchema, signal);
}

export function registerColony(input: ColonyInput): Promise<Colony> {
  return apiRequest('/api/colonies', colonySchema, { method: 'POST', body: input });
}

export function captureSwarm(input: ColonyInput): Promise<Colony> {
  return apiRequest('/api/colonies/swarm-captures', colonySchema, { method: 'POST', body: input });
}

/** Splits a new colony from `parentId`; resolves to the new colony. */
export function splitColony(parentId: string, input: ColonyInput): Promise<Colony> {
  return apiRequest(`${colonyPath(parentId)}/splits`, colonySchema, { method: 'POST', body: input });
}

/** Merges colony `id` into another; resolves to the merged colony. */
export function mergeColony(id: string, input: { intoColonyId: string; occurredOn: string }): Promise<Colony> {
  return apiRequest(`${colonyPath(id)}/merges`, colonySchema, { method: 'POST', body: input });
}

export function requeenColony(id: string, queen: QueenInput): Promise<Colony> {
  return apiRequest(`${colonyPath(id)}/requeens`, colonySchema, { method: 'POST', body: queen });
}

export function moveColony(id: string, input: { yardId: string; occurredOn: string }): Promise<Colony> {
  return apiRequest(`${colonyPath(id)}/moves`, colonySchema, { method: 'POST', body: input });
}
