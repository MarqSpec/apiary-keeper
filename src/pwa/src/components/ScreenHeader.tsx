import IconButton from '@mui/material/IconButton';
import Stack from '@mui/material/Stack';
import SvgIcon from '@mui/material/SvgIcon';
import Typography from '@mui/material/Typography';
import type { ReactNode } from 'react';

const backArrow = 'M20 11H7.8l5.6-5.6L12 4l-8 8 8 8 1.4-1.4L7.8 13H20v-2Z';

/** A screen's title, with a back link to where it sits when it is not a top-level screen. */
export function ScreenHeader({
  title,
  subtitle,
  back,
}: {
  readonly title: string;
  readonly subtitle?: ReactNode;
  readonly back?: { readonly href: string; readonly label: string };
}) {
  return (
    <Stack direction="row" spacing={1} sx={{ alignItems: 'center', mb: 2 }}>
      {back && (
        <IconButton component="a" href={back.href} aria-label={`Back to ${back.label}`} edge="start">
          <SvgIcon>
            <path d={backArrow} />
          </SvgIcon>
        </IconButton>
      )}
      <div>
        <Typography variant="h5" component="h2">
          {title}
        </Typography>
        {subtitle && (
          <Typography variant="body1" color="text.secondary">
            {subtitle}
          </Typography>
        )}
      </div>
    </Stack>
  );
}
