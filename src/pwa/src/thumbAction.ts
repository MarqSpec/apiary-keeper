import type { SxProps, Theme } from '@mui/material/styles';

/** The bottom navigation's height: the main content keeps this much clear below it. */
export const bottomNavHeight = 56;

/**
 * A screen's main action, floating above the bottom navigation on the right, where a right thumb reaches it with
 * the phone held in one hand (NFR-3).
 */
export const thumbAction: SxProps<Theme> = {
  position: 'fixed',
  right: 16,
  bottom: `calc(${bottomNavHeight + 16}px + env(safe-area-inset-bottom))`,
};
