import CircularProgress from '@mui/material/CircularProgress';
import Stack from '@mui/material/Stack';

/** Shown while a screen waits for the API; `label` names what it waits for. */
export function Loading({ label }: { readonly label: string }) {
  return (
    <Stack sx={{ alignItems: 'center', py: 4 }}>
      <CircularProgress aria-label={label} />
    </Stack>
  );
}
