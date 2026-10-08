// A change the API recorded with a warning (INTERFACES.md D.1 *Warnings*): shown, never blocking.
// reference: CONVENTIONS.md §4.3
import Alert from '@mui/material/Alert';
import AlertTitle from '@mui/material/AlertTitle';
import { useMemo, useState, type ReactNode } from 'react';
import { messageWithoutCategory } from '../api/client';
import { ChangeNoticeContext, useChangeNotice, type ChangeNotices, type Notice } from './useChangeNotice';

/** Holds the latest change's warnings across a navigation, so a new colony's screen can show them. */
export function ChangeNoticeProvider({ children }: { readonly children: ReactNode }) {
  const [notice, setNotice] = useState<Notice | null>(null);
  const value = useMemo<ChangeNotices>(
    () => ({
      notice,
      show: (colonyId, warnings) => {
        const messages = Object.values(warnings ?? {})
          .flat()
          .map(messageWithoutCategory);
        setNotice(messages.length === 0 ? null : { colonyId, messages });
      },
      dismiss: () => setNotice(null),
    }),
    [notice],
  );
  return <ChangeNoticeContext value={value}>{children}</ChangeNoticeContext>;
}

/** The warnings of the last change to `colonyId`, until dismissed; nothing when there are none. */
export function ChangeNoticeView({ colonyId }: { readonly colonyId: string }) {
  const { notice, dismiss } = useChangeNotice();
  if (notice?.colonyId !== colonyId) return null;
  return (
    <Alert
      severity="warning"
      onClose={dismiss}
      slotProps={{ closeButton: { title: 'Dismiss', 'aria-label': 'Dismiss' } }}
    >
      <AlertTitle component="h2">Recorded, with a warning</AlertTitle>
      {notice.messages.map((message) => (
        <div key={message}>{message}</div>
      ))}
    </Alert>
  );
}
