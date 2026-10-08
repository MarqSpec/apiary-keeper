import Box from '@mui/material/Box';
import Button from '@mui/material/Button';
import IconButton from '@mui/material/IconButton';
import Stack from '@mui/material/Stack';
import ToggleButton from '@mui/material/ToggleButton';
import Typography from '@mui/material/Typography';
import type { ReactNode } from 'react';
import type { Option } from './fields';

const yesNo: readonly Option[] = [
  { value: 'yes', label: 'Yes' },
  { value: 'no', label: 'No' },
];

function Labelled({ label, children }: { readonly label: string; readonly children: ReactNode }) {
  return (
    <Box component="fieldset" sx={{ border: 0, p: 0, m: 0, minWidth: 0 }}>
      <Typography variant="subtitle1" component="legend" sx={{ mb: 0.5, p: 0 }}>
        {label}
      </Typography>
      <Stack direction="row" useFlexGap sx={{ flexWrap: 'wrap', gap: 1 }}>
        {children}
      </Stack>
    </Box>
  );
}

const chip = { flex: '1 1 0', minWidth: 96 } as const;

/** One answer of several, yes or no unless told otherwise; tapping the chosen one again takes the answer back. */
export function ChoiceField({
  label,
  options = yesNo,
  value,
  onChange,
}: {
  readonly label: string;
  readonly options?: readonly Option[];
  readonly value: string | null;
  readonly onChange: (value: string | null) => void;
}) {
  return (
    <Labelled label={label}>
      {options.map((option) => (
        <ToggleButton
          key={option.value}
          value={option.value}
          selected={value === option.value}
          onChange={() => onChange(value === option.value ? null : option.value)}
          sx={chip}
        >
          {option.label}
        </ToggleButton>
      ))}
    </Labelled>
  );
}

/** Any of several; none tapped leaves the field out. */
export function MultiChoiceField({
  label,
  options,
  value,
  onChange,
}: {
  readonly label: string;
  readonly options: readonly Option[];
  readonly value: readonly string[];
  readonly onChange: (value: string[]) => void;
}) {
  return (
    <Labelled label={label}>
      {options.map((option) => (
        <ToggleButton
          key={option.value}
          value={option.value}
          selected={value.includes(option.value)}
          onChange={() =>
            onChange(value.includes(option.value) ? value.filter((v) => v !== option.value) : [...value, option.value])
          }
          sx={chip}
        >
          {option.label}
        </ToggleButton>
      ))}
    </Labelled>
  );
}

const stepper = { border: 1, borderColor: 'divider', minWidth: 56, minHeight: 56 } as const;

/** A count with plus and minus: never guessed, so it starts as not counted. */
export function BroodFrames({
  label,
  max,
  value,
  onChange,
}: {
  readonly label: string;
  readonly max: number;
  readonly value: number | null;
  readonly onChange: (value: number | null) => void;
}) {
  return (
    <div>
      <Typography variant="subtitle1" component="span" sx={{ display: 'block', mb: 0.5 }}>
        {label}
      </Typography>
      <Stack direction="row" spacing={1} sx={{ alignItems: 'center' }}>
        <IconButton
          aria-label="Fewer brood frames"
          disabled={value === null || value <= 0}
          onClick={() => onChange(value === null ? null : Math.max(0, value - 1))}
          sx={stepper}
        >
          -
        </IconButton>
        <Typography data-testid="brood-frames" aria-live="polite" variant="h6" sx={{ minWidth: 96, textAlign: 'center' }}>
          {value === null ? 'Not counted' : value}
        </Typography>
        <IconButton
          aria-label="More brood frames"
          disabled={value !== null && value >= max}
          onClick={() => onChange(Math.min(max, (value ?? 0) + 1))}
          sx={stepper}
        >
          +
        </IconButton>
        {value !== null && (
          <Button aria-label="Clear brood frames" onClick={() => onChange(null)}>
            Clear
          </Button>
        )}
      </Stack>
    </div>
  );
}
