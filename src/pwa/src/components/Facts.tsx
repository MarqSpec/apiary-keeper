import Box from '@mui/material/Box';
import Typography from '@mui/material/Typography';
import type { ReactNode } from 'react';

/** A record's fields as a definition list, label over value; a field with no value is left out. */
export function Facts({ facts }: { readonly facts: readonly (readonly [string, ReactNode])[] }) {
  return (
    <Box component="dl" sx={{ m: 0, display: 'grid', gap: 1.5 }}>
      {facts
        .filter(([, value]) => value !== null && value !== undefined && value !== '')
        .map(([label, value]) => (
          <div key={label}>
            <Typography component="dt" variant="body2" color="text.secondary">
              {label}
            </Typography>
            <Typography component="dd" sx={{ m: 0, whiteSpace: 'pre-line' }}>
              {value}
            </Typography>
          </div>
        ))}
    </Box>
  );
}
