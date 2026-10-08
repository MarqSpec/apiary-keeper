import AppBar from '@mui/material/AppBar';
import BottomNavigation from '@mui/material/BottomNavigation';
import BottomNavigationAction from '@mui/material/BottomNavigationAction';
import Container from '@mui/material/Container';
import CssBaseline from '@mui/material/CssBaseline';
import Paper from '@mui/material/Paper';
import Toolbar from '@mui/material/Toolbar';
import Typography from '@mui/material/Typography';
import { ThemeProvider } from '@mui/material/styles';
import { useState } from 'react';
import { ColonyDetail } from './colonies/ColonyDetail';
import { ColonyForm } from './colonies/ColonyForm';
import { ColonyList } from './colonies/ColonyList';
import { CaptureScreen } from './capture/CaptureScreen';
import { ChangeNoticeProvider } from './components/ChangeNotice';
import { ColorSchemeToggle } from './components/ColorSchemeToggle';
import { ScreenHeader } from './components/ScreenHeader';
import { createQueryClient, QueryClientProvider } from './lib/query';
import { hrefFor, type Route } from './routing/routes';
import { useRoute } from './routing/useRoute';
import { honeyTheme } from './theme/honeyTheme';
import { bottomNavHeight } from './thumbAction';
import { YardDetail } from './yards/YardDetail';
import { YardForm } from './yards/YardForm';
import { YardList } from './yards/YardList';

function Screen({ route }: { readonly route: Route }) {
  switch (route.name) {
    case 'yards':
      return <YardList />;
    case 'yard-new':
      return <YardForm yardId={null} />;
    case 'yard':
      return <YardDetail key={route.id} yardId={route.id} />;
    case 'yard-edit':
      return <YardForm key={route.id} yardId={route.id} />;
    case 'colonies':
      return <ColonyList />;
    case 'colony-new':
      return <ColonyForm key={`${route.origin}-${route.yardId}`} kind={route.origin} yardId={route.yardId} />;
    case 'colony':
      return <ColonyDetail key={route.id} colonyId={route.id} />;
    case 'colony-split':
      return <ColonyForm key={route.id} kind="split" parentId={route.id} />;
    case 'colony-inspect':
      return <CaptureScreen key={route.id} colonyId={route.id} />;
    case 'not-found':
      return <ScreenHeader title="Page not found" back={{ href: hrefFor({ name: 'yards' }), label: 'yards' }} />;
    default: {
      const unreachable: never = route;
      return unreachable;
    }
  }
}

const tabs = [
  { label: 'Yards', href: hrefFor({ name: 'yards' }), screens: ['yards', 'yard-new', 'yard', 'yard-edit'] },
  { label: 'Hives', href: hrefFor({ name: 'colonies' }), screens: ['colonies', 'colony-new', 'colony', 'colony-split', 'colony-inspect'] },
] as const;

/** The family PWA's shell: the honey theme, the app bar, the current screen and the bottom navigation. */
export default function App() {
  const [queryClient] = useState(createQueryClient);
  const route = useRoute();
  const tab = tabs.findIndex((t) => (t.screens as readonly string[]).includes(route.name));

  return (
    <ThemeProvider theme={honeyTheme} defaultMode="system">
      <CssBaseline />
      <QueryClientProvider client={queryClient}>
        <AppBar position="sticky">
          <Toolbar>
            <Typography variant="h6" component="h1" sx={{ flexGrow: 1 }}>
              Apiary-Keeper
            </Typography>
            <ColorSchemeToggle />
          </Toolbar>
        </AppBar>
        {/* Room below the content for the floating action and the bottom navigation. */}
        <Container component="main" maxWidth="sm" sx={{ pt: 2, pb: `${bottomNavHeight + 96}px` }}>
          <ChangeNoticeProvider>
            <Screen route={route} />
          </ChangeNoticeProvider>
        </Container>
        <Paper component="nav" aria-label="Main" elevation={3} sx={{ position: 'fixed', bottom: 0, left: 0, right: 0 }}>
          <BottomNavigation showLabels value={tab === -1 ? false : tab} sx={{ pb: 'env(safe-area-inset-bottom)' }}>
            {tabs.map((t, index) => (
              <BottomNavigationAction
                key={t.label}
                label={t.label}
                href={t.href}
                aria-current={index === tab ? 'page' : undefined}
              />
            ))}
          </BottomNavigation>
        </Paper>
      </QueryClientProvider>
    </ThemeProvider>
  );
}
