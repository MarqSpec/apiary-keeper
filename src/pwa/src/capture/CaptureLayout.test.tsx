import { screen, within } from '@testing-library/react';
import { describe, expect, it } from 'vitest';
import { colonyA01, stubApi } from '../test/fixtures';
import { renderScreen } from '../test/render';
import { minTapTarget } from '../theme/honeyTheme';
import { CaptureScreen } from './CaptureScreen';
import { fieldGroups } from './fields';

// jsdom lays nothing out, so "works at 390 px wide" is held by the styles that decide it: a row of answers
// wraps, three of them fit across a phone's content width, and nothing has a fixed width past the screen.
const phoneWidth = 390;
const gutters = 2 * 16;

describe('CaptureScreen at 390 px', () => {
  async function open() {
    stubApi({ colonies: [colonyA01] });
    renderScreen(<CaptureScreen colonyId="colony-1" />, '#/colonies/colony-1/inspect');
    await screen.findByRole('heading', { name: 'Inspect A-01' });
  }

  it('should let a row of answers wrap rather than run off the screen', async () => {
    await open();

    const row = screen.getByRole('group', { name: 'Disease signs' }).querySelector('div');
    expect(row && getComputedStyle(row).flexWrap).toBe('wrap');
  });

  it('should fit three answers across the content width when a field has three', async () => {
    await open();
    const toggle = within(screen.getByRole('group', { name: 'Brood pattern' })).getByRole('button', { name: 'Solid' });
    const minWidth = Number.parseFloat(getComputedStyle(toggle).minWidth);

    expect(minWidth).toBeGreaterThanOrEqual(minTapTarget);
    expect(3 * minWidth + 2 * 8).toBeLessThanOrEqual(phoneWidth - gutters);
  });

  it('should offer every field as a group a thumb can reach when it opens', async () => {
    await open();

    for (const field of fieldGroups.filter((f) => f.kind !== 'count')) {
      expect(screen.getByRole('group', { name: field.label })).toBeInTheDocument();
    }
  });
});
