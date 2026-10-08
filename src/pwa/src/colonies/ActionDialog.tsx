import Button from '@mui/material/Button';
import Dialog from '@mui/material/Dialog';
import DialogActions from '@mui/material/DialogActions';
import DialogContent from '@mui/material/DialogContent';
import DialogTitle from '@mui/material/DialogTitle';
import Stack from '@mui/material/Stack';
import type { FormEvent, ReactNode } from 'react';
import { FormProblem } from '../components/FormProblem';

/**
 * A lineage action as a form in a dialog, open while it is rendered: its fields, Cancel and the action's own button.
 * A failure shows above the fields and the dialog stays open, so nothing typed is lost.
 */
export function ActionDialog({
  title,
  action,
  pending,
  error,
  fields,
  onClose,
  onSubmit,
  children,
}: {
  readonly title: string;
  readonly action: string;
  readonly pending: boolean;
  readonly error: unknown;
  readonly fields: readonly string[];
  readonly onClose: () => void;
  readonly onSubmit: () => void;
  readonly children: ReactNode;
}) {
  const titleId = `${action.toLowerCase().replaceAll(' ', '-')}-title`;
  function submit(event: FormEvent) {
    event.preventDefault();
    onSubmit();
  }

  return (
    <Dialog open onClose={onClose} fullWidth aria-labelledby={titleId}>
      <form noValidate onSubmit={submit}>
        <DialogTitle id={titleId}>{title}</DialogTitle>
        <DialogContent>
          <Stack spacing={2} sx={{ pt: 1 }}>
            <FormProblem error={error} fields={fields} />
            {children}
          </Stack>
        </DialogContent>
        <DialogActions>
          <Button onClick={onClose}>Cancel</Button>
          <Button type="submit" variant="contained" loading={pending}>
            {action}
          </Button>
        </DialogActions>
      </form>
    </Dialog>
  );
}
