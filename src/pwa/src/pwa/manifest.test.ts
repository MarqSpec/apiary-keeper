import { describe, expect, it } from 'vitest';
import { honeyTheme } from '../theme/honeyTheme';
import { manifest } from './manifest';

const sizes = (manifest.icons ?? []).map((icon) => icon.sizes);

describe('manifest', () => {
  // Chrome's install criteria: a name, a start URL, standalone display, 192 and 512 px icons.
  it('should name the app when it is installed', () => {
    expect(manifest.name).toBe('Apiary-Keeper');
    expect(manifest.short_name).toBe('Apiary-Keeper');
  });

  it('should describe the product in the README words when it is installed', () => {
    expect(manifest.description).toBe(
      'Apiary-Keeper is an offline-first web app for owner-operator Texas apiarists that tracks yards, hives, inspections, treatments, harvests, and sales, and includes a Telegram family chat, a customer storefront, and AI agents planned for a later phase.',
    );
  });

  it('should open standalone at the root when it is launched from the home screen', () => {
    expect(manifest.display).toBe('standalone');
    expect(manifest.start_url).toBe('/');
  });

  it('should offer 192 and 512 pixel icons when a device installs it', () => {
    expect(sizes).toEqual(expect.arrayContaining(['192x192', '512x512']));
  });

  it('should offer a maskable icon when Android shapes the launcher icon', () => {
    expect(manifest.icons?.some((icon) => icon.purpose === 'maskable')).toBe(true);
  });

  it('should use the light theme colours when the splash screen is drawn', () => {
    const light = honeyTheme.colorSchemes.light?.palette;
    expect(manifest.theme_color).toBe(light?.primary.main);
    expect(manifest.background_color).toBe(light?.background.default);
  });
});
