import { describe, expect, it } from 'vitest';
import { contrastRatio } from './contrast';
import { honeyTheme } from './honeyTheme';

// NFR-13: text contrast at least 4.5:1 in both schemes.
const minimum = 4.5;

const schemes = ['light', 'dark'] as const;

function paletteOf(scheme: (typeof schemes)[number]) {
  const palette = honeyTheme.colorSchemes[scheme]?.palette;
  if (!palette) throw new Error(`The honey theme has no ${scheme} scheme`);
  return palette;
}

describe('honeyTheme', () => {
  it('should define a light and a dark colour scheme when it is created', () => {
    expect(Object.keys(honeyTheme.colorSchemes).sort()).toEqual(['dark', 'light']);
  });

  // The toggle can only override the device when schemes switch on an attribute, not the media query.
  it('should switch schemes on a data attribute when the user overrides the device', () => {
    const toggled = honeyTheme.generateStyleSheets().some((sheet) =>
      Object.keys(sheet).some((selector) => selector.includes('[data-mui-scheme="dark"]')),
    );
    expect(toggled).toBe(true);
  });

  describe.each(schemes)('the %s scheme', (scheme) => {
    const palette = paletteOf(scheme);
    const surfaces = { background: palette.background.default, paper: palette.background.paper };

    describe.each(Object.entries(surfaces))('on %s', (_, surface) => {
      it('should keep primary text at 4.5:1 or more when it is on the surface', () => {
        expect(contrastRatio(palette.text.primary, surface)).toBeGreaterThanOrEqual(minimum);
      });

      it('should keep secondary text at 4.5:1 or more when it is on the surface', () => {
        expect(contrastRatio(palette.text.secondary, surface)).toBeGreaterThanOrEqual(minimum);
      });

      it('should keep primary-coloured text at 4.5:1 or more when it is on the surface', () => {
        expect(contrastRatio(palette.primary.main, surface)).toBeGreaterThanOrEqual(minimum);
      });
    });

    describe.each(['primary', 'secondary', 'success', 'error'] as const)('%s', (role) => {
      it('should keep its contrast text at 4.5:1 or more when it fills a button', () => {
        expect(contrastRatio(palette[role].contrastText, palette[role].main)).toBeGreaterThanOrEqual(minimum);
      });
    });
  });
});
