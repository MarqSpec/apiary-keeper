import { describe, expect, it } from 'vitest';
import { formatDay, queenMark, todayIso } from './format';

describe('formatDay', () => {
  it('should show the calendar day the API sent when it is given a day', () => {
    expect(formatDay('2026-04-01')).toBe('Apr 1, 2026');
  });
});

describe('todayIso', () => {
  it('should be the local calendar day when the clock is late in the evening', () => {
    expect(todayIso(new Date(2026, 9, 3, 23, 30))).toBe('2026-10-03');
  });
});

describe('queenMark', () => {
  it('should name the colour and the year when she is marked', () => {
    expect(queenMark({ markColour: 'yellow', markYear: 2026 })).toBe('Marked yellow, 2026');
  });

  it('should name the colour alone when no year is recorded', () => {
    expect(queenMark({ markColour: 'blue', markYear: null })).toBe('Marked blue');
  });

  it('should say unmarked when no colour is recorded', () => {
    expect(queenMark({ markColour: null, markYear: null })).toBe('Unmarked');
  });
});
