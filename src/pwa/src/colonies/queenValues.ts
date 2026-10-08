import type { QueenInput } from '../api/colonies';

/** A queen's fields as typed, before they are sent (FR-5). */
export interface QueenValues {
  readonly source: string;
  readonly markColour: string;
  readonly markYear: string;
  readonly introducedOn: string;
}

export const blankQueen = (introducedOn: string): QueenValues => ({
  source: '',
  markColour: '',
  markYear: '',
  introducedOn,
});

/** The queen as the API takes her: no mark colour or year is null, so she is recorded unmarked. */
export function queenInputOf(values: QueenValues): QueenInput {
  const year = Number.parseInt(values.markYear, 10);
  return {
    source: values.source,
    markColour: values.markColour === '' ? null : values.markColour,
    markYear: Number.isNaN(year) ? null : year,
    introducedOn: values.introducedOn,
  };
}

/** The JSON path each queen field's errors come back under, below `prefix` (`queen.` in a new colony, none in a requeen). */
export const queenPaths = (prefix: string) =>
  ({
    source: `${prefix}source`,
    markColour: `${prefix}markColour`,
    markYear: `${prefix}markYear`,
    introducedOn: `${prefix}introducedOn`,
  }) as const;
