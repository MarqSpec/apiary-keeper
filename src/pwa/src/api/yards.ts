// The yard endpoints (FR-1, FR-2). reference: INTERFACES.md D.1 *Yard endpoints*
import { z } from 'zod';
import { apiRequest, fetchAllPages } from './client';

export const landownerTerms = ['honey', 'fee', 'free'] as const;

export const yardSchema = z.object({
  id: z.string(),
  name: z.string(),
  location: z.object({ latitude: z.number(), longitude: z.number() }),
  state: z.string(),
  county: z.string(),
  accessNotes: z.string().nullable(),
  forageNotes: z.string().nullable(),
  landowner: z.object({
    contact: z.string(),
    terms: z.enum(landownerTerms),
    nearbyCrops: z.string().nullable(),
    sprayContact: z.string().nullable(),
  }),
});

/** A yard and its host landowner, as the API returns it. */
export type Yard = z.infer<typeof yardSchema>;

/** A yard as the PWA writes it to create or replace one; the API checks every field. */
export interface YardInput {
  readonly name: string;
  readonly location: { readonly latitude: number | null; readonly longitude: number | null };
  readonly state: string;
  readonly county: string;
  readonly accessNotes: string;
  readonly forageNotes: string;
  readonly landowner: {
    readonly contact: string;
    readonly terms: string;
    readonly nearbyCrops: string;
    readonly sprayContact: string;
  };
}

/** Every yard of the account, by name. */
export function listYards(signal?: AbortSignal): Promise<Yard[]> {
  return fetchAllPages('/api/yards', yardSchema, signal);
}

export function getYard(id: string, signal?: AbortSignal): Promise<Yard> {
  return apiRequest(`/api/yards/${encodeURIComponent(id)}`, yardSchema, { signal });
}

export function createYard(input: YardInput): Promise<Yard> {
  return apiRequest('/api/yards', yardSchema, { method: 'POST', body: input });
}

export function updateYard(id: string, input: YardInput): Promise<Yard> {
  return apiRequest(`/api/yards/${encodeURIComponent(id)}`, yardSchema, { method: 'PUT', body: input });
}
