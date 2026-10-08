// How the colony screen writes an inspection's findings. Derived from each field's name and value, so a field the
// catalogue gains shows without a change here.
// reference: INTERFACES.md D.1 *Inspection endpoints* (the catalogue is the authority)
import type { Finding } from '../api/inspections';

/** A field name as words: `queenSeen` → `Queen seen`. */
export function labelOf(name: string): string {
  const words = name.replace(/([a-z0-9])([A-Z])/g, '$1 $2').toLowerCase();
  return words.charAt(0).toUpperCase() + words.slice(1);
}

const optionText = (option: string) => option.replaceAll('-', ' ');

function valueText(value: Finding): string {
  if (typeof value === 'boolean') return value ? 'yes' : 'no';
  if (typeof value === 'number') return String(value);
  if (typeof value === 'string') return optionText(value);
  return value.length === 0 ? 'none' : value.map(optionText).join(', ');
}

/**
 * Each finding as `Label: value`, in the order the API sent them: the catalogue's order, which the API applies on
 * every read (INTERFACES.md D.1), so the PWA neither sorts nor holds a copy of the catalogue.
 */
export function findingsText(findings: Readonly<Record<string, Finding>>): string[] {
  return Object.entries(findings).map(([name, value]) => `${labelOf(name)}: ${valueText(value)}`);
}
