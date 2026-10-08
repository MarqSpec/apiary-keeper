import { useState } from 'react';
import { requeenColony, type Colony } from '../api/colonies';
import { fieldErrorsOf } from '../api/fieldErrors';
import { todayIso } from '../format';
import { ActionDialog } from './ActionDialog';
import { QueenFields } from './QueenFields';
import { blankQueen, queenInputOf, queenPaths } from './queenValues';
import { useLineageAction } from './useLineageAction';

/** Introduce a new queen; the one she replaces keeps her record, now ended (FR-5). */
export function RequeenDialog({ colony, onClose }: { readonly colony: Colony; readonly onClose: () => void }) {
  const [queen, setQueen] = useState(() => blankQueen(todayIso()));
  const requeen = useLineageAction(colony.id, () => requeenColony(colony.id, queenInputOf(queen)), onClose);

  return (
    <ActionDialog
      title={`Requeen ${colony.tagId}`}
      action="Requeen"
      pending={requeen.isPending}
      error={requeen.error}
      fields={Object.values(queenPaths(''))}
      onClose={onClose}
      onSubmit={() => requeen.mutate(undefined)}
    >
      <QueenFields
        idPrefix="requeen"
        values={queen}
        onChange={setQueen}
        errors={fieldErrorsOf(requeen.error)}
        errorPrefix=""
      />
    </ActionDialog>
  );
}
