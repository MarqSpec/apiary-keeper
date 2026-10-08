// WCAG 2.x contrast ratio, the measure NFR-13's 4.5:1 is stated in.
// https://www.w3.org/TR/WCAG22/#dfn-contrast-ratio

function linear(channel: number): number {
  const c = channel / 255;
  return c <= 0.04045 ? c / 12.92 : ((c + 0.055) / 1.055) ** 2.4;
}

function luminance(hex: string): number {
  const match = /^#([0-9a-f]{3}|[0-9a-f]{6})$/i.exec(hex);
  const digits = match?.[1];
  if (!digits) throw new Error(`Expected a hex colour such as #8A5100, got '${hex}'`);
  const full = digits.length === 3 ? [...digits].map((d) => d + d).join('') : digits;
  const r = parseInt(full.slice(0, 2), 16);
  const g = parseInt(full.slice(2, 4), 16);
  const b = parseInt(full.slice(4, 6), 16);
  return 0.2126 * linear(r) + 0.7152 * linear(g) + 0.0722 * linear(b);
}

/** The WCAG contrast ratio of two hex colours, from 1 (none) to 21 (black on white); order does not matter. */
export function contrastRatio(foreground: string, background: string): number {
  const a = luminance(foreground);
  const b = luminance(background);
  return (Math.max(a, b) + 0.05) / (Math.min(a, b) + 0.05);
}
