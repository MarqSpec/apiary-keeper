import Fab from '@mui/material/Fab';
import List from '@mui/material/List';
import ListItemButton from '@mui/material/ListItemButton';
import ListItemText from '@mui/material/ListItemText';
import Paper from '@mui/material/Paper';
import Typography from '@mui/material/Typography';
import { listColonies } from '../api/colonies';
import { listYards } from '../api/yards';
import { Loading } from '../components/Loading';
import { ProblemView } from '../components/ProblemView';
import { ScreenHeader } from '../components/ScreenHeader';
import { queryKeys, useQuery } from '../lib/query';
import { hrefFor } from '../routing/routes';
import { thumbAction } from '../thumbAction';

const hives = (count: number) => (count === 1 ? '1 active hive' : `${count} active hives`);

/** Every yard, by name, with how many active hives each holds (UC-9). */
export function YardList() {
  const yards = useQuery({ queryKey: queryKeys.yards, queryFn: ({ signal }) => listYards(signal) });
  const colonies = useQuery({ queryKey: queryKeys.colonies, queryFn: ({ signal }) => listColonies(signal) });

  const activeIn = (yardId: string) =>
    colonies.data?.filter((colony) => colony.yardId === yardId && colony.status === 'active').length;

  return (
    <>
      <ScreenHeader title="Yards" />
      {yards.isPending && <Loading label="Loading yards" />}
      {yards.isError && <ProblemView error={yards.error} onRetry={() => void yards.refetch()} />}
      {yards.data?.length === 0 && <Typography>No yards yet. Add the first one.</Typography>}
      {yards.data && yards.data.length > 0 && (
        <Paper>
          <List disablePadding aria-label="Yards">
            {yards.data.map((yard) => {
              const count = activeIn(yard.id);
              return (
                <li key={yard.id}>
                  <ListItemButton component="a" href={hrefFor({ name: 'yard', id: yard.id })} divider>
                    <ListItemText
                      primary={yard.name}
                      secondary={`${yard.county}, ${yard.state}${count === undefined ? '' : ` · ${hives(count)}`}`}
                    />
                  </ListItemButton>
                </li>
              );
            })}
          </List>
        </Paper>
      )}
      {!yards.isError && (
        <Fab variant="extended" color="primary" href={hrefFor({ name: 'yard-new' })} sx={thumbAction}>
          Add yard
        </Fab>
      )}
    </>
  );
}
