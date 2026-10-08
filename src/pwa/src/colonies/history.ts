// A colony's history as one timeline, newest first: its lineage and its inspections (FR-6, FR-7).
import type { ColonyEvent } from '../api/colonies';
import type { Inspection } from '../api/inspections';

export type HistoryEntry =
  | { readonly kind: 'lineage'; readonly id: string; readonly event: ColonyEvent }
  | { readonly kind: 'inspection'; readonly id: string; readonly inspection: Inspection };

const dayOf = (entry: HistoryEntry) => (entry.kind === 'lineage' ? entry.event.occurredOn : entry.inspection.inspectedOn);
const recordedOf = (entry: HistoryEntry) =>
  entry.kind === 'lineage' ? entry.event.recordedAt : entry.inspection.recordedAt;

/** True when `a` belongs above `b`: a later day, or the same day recorded later. */
function isNewer(a: HistoryEntry, b: HistoryEntry): boolean {
  const day = dayOf(a).localeCompare(dayOf(b));
  return day !== 0 ? day > 0 : Date.parse(recordedOf(a)) > Date.parse(recordedOf(b));
}

/**
 * Merges the lineage and the inspections read so far, each already newest first, keeping each one's own order. While
 * older inspections are still to be read (`complete` false), lineage older than the oldest one read is held back, so
 * the timeline never skips an inspection between two events.
 */
export function historyOf(
  events: readonly ColonyEvent[],
  inspections: readonly Inspection[],
  complete: boolean,
): HistoryEntry[] {
  const oldest = inspections.at(-1)?.inspectedOn;
  const lineage: HistoryEntry[] = events
    .filter((event) => complete || oldest === undefined || event.occurredOn >= oldest)
    .map((event) => ({ kind: 'lineage', id: event.id, event }));
  const inspected: HistoryEntry[] = inspections.map((inspection) => ({
    kind: 'inspection',
    id: inspection.id,
    inspection,
  }));

  const merged: HistoryEntry[] = [];
  let l = 0;
  let i = 0;
  while (l < lineage.length || i < inspected.length) {
    const next = lineage[l];
    const other = inspected[i];
    if (other === undefined || (next !== undefined && !isNewer(other, next))) {
      if (next !== undefined) merged.push(next);
      l += 1;
    } else {
      merged.push(other);
      i += 1;
    }
  }
  return merged;
}
