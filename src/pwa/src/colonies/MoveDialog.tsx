import MenuItem from '@mui/material/MenuItem';
import TextField from '@mui/material/TextField';
import { useState } from 'react';
import { moveColony, type Colony } from '../api/colonies';
import { fieldErrorsOf } from '../api/fieldErrors';
import type { Yard } from '../api/yards';
import { todayIso } from '../format';
import { ActionDialog } from './ActionDialog';
import { useLineageAction } from './useLineageAction';

/** Move the colony to another of the account's yards (FR-6). */
export function MoveDialog({
  colony,
  yards,
  onClose,
}: {
  readonly colony: Colony;
  readonly yards: readonly Yard[];
  readonly onClose: () => void;
}) {
  const [yardId, setYardId] = useState('');
  const [occurredOn, setOccurredOn] = useState(todayIso);
  const move = useLineageAction(colony.id, () => moveColony(colony.id, { yardId, occurredOn }), onClose);
  const errors = fieldErrorsOf(move.error);

  return (
    <ActionDialog
      title={`Move ${colony.tagId}`}
      action="Move"
      pending={move.isPending}
      error={move.error}
      fields={['yardId', 'occurredOn']}
      onClose={onClose}
      onSubmit={() => move.mutate(undefined)}
    >
      <TextField
        id="move-yard"
        label="To yard"
        required
        select
        fullWidth
        value={yardId}
        onChange={(event) => setYardId(event.target.value)}
        error={errors.yardId !== undefined}
        helperText={errors.yardId?.[0]}
      >
        {yards
          .filter((yard) => yard.id !== colony.yardId)
          .map((yard) => (
            <MenuItem key={yard.id} value={yard.id}>
              {yard.name}
            </MenuItem>
          ))}
      </TextField>
      <TextField
        id="move-date"
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
