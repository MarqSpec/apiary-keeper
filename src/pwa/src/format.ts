// How the screens write the API's values for people: days, labels and queen marks.
import type { Colony, Queen } from './api/colonies';

const dayFormat = new Intl.DateTimeFormat('en-US', { dateStyle: 'medium', timeZone: 'UTC' });

/** A `yyyy-MM-dd` day as people read it (`Apr 1, 2026`), the same day wherever the phone is. */
export function formatDay(iso: string): string {
  return dayFormat.format(new Date(`${iso}T00:00:00Z`));
}

/** The phone's own calendar day as `yyyy-MM-dd`: the default for a day the family records. */
export function todayIso(now: Date = new Date()): string {
  const pad = (n: number) => String(n).padStart(2, '0');
  return `${now.getFullYear()}-${pad(now.getMonth() + 1)}-${pad(now.getDate())}`;
}

/** A queen's mark in words (FR-5). */
export function queenMark(queen: Pick<Queen, 'markColour' | 'markYear'>): string {
  if (queen.markColour === null) return 'Unmarked';
  return queen.markYear === null ? `Marked ${queen.markColour}` : `Marked ${queen.markColour}, ${queen.markYear}`;
}

export const statusLabels: Readonly<Record<Colony['status'], string>> = {
  active: 'Active',
  'dead-out': 'Dead-out',
  merged: 'Merged',
  sold: 'Sold',
};

export const termsLabels: Readonly<Record<string, string>> = { honey: 'Honey', fee: 'Fee', free: 'Free' };
