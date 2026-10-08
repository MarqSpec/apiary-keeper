// The inspection endpoints (FR-7): read for the colony's history, recorded by the capture screen.
// reference: INTERFACES.md D.1 *Inspection endpoints*
import { z } from 'zod';
import { apiRequest, fetchPage, type Page } from './client';

/**
 * A finding's value is one of the catalogue's four kinds — a flag, a count, a choice or a list of choices. Which
 * fields exist is the API's catalogue (`InspectionFields.All`), not the PWA's: any field name is accepted and shown.
 */
const findingSchema = z.union([z.boolean(), z.number().int(), z.string(), z.array(z.string())]);

export const inspectionSchema = z.object({
  id: z.string(),
  colonyId: z.string(),
  inspectedOn: z.iso.date(),
  recordedAt: z.string(),
  findings: z.record(z.string(), findingSchema),
  notes: z.string().nullable(),
});

export type Inspection = z.infer<typeof inspectionSchema>;
export type Finding = z.infer<typeof findingSchema>;

/** How many inspections the colony screen reads at a time. */
export const inspectionPageSize = 20;

/** One page of the colony's inspections, by day inspected, newest first; `cursor` null for the first. */
export function listInspectionsPage(
  colonyId: string,
  cursor: string | null,
  signal?: AbortSignal,
): Promise<Page<Inspection>> {
  return fetchPage(`/api/colonies/${encodeURIComponent(colonyId)}/inspections`, inspectionSchema, {
    limit: inspectionPageSize,
    cursor,
    signal,
  });
}

/** An inspection as the capture screen records it: a day, the findings looked at and free text (FR-7). */
export interface InspectionInput {
  readonly inspectedOn: string;
  /** Only the fields looked at: the API takes a left-out field as not looked at, and a `null` the same. */
  readonly findings: Readonly<Record<string, Finding>>;
  readonly notes: string | null;
}

/** Records an inspection of colony `colonyId`; it is recorded once and never changed. */
export function recordInspection(colonyId: string, input: InspectionInput): Promise<Inspection> {
  return apiRequest(`/api/colonies/${encodeURIComponent(colonyId)}/inspections`, inspectionSchema, {
    method: 'POST',
    body: input,
  });
}
