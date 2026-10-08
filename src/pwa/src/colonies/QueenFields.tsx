import MenuItem from '@mui/material/MenuItem';
import Stack from '@mui/material/Stack';
import TextField from '@mui/material/TextField';
import { markColours } from '../api/colonies';
import { queenPaths, type QueenValues } from './queenValues';

/** Source, mark and introduction day of a queen; `errors` are the API's, keyed by the paths `queenPaths(prefix)` names. */
export function QueenFields({
  idPrefix,
  values,
  onChange,
  errors,
  errorPrefix,
}: {
  readonly idPrefix: string;
  readonly values: QueenValues;
  readonly onChange: (values: QueenValues) => void;
  readonly errors: Readonly<Record<string, readonly string[]>>;
  readonly errorPrefix: string;
}) {
  const paths = queenPaths(errorPrefix);
  const field = (name: keyof QueenValues) => {
    const message = errors[paths[name]]?.[0];
    return {
      id: `${idPrefix}-${name}`,
      value: values[name],
      onChange: (event: { target: { value: string } }) => onChange({ ...values, [name]: event.target.value }),
      error: message !== undefined,
      helperText: message,
      fullWidth: true,
    };
  };

  return (
    <Stack spacing={2}>
      <TextField label="Queen source" required placeholder="Breeder, raised here, swarm" {...field('source')} />
      <Stack direction="row" spacing={2}>
        <TextField label="Mark colour" select {...field('markColour')}>
          <MenuItem value="">Unmarked</MenuItem>
          {markColours.map((colour) => (
            <MenuItem key={colour} value={colour}>
              {colour.charAt(0).toUpperCase() + colour.slice(1)}
            </MenuItem>
          ))}
        </TextField>
        <TextField
          label="Mark year"
          type="number"
          slotProps={{ htmlInput: { inputMode: 'numeric' } }}
          {...field('markYear')}
        />
      </Stack>
      <TextField
        label="Introduced on"
        required
        type="date"
        slotProps={{ inputLabel: { shrink: true } }}
        {...field('introducedOn')}
      />
    </Stack>
  );
}
