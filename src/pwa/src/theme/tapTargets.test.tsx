import BottomNavigation from '@mui/material/BottomNavigation';
import BottomNavigationAction from '@mui/material/BottomNavigationAction';
import Button from '@mui/material/Button';
import Checkbox from '@mui/material/Checkbox';
import IconButton from '@mui/material/IconButton';
import ListItemButton from '@mui/material/ListItemButton';
import MenuItem from '@mui/material/MenuItem';
import MenuList from '@mui/material/MenuList';
import ToggleButton from '@mui/material/ToggleButton';
import { ThemeProvider } from '@mui/material/styles';
import { render, screen } from '@testing-library/react';
import { describe, expect, it } from 'vitest';
import { honeyTheme, minTapTarget } from './honeyTheme';

// NFR-3: large tap targets, usable with gloves. jsdom lays nothing out, so the floor is read from the styles.
function minHeightOf(element: HTMLElement): number {
  return Number.parseFloat(getComputedStyle(element).minHeight);
}

function inTheme(ui: React.ReactElement) {
  return render(<ThemeProvider theme={honeyTheme}>{ui}</ThemeProvider>);
}

describe('honeyTheme tap targets', () => {
  it('should be at least 48 px when the floor is set', () => {
    expect(minTapTarget).toBeGreaterThanOrEqual(48);
  });

  it('should give a button the tap-target floor when it is rendered', () => {
    inTheme(<Button>Save</Button>);

    expect(minHeightOf(screen.getByRole('button', { name: 'Save' }))).toBeGreaterThanOrEqual(minTapTarget);
  });

  it('should give an icon button the tap-target floor in both directions when it is rendered', () => {
    inTheme(<IconButton aria-label="Back">‹</IconButton>);
    const button = screen.getByRole('button', { name: 'Back' });

    expect([minHeightOf(button), Number.parseFloat(getComputedStyle(button).minWidth)]).toEqual([
      minTapTarget,
      minTapTarget,
    ]);
  });

  it('should give a checkbox the tap-target floor in both directions when it is rendered', () => {
    inTheme(<Checkbox slotProps={{ input: { 'aria-label': 'Known' } }} />);
    const root = screen.getByRole('checkbox', { name: 'Known' }).parentElement as HTMLElement;

    expect([minHeightOf(root), Number.parseFloat(getComputedStyle(root).minWidth)]).toEqual([
      minTapTarget,
      minTapTarget,
    ]);
  });

  it('should give a list row more than the floor when it is rendered', () => {
    inTheme(<ListItemButton>Pecan Bottom</ListItemButton>);

    expect(minHeightOf(screen.getByRole('button', { name: 'Pecan Bottom' }))).toBeGreaterThan(minTapTarget);
  });

  it('should give a toggle button a row-height floor and a tap-target width when it is rendered', () => {
    inTheme(<ToggleButton value="yes">Yes</ToggleButton>);
    const button = screen.getByRole('button', { name: 'Yes' });

    expect(minHeightOf(button)).toBeGreaterThan(minTapTarget);
    expect(Number.parseFloat(getComputedStyle(button).minWidth)).toBeGreaterThanOrEqual(minTapTarget);
  });

  it('should give a menu option the tap-target floor when it is rendered', () => {
    inTheme(
      <MenuList>
        <MenuItem>Honey</MenuItem>
      </MenuList>,
    );

    expect(minHeightOf(screen.getByRole('menuitem', { name: 'Honey' }))).toBeGreaterThanOrEqual(minTapTarget);
  });

  it('should give a bottom navigation tab more than the floor when it is rendered', () => {
    inTheme(
      <BottomNavigation showLabels value={0}>
        <BottomNavigationAction label="Yards" />
      </BottomNavigation>,
    );

    expect(minHeightOf(screen.getByRole('button', { name: 'Yards' }))).toBeGreaterThan(minTapTarget);
  });
});
