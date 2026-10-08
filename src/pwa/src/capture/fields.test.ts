import { describe, expect, it } from 'vitest';
import { answersOf, blankAnswers, fieldGroups, hasAnything, withAnswer } from './fields';

// reference: INTERFACES.md D.1 *Inspection endpoints* (the drafted field set)
describe('answersOf', () => {
  it('should send nothing when no field was answered', () => {
    expect(answersOf(blankAnswers)).toEqual({});
  });

  it('should send a flag as a boolean, a choice as its value and a count as a number', () => {
    const answers = {
      single: { queenSeen: 'no', foodStores: 'plenty' },
      lists: {},
      counts: { broodFrames: 0 },
    };

    expect(answersOf(answers)).toEqual({ queenSeen: false, foodStores: 'plenty', broodFrames: 0 });
  });

  it('should leave a list out when none of its choices is selected', () => {
    expect(answersOf({ single: {}, lists: { diseaseSigns: [] }, counts: {} })).toEqual({});
  });

  it('should send the findings in the order of the fields when answered out of order', () => {
    const answers = { single: { foodStores: 'low', queenSeen: 'yes' }, lists: { actionsTaken: ['fed'] }, counts: {} };

    expect(Object.keys(answersOf(answers))).toEqual(['queenSeen', 'foodStores', 'actionsTaken']);
  });
});

describe('withAnswer', () => {
  it('should set an answer without changing the others', () => {
    const first = withAnswer(blankAnswers, 'single', 'queenSeen', 'yes');
    const second = withAnswer(first, 'single', 'eggsSeen', 'no');

    expect(second.single).toEqual({ queenSeen: 'yes', eggsSeen: 'no' });
    expect(first.single).toEqual({ queenSeen: 'yes' });
  });

  it('should take an answer back when its value is null', () => {
    const answers = withAnswer(withAnswer(blankAnswers, 'counts', 'broodFrames', 4), 'counts', 'broodFrames', null);

    expect(answers.counts).toEqual({});
  });
});

describe('hasAnything', () => {
  it('should be true for a finding alone or a note alone and false for neither', () => {
    expect([hasAnything({ queenSeen: true }, ''), hasAnything({}, 'note'), hasAnything({}, '')]).toEqual([true, true, false]);
  });
});

describe('fieldGroups', () => {
  it('should name each field once', () => {
    const names = fieldGroups.map((field) => field.name);

    expect(new Set(names).size).toBe(names.length);
  });
});
