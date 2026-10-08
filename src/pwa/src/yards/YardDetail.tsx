import Button from '@mui/material/Button';
import Paper from '@mui/material/Paper';
import Stack from '@mui/material/Stack';
import Typography from '@mui/material/Typography';
import { listColonies } from '../api/colonies';
import { getYard } from '../api/yards';
import { ColonyRows } from '../components/ColonyRows';
import { Facts } from '../components/Facts';
import { Loading } from '../components/Loading';
import { ProblemView } from '../components/ProblemView';
import { ScreenHeader } from '../components/ScreenHeader';
import { termsLabels } from '../format';
import { queryKeys, useQuery } from '../lib/query';
import { hrefFor } from '../routing/routes';

const back = { href: hrefFor({ name: 'yards' }), label: 'yards' };

/** One yard: its landowner and notes, and the colonies in it (UC-9, UC-3). */
export function YardDetail({ yardId }: { readonly yardId: string }) {
  const yard = useQuery({ queryKey: queryKeys.yard(yardId), queryFn: ({ signal }) => getYard(yardId, signal) });
  // The API lists colonies for the whole account; the yard's are picked out here.
  const colonies = useQuery({ queryKey: queryKeys.colonies, queryFn: ({ signal }) => listColonies(signal) });

  if (yard.isPending) return <Loading label="Loading the yard" />;
  if (yard.isError) {
    return (
      <>
        <ScreenHeader title="Yard" back={back} />
        <ProblemView error={yard.error} onRetry={() => void yard.refetch()} />
      </>
    );
  }

  const { name, county, state, location, landowner, accessNotes, forageNotes } = yard.data;
  const here = colonies.data?.filter((colony) => colony.yardId === yardId) ?? [];

  return (
    <Stack spacing={3}>
      <ScreenHeader title={name} subtitle={`${county}, ${state}`} back={back} />
      <Paper sx={{ p: 2 }}>
        <Facts
          facts={[
            ['Landowner', landowner.contact],
            ['Terms', termsLabels[landowner.terms]],
            ['Nearby crops', landowner.nearbyCrops],
            ['Spray contact', landowner.sprayContact],
            ['Access notes', accessNotes],
            ['Forage notes', forageNotes],
            ['GPS', `${location.latitude}, ${location.longitude}`],
          ]}
        />
      </Paper>
      <Button variant="outlined" href={hrefFor({ name: 'yard-edit', id: yardId })}>
        Edit yard
      </Button>
      <section aria-labelledby="yard-hives">
        <Typography id="yard-hives" variant="h6" component="h3" sx={{ mb: 1 }}>
          Hives
        </Typography>
        {colonies.isPending && <Loading label="Loading hives" />}
        {colonies.isError && <ProblemView error={colonies.error} onRetry={() => void colonies.refetch()} />}
        {colonies.isSuccess && here.length === 0 && <Typography>No hives recorded in this yard yet.</Typography>}
        {here.length > 0 && <ColonyRows label="Hives" colonies={here} />}
      </section>
      <Stack direction="row" spacing={2}>
        <Button
          variant="contained"
          fullWidth
          href={hrefFor({ name: 'colony-new', origin: 'registered', yardId })}
        >
          Register colony
        </Button>
        <Button
          variant="outlined"
          fullWidth
          href={hrefFor({ name: 'colony-new', origin: 'swarm-capture', yardId })}
        >
          Capture swarm
        </Button>
      </Stack>
    </Stack>
  );
}
