// The inspection fields the capture screen asks about: the drafted set of INTERFACES.md D.1 *Inspection
// endpoints*. The API's catalogue (`InspectionFields.All`) is the authority; the read side of the PWA holds no copy of
// it, but the form must draw something, so this is the one place the capture screen names the fields. A name the API
// no longer holds comes back as a 400 keyed `findings.<name>` and is shown above the form.
// reference: INTERFACES.md D.1 *Inspection endpoints*
import type { Finding } from '../api/inspections';

export interface Option {
  readonly value: string;
  readonly label: string;
}

export type FieldGroup =
  | { readonly kind: 'flag'; readonly name: string; readonly label: string }
  | { readonly kind: 'count'; readonly name: string; readonly label: string; readonly max: number }
  | {
      readonly kind: 'choice' | 'multi';
      readonly name: string;
      readonly label: string;
      readonly options: readonly Option[];
    };

const options = (...values: string[]): Option[] =>
  values.map((value) => ({ value, label: value.charAt(0).toUpperCase() + value.slice(1).replaceAll('-', ' ') }));

/** In the catalogue's order, which is the order the screen asks. */
export const fieldGroups: readonly FieldGroup[] = [
  { kind: 'flag', name: 'queenSeen', label: 'Queen seen' },
  { kind: 'flag', name: 'eggsSeen', label: 'Eggs seen' },
  { kind: 'count', name: 'broodFrames', label: 'Frames of brood', max: 100 },
  { kind: 'choice', name: 'broodPattern', label: 'Brood pattern', options: options('solid', 'spotty', 'none') },
  { kind: 'choice', name: 'foodStores', label: 'Food stores', options: options('none', 'low', 'adequate', 'plenty') },
  { kind: 'choice', name: 'temperament', label: 'Temperament', options: options('calm', 'nervous', 'defensive') },
  {
    kind: 'multi',
    name: 'diseaseSigns',
    label: 'Disease signs',
    options: options(
      'deformed-wings',
      'sunken-cappings',
      'ropy-brood',
      'mummified-brood',
      'small-hive-beetle',
      'wax-moth',
      'varroa-seen',
      'dysentery',
    ),
  },
  {
    kind: 'multi',
    name: 'actionsTaken',
    label: 'Actions taken',
    options: options('fed', 'added-box', 'removed-box', 'swapped-frames', 'equalized', 'treated', 'other'),
  },
];

/** What has been tapped so far; a field never tapped is absent, so it is never guessed. */
export interface Answers {
  /** A flag is `'yes'` or `'no'`; a choice is one of its options. */
  readonly single: Readonly<Record<string, string>>;
  readonly lists: Readonly<Record<string, readonly string[]>>;
  readonly counts: Readonly<Record<string, number>>;
}

export const blankAnswers: Answers = { single: {}, lists: {}, counts: {} };

/** `answers` with `name` set to `value`, or taken back when `value` is null. */
export function withAnswer<K extends keyof Answers>(
  answers: Answers,
  part: K,
  name: string,
  value: Answers[K][string] | null,
): Answers {
  const { [name]: _removed, ...rest } = answers[part] as Record<string, unknown>;
  return { ...answers, [part]: value === null ? rest : { ...rest, [name]: value } };
}

/** The findings the API takes: only the fields that were answered, in the order of the fields. */
export function answersOf(answers: Answers): Record<string, Finding> {
  const findings: Record<string, Finding> = {};
  for (const field of fieldGroups) {
    const single = answers.single[field.name];
    const list = answers.lists[field.name];
    const count = answers.counts[field.name];
    if (field.kind === 'flag' && single !== undefined) findings[field.name] = single === 'yes';
    else if (field.kind === 'choice' && single !== undefined) findings[field.name] = single;
    else if (field.kind === 'multi' && list !== undefined && list.length > 0) findings[field.name] = [...list];
    else if (field.kind === 'count' && count !== undefined) findings[field.name] = count;
  }
  return findings;
}

/** An inspection that says nothing is refused by the API; the screen says so before asking. */
export function hasAnything(findings: Readonly<Record<string, Finding>>, notes: string): boolean {
  return Object.keys(findings).length > 0 || notes !== '';
}
