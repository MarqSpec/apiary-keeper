import type { Colony } from '../api/colonies';
import { useChangeNotice } from '../components/useChangeNotice';
import { queryKeys, useMutation, useQueryClient } from '../lib/query';

/**
 * Runs a lineage action (FR-6) and, once the API has appended its event, refreshes every colony read — the colony,
 * its history and queens, and the lists — shows any warning the API recorded it with on `colonyId`'s screen, then
 * calls `onDone`.
 */
export function useLineageAction<T>(colonyId: string, action: (input: T) => Promise<Colony>, onDone: () => void) {
  const queryClient = useQueryClient();
  const notice = useChangeNotice();
  return useMutation({
    mutationFn: action,
    onSuccess: async (result) => {
      notice.show(colonyId, result.warnings);
      await queryClient.invalidateQueries({ queryKey: queryKeys.colonies });
      onDone();
    },
  });
}
