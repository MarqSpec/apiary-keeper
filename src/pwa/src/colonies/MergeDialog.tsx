import MenuItem from '@mui/material/MenuItem';
import TextField from '@mui/material/TextField';
import { useState } from 'react';
import { mergeColony, type Colony } from '../api/colonies';
import { fieldErrorsOf } from '../api/fieldErrors';
import { todayIso } from '../format';
import { ActionDialog } from './ActionDialog';
import { useLineageAction } from './useLineageAction';

/** Merge this colony into another active one; it stays readable, with its history, as merged (FR-6). */
export function MergeDialog({
  colony,
  colonies,
  onClose,
}: {
  readonly colony: Colony;
  readonly colonies: readonly Colony[];
  readonly onClose: () => void;
}) {
  const [intoColonyId, setIntoColonyId] = useState('');
  const [occurredOn, setOccurredOn] = useState(todayIso);
  const merge = useLineageAction(colony.id, () => mergeColony(colony.id, { intoColonyId, occurredOn }), onClose);
  const errors = fieldErrorsOf(merge.error);

  return (
    <ActionDialog
      title={`Merge ${colony.tagId}`}
      action="Merge"
      pending={merge.isPending}
      error={merge.error}
      fields={['intoColonyId', 'occurredOn']}
      onClose={onClose}
      onSubmit={() => merge.mutate(undefined)}
    >
      <TextField
        id="merge-into"
        label="Into colony"
        required
        select
        fullWidth
        value={intoColonyId}
        onChange={(event) => setIntoColonyId(event.target.value)}
        error={errors.intoColonyId !== undefined}
        helperText={errors.intoColonyId?.[0]}
      >
        {colonies
          .filter((other) => other.id !== colony.id && other.status === 'active')
          .map((other) => (
            <MenuItem key={other.id} value={other.id}>
              {other.tagId}
            </MenuItem>
          ))}
      </TextField>
      <TextField
        id="merge-date"
        label="Date"
        required
        type="date"
        fullWidth
        value={occurredOn}
        onChange={(event) => setOccurredOn(event.target.value)}
        slotProps={{ inputLabel: { shrink: true } }}
        error={errors.occurredOn !== undefined}
        helperText={errors.occurredOn?.[0]}
      />
    </ActionDialog>
  );
}
