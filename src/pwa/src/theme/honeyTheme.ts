// Honey theme for Material UI (v6+), light and dark.
// Starting values from the PRD; regenerate final ones in Material Theme Builder
// from seed #E8A33D and re-check text contrast (4.5:1) before shipping.
// The light background is warmed by hand to tan, rather than Material 3's near-white.
// Moved here when the PWA was created; honeyTheme.test.ts holds every text pair
// to 4.5:1 (NFR-13).
import { createTheme } from '@mui/material/styles';
// Types the CSS-variables theme: colorSchemes, vars, generateStyleSheets.
import type {} from '@mui/material/themeCssVarsAugmentation';

/** NFR-3: the smallest tap target, in CSS pixels, so a gloved thumb can hit it (Material's 48 dp). */
export const minTapTarget = 48;

/** A row in a list, or a tab, is the screen's main target and gets more than the floor. */
const rowHeight = 56;

export const honeyTheme = createTheme({
  // An attribute, not the media query, picks the scheme, so the manual toggle can override the device (D-14).
  cssVariables: { colorSchemeSelector: 'data-mui-scheme' },
  colorSchemes: {
    light: {
      palette: {
        primary: { main: '#8A5100', contrastText: '#FFFFFF' },
        secondary: { main: '#6F5B40', contrastText: '#FFFFFF' },
        success: { main: '#4F6542', contrastText: '#FFFFFF' }, // tertiary: forage green
        error: { main: '#BA1A1A', contrastText: '#FFFFFF' },
        background: { default: '#F3E3C6', paper: '#FDF5E8' },
        text: { primary: '#1F1B16', secondary: '#5A4E44' },
        divider: '#D3BC98',
      },
    },
    dark: {
      palette: {
        primary: { main: '#FFB95A', contrastText: '#4A2800' },
        secondary: { main: '#DDC2A1', contrastText: '#3E2D16' },
        success: { main: '#B5CEA4', contrastText: '#223518' },
        error: { main: '#FFB4AB', contrastText: '#690005' },
        background: { default: '#19120C', paper: '#261E17' },
        text: { primary: '#EFE0D5', secondary: '#D6C3B5' },
        divider: '#51443A',
      },
    },
  },
  shape: { borderRadius: 8 },
  // Field UX (NFR-3): large tap targets for gloved, one-handed use.
  components: {
    MuiButton: { defaultProps: { size: 'large' }, styleOverrides: { root: { minHeight: minTapTarget } } },
    MuiIconButton: { styleOverrides: { root: { minWidth: minTapTarget, minHeight: minTapTarget } } },
    MuiCheckbox: { styleOverrides: { root: { minWidth: minTapTarget, minHeight: minTapTarget } } },
    MuiListItemButton: { styleOverrides: { root: { minHeight: rowHeight } } },
    // The capture screen's answers are toggles: taller than a button, for a gloved thumb.
    MuiToggleButton: { styleOverrides: { root: { minHeight: rowHeight, minWidth: minTapTarget } } },
    MuiMenuItem: { styleOverrides: { root: { minHeight: minTapTarget } } },
    MuiBottomNavigationAction: { styleOverrides: { root: { minHeight: rowHeight } } },
  },
});

// Used in App.tsx: <ThemeProvider theme={honeyTheme}><CssBaseline />...</ThemeProvider>. It follows the device's
// light/dark setting; ColorSchemeToggle overrides it through useColorScheme().
