import { describe, expect, it } from 'vitest';
import { medianSeconds, readReadings, recordReading, readingsKey, maxReadings } from './captureTiming';

// reference: NFR-2 (time to log an inspection: the median of these readings)
const reading = (n: number) => ({ at: `2026-10-0${n}T12:00:00.000Z`, openToSaveSeconds: n * 10, openToSavedSeconds: n * 10 + 5 });

describe('captureTiming', () => {
  it('should keep the readings under the product-name key when one is recorded', () => {
    recordReading(window.localStorage, reading(1));

    expect(readingsKey).toBe('apiary-keeper.capture-readings');
    expect(window.localStorage.getItem('apiary-keeper.capture-readings')).not.toBeNull();
  });

  it('should keep a reading and read it back when one is recorded', () => {
    recordReading(window.localStorage, reading(1));

    expect(readReadings(window.localStorage)).toEqual([reading(1)]);
  });

  it('should keep only the newest readings when more than the limit are recorded', () => {
    for (let i = 0; i < maxReadings + 5; i++) {
      recordReading(window.localStorage, { at: String(i), openToSaveSeconds: i, openToSavedSeconds: i });
    }

    const kept = readReadings(window.localStorage);
    expect([kept.length, kept[0]?.at, kept.at(-1)?.at]).toEqual([maxReadings, '5', String(maxReadings + 4)]);
  });

  it('should read nothing when the stored value is not readings', () => {
    window.localStorage.setItem(readingsKey, '{"nope":1}');

    expect(readReadings(window.localStorage)).toEqual([]);
  });

  it('should not throw when the browser refuses storage', () => {
    const refusing = {
      getItem: () => {
        throw new Error('blocked');
      },
      setItem: () => {
        throw new Error('blocked');
      },
    } as unknown as Storage;

    expect(() => recordReading(refusing, reading(1))).not.toThrow();
    expect(readReadings(refusing)).toEqual([]);
  });

  it('should take the median of the middle when the count is odd and the mean of the two when even', () => {
    expect(medianSeconds([30, 10, 20])).toBe(20);
    expect(medianSeconds([10, 40, 20, 30])).toBe(25);
    expect(medianSeconds([])).toBeNull();
  });
});
