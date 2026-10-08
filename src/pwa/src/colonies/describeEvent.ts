import type { ColonyEvent } from '../api/colonies';

/** Looks up the names people know records by: a colony's tag id and a yard's name; undefined when not known. */
export interface Names {
  readonly colony: (id: string) => string | undefined;
  readonly yard: (id: string) => string | undefined;
}

/** One lineage event in words, as seen from one colony's history, and the other colony it involves, if any. */
export interface EventDescription {
  readonly title: string;
  readonly detail: string;
  readonly related?: string;
}

/**
 * Describes a lineage event (FR-6) from the point of view of `colonyId`. A split and a merge appear in both colonies'
 * histories (INTERFACES.md D.1), so each side reads it its own way.
 */
export function describeEvent(event: ColonyEvent, colonyId: string, names: Names): EventDescription {
  const yard = (id: string | null) => (id === null ? undefined : names.yard(id)) ?? 'another yard';
  const colony = (id: string | null) => (id === null ? undefined : names.colony(id)) ?? 'an unknown colony';
  const other = (id: string | null) => (id === null ? {} : { related: id });

  switch (event.type) {
    case 'registered':
      return { title: 'Registered', detail: `In ${yard(event.yardId)}` };
    case 'swarm-capture':
      return { title: 'Swarm captured', detail: `In ${yard(event.yardId)}` };
    case 'split':
      return event.colonyId === colonyId
        ? { title: 'Split', detail: `Split off ${colony(event.relatedColonyId)}`, ...other(event.relatedColonyId) }
        : { title: `Split from ${colony(event.colonyId)}`, detail: `In ${yard(event.yardId)}`, related: event.colonyId };
    case 'merge':
      return event.colonyId === colonyId
        ? { title: 'Merged', detail: `Into ${colony(event.relatedColonyId)}`, ...other(event.relatedColonyId) }
        : { title: 'Merge', detail: `${colony(event.colonyId)} merged into this colony`, related: event.colonyId };
    case 'requeen':
      return { title: 'Requeened', detail: 'New queen introduced' };
    case 'move':
      return { title: 'Moved', detail: `From ${yard(event.fromYardId)} to ${yard(event.yardId)}` };
    default: {
      const unreachable: never = event.type;
      return unreachable;
    }
  }
}
