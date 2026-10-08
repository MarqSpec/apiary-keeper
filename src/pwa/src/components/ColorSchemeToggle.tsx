import IconButton from '@mui/material/IconButton';
import SvgIcon from '@mui/material/SvgIcon';
import { useColorScheme } from '@mui/material/styles';

const sun = 'M12 7a5 5 0 1 0 0 10 5 5 0 0 0 0-10Zm0-5 1 3h-2l1-3Zm0 20-1-3h2l-1 3ZM2 12l3-1v2l-3-1Zm20 0-3 1v-2l3 1Z';
const moon = 'M12.3 2a9 9 0 1 0 9.7 11.6A7.5 7.5 0 0 1 12.3 2Z';

/**
 * The manual light/dark override (D-14). Until it is pressed the app follows the device; pressing it switches to
 * the other scheme, and MUI remembers the choice in localStorage across reloads.
 */
export function ColorSchemeToggle() {
  const { mode, systemMode, setMode } = useColorScheme();
  const current = mode === 'system' ? systemMode : mode;
  const next = current === 'dark' ? 'light' : 'dark';

  return (
    <IconButton color="inherit" aria-label={`Switch to ${next} theme`} onClick={() => setMode(next)}>
      <SvgIcon>
        <path d={next === 'dark' ? moon : sun} />
      </SvgIcon>
    </IconButton>
  );
}
