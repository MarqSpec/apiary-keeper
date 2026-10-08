import { describe, expect, it } from 'vitest';
import { lineageEvent } from '../test/fixtures';
import { describeEvent, type Names } from './describeEvent';

const names: Names = {
  colony: (id) => ({ 'colony-1': 'A-01', 'colony-2': 'A-02', 'colony-3': 'A-03' })[id],
  yard: (id) => ({ 'yard-1': 'Pecan Bottom', 'yard-2': 'Creek Yard' })[id],
};

describe('describeEvent', () => {
  it('should say where the colony was registered when it was registered', () => {
    const event = lineageEvent({ id: 'e1', type: 'registered', occurredOn: '2026-04-01', yardId: 'yard-1' });

    expect(describeEvent(event, 'colony-1', names)).toEqual({ title: 'Registered', detail: 'In Pecan Bottom' });
  });

  it('should say where the swarm was hived when the colony is a swarm capture', () => {
    const event = lineageEvent({ id: 'e1', type: 'swarm-capture', occurredOn: '2026-04-01', yardId: 'yard-2' });

    expect(describeEvent(event, 'colony-1', names)).toEqual({ title: 'Swarm captured', detail: 'In Creek Yard' });
  });

  it('should name the new colony when the split is seen from its parent', () => {
    const event = lineageEvent({
      id: 'e2',
      type: 'split',
      occurredOn: '2026-05-01',
      colonyId: 'colony-1',
      relatedColonyId: 'colony-3',
      yardId: 'yard-1',
    });

    expect(describeEvent(event, 'colony-1', names)).toEqual({
      title: 'Split',
      detail: 'Split off A-03',
      related: 'colony-3',
    });
  });

  it('should name the parent when the split is seen from the new colony', () => {
    const event = lineageEvent({
      id: 'e2',
      type: 'split',
      occurredOn: '2026-05-01',
      colonyId: 'colony-1',
      relatedColonyId: 'colony-3',
      yardId: 'yard-2',
    });

    expect(describeEvent(event, 'colony-3', names)).toEqual({
      title: 'Split from A-01',
      detail: 'In Creek Yard',
      related: 'colony-1',
    });
  });

  it('should name the colony it went into when the merge is seen from the merged colony', () => {
    const event = lineageEvent({
      id: 'e3',
      type: 'merge',
      occurredOn: '2026-06-01',
      colonyId: 'colony-2',
      relatedColonyId: 'colony-1',
    });

    expect(describeEvent(event, 'colony-2', names)).toEqual({
      title: 'Merged',
      detail: 'Into A-01',
      related: 'colony-1',
    });
  });

  it('should name the colony merged in when the merge is seen from the colony that took it', () => {
    const event = lineageEvent({
      id: 'e3',
      type: 'merge',
      occurredOn: '2026-06-01',
      colonyId: 'colony-2',
      relatedColonyId: 'colony-1',
    });

    expect(describeEvent(event, 'colony-1', names)).toEqual({
      title: 'Merge',
      detail: 'A-02 merged into this colony',
      related: 'colony-2',
    });
  });

  it('should say a new queen went in when the colony was requeened', () => {
    const event = lineageEvent({ id: 'e4', type: 'requeen', occurredOn: '2026-07-01', queenId: 'queen-2' });

    expect(describeEvent(event, 'colony-1', names)).toEqual({ title: 'Requeened', detail: 'New queen introduced' });
  });

  it('should name both yards when the colony moved', () => {
    const event = lineageEvent({
      id: 'e5',
      type: 'move',
      occurredOn: '2026-08-01',
      fromYardId: 'yard-1',
      yardId: 'yard-2',
    });

    expect(describeEvent(event, 'colony-1', names)).toEqual({
      title: 'Moved',
      detail: 'From Pecan Bottom to Creek Yard',
    });
  });

  it('should fall back to plain words when a yard or colony name is not known', () => {
    const event = lineageEvent({
      id: 'e5',
      type: 'move',
      occurredOn: '2026-08-01',
      fromYardId: 'yard-9',
      yardId: null,
    });

    expect(describeEvent(event, 'colony-1', names).detail).toBe('From another yard to another yard');
  });

  it('should call a colony unknown when its tag is not known', () => {
    const event = lineageEvent({
      id: 'e2',
      type: 'split',
      occurredOn: '2026-05-01',
      colonyId: 'colony-1',
      relatedColonyId: 'colony-9',
    });

    expect(describeEvent(event, 'colony-1', names).detail).toBe('Split off an unknown colony');
  });
});
