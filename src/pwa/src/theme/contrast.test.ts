import { describe, expect, it } from 'vitest';
import { contrastRatio } from './contrast';

describe('contrastRatio', () => {
  it('should be 21 when black text is on white', () => {
    expect(contrastRatio('#000000', '#FFFFFF')).toBeCloseTo(21, 5);
  });

  it('should be 1 when both colours are the same', () => {
    expect(contrastRatio('#8A5100', '#8A5100')).toBeCloseTo(1, 5);
  });

  it('should not depend on which colour is the foreground when the pair is swapped', () => {
    expect(contrastRatio('#FFFFFF', '#767676')).toBeCloseTo(contrastRatio('#767676', '#FFFFFF'), 10);
  });

  // #767676 on white is the textbook just-passing AA grey (4.54:1); a wrong gamma curve misses it.
  it('should match the WCAG reference value when the grey is #767676 on white', () => {
    expect(contrastRatio('#767676', '#FFFFFF')).toBeCloseTo(4.54, 2);
  });

  it('should read three-digit hex the same as six-digit when the colour is shorthand', () => {
    expect(contrastRatio('#fff', '#000')).toBeCloseTo(21, 5);
  });

  it('should throw when the colour is not a hex value', () => {
    expect(() => contrastRatio('rgb(0, 0, 0)', '#FFFFFF')).toThrow(/hex colour/);
  });
});
