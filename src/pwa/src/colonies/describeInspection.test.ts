import { describe, expect, it } from 'vitest';
import { findingsText, labelOf } from './describeInspection';

describe('labelOf', () => {
  it('should write a field name as words when it is camel case', () => {
    expect(labelOf('queenSeen')).toBe('Queen seen');
    expect(labelOf('broodFrames')).toBe('Brood frames');
    expect(labelOf('temperament')).toBe('Temperament');
  });
});

describe('findingsText', () => {
  it('should write each kind of finding as people read it when the inspection recorded them', () => {
    expect(
      findingsText({
        queenSeen: true,
        eggsSeen: false,
        broodFrames: 6,
        foodStores: 'adequate',
        diseaseSigns: ['deformed-wings', 'varroa-seen'],
        actionsTaken: [],
      }),
    ).toEqual([
      'Queen seen: yes',
      'Eggs seen: no',
      'Brood frames: 6',
      'Food stores: adequate',
      'Disease signs: deformed wings, varroa seen',
      'Actions taken: none',
    ]);
  });

  it('should keep the order the API sent them in when it lists several', () => {
    expect(findingsText({ temperament: 'calm', queenSeen: true })).toEqual(['Temperament: calm', 'Queen seen: yes']);
  });

  it('should show a field it has never heard of when the catalogue gains one', () => {
    expect(findingsText({ supersOn: 2, smellOfBrood: 'sour-smell' })).toEqual([
      'Supers on: 2',
      'Smell of brood: sour smell',
    ]);
  });

  it('should say nothing when no field was filled in', () => {
    expect(findingsText({})).toEqual([]);
  });
});
