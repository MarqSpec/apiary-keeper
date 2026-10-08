// The first reading toward NFR-2 (a median under 60 s to log an inspection): how long the capture screen was open
// before the family saved, kept on the phone until readings travel with the synced record (Phase 1, FR-9).
// reference: NFR-2 (time to log an inspection: the median of these readings)

/** One saved capture: when, and how long from opening the screen to tapping Save and to everything uploaded. */
export interface TimingReading {
  readonly at: string;
  readonly openToSaveSeconds: number;
  readonly openToSavedSeconds: number;
}

export const readingsKey = 'apiary-keeper.capture-readings';
export const maxReadings = 50;

/** The readings kept on this phone, oldest first; none when there are none or the browser holds something else. */
export function readReadings(storage: Pick<Storage, 'getItem'>): TimingReading[] {
  try {
    const parsed: unknown = JSON.parse(storage.getItem(readingsKey) ?? '[]');
    if (!Array.isArray(parsed)) return [];
    return parsed.filter(
      (r): r is TimingReading =>
        typeof r === 'object' &&
        r !== null &&
        typeof (r as TimingReading).at === 'string' &&
        typeof (r as TimingReading).openToSaveSeconds === 'number' &&
        typeof (r as TimingReading).openToSavedSeconds === 'number',
    );
  } catch {
    return [];
  }
}

/** Keeps `reading` with the newest `maxReadings`; a browser that refuses storage loses the reading, never the save. */
export function recordReading(storage: Pick<Storage, 'getItem' | 'setItem'>, reading: TimingReading): void {
  try {
    storage.setItem(readingsKey, JSON.stringify([...readReadings(storage), reading].slice(-maxReadings)));
  } catch {
    // Storage is a convenience here: the inspection is already saved.
  }
}

/** The median of `seconds`, the mean of the middle two when there is an even number; null when there are none. */
export function medianSeconds(seconds: readonly number[]): number | null {
  if (seconds.length === 0) return null;
  const sorted = seconds.toSorted((a, b) => a - b);
  const middle = Math.floor(sorted.length / 2);
  return sorted.length % 2 === 1 ? (sorted[middle] ?? null) : ((sorted[middle - 1] ?? 0) + (sorted[middle] ?? 0)) / 2;
}
