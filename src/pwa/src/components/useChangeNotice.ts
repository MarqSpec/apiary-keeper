// The change notices' context and their hook, apart from the components so fast refresh keeps working.
import { createContext, useContext } from 'react';

export interface Notice {
  readonly colonyId: string;
  readonly messages: readonly string[];
}

export interface ChangeNotices {
  readonly notice: Notice | null;
  /** Shows a change's warnings on `colonyId`'s screen, or clears the notice when there are none. */
  readonly show: (colonyId: string, warnings: Readonly<Record<string, readonly string[]>> | undefined) => void;
  readonly dismiss: () => void;
}

export const ChangeNoticeContext = createContext<ChangeNotices | null>(null);

export function useChangeNotice(): ChangeNotices {
  const notices = useContext(ChangeNoticeContext);
  if (notices === null) throw new Error('useChangeNotice needs a ChangeNoticeProvider above it.');
  return notices;
}
