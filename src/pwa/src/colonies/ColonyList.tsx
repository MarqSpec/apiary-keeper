import Fab from '@mui/material/Fab';
import Typography from '@mui/material/Typography';
import { listColonies } from '../api/colonies';
import { listYards } from '../api/yards';
import { ColonyRows } from '../components/ColonyRows';
import { Loading } from '../components/Loading';
import { ProblemView } from '../components/ProblemView';
import { ScreenHeader } from '../components/ScreenHeader';
import { queryKeys, useQuery } from '../lib/query';
import { hrefFor } from '../routing/routes';
import { thumbAction } from '../thumbAction';

/** Every colony across the yards, by tag id, active first (UC-3). */
export function ColonyList() {
  const colonies = useQuery({ queryKey: queryKeys.colonies, queryFn: ({ signal }) => listColonies(signal) });
  const yards = useQuery({ queryKey: queryKeys.yards, queryFn: ({ signal }) => listYards(signal) });
  const yardName = (id: string) => yards.data?.find((yard) => yard.id === id)?.name ?? 'Unknown yard';

  return (
    <>
      <ScreenHeader title="Hives" />
      {colonies.isPending && <Loading label="Loading hives" />}
      {colonies.isError && <ProblemView error={colonies.error} onRetry={() => void colonies.refetch()} />}
      {colonies.data?.length === 0 && <Typography>No hives recorded yet.</Typography>}
      {colonies.data && colonies.data.length > 0 && (
        <ColonyRows label="Hives" colonies={colonies.data} detail={(colony) => yardName(colony.yardId)} />
      )}
      {!colonies.isError && (
        <Fab
          variant="extended"
          color="primary"
          href={hrefFor({ name: 'colony-new', origin: 'registered', yardId: null })}
          sx={thumbAction}
        >
          Register colony
        </Fab>
      )}
    </>
  );
}
