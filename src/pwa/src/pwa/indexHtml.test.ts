import { describe, expect, it } from 'vitest';
import indexHtml from '../../index.html?raw';
import { manifest } from './manifest';

describe('index.html', () => {
  const page = new DOMParser().parseFromString(indexHtml, 'text/html');

  it('should title the page with the product name when the browser shows the tab', () => {
    expect(page.title).toBe('Apiary-Keeper');
  });

  it('should describe the product in the manifest words when a search engine reads the page', () => {
    expect(page.querySelector('meta[name="description"]')?.getAttribute('content')).toBe(manifest.description);
  });
});
