import Box from '@mui/material/Box';
import Button from '@mui/material/Button';
import Chip from '@mui/material/Chip';
import Link from '@mui/material/Link';
import List from '@mui/material/List';
import ListItem from '@mui/material/ListItem';
import ListItemButton from '@mui/material/ListItemButton';
import ListItemText from '@mui/material/ListItemText';
import Paper from '@mui/material/Paper';
import Stack from '@mui/material/Stack';
import Typography from '@mui/material/Typography';
import { useState } from 'react';
import { getColony, listColonies, listColonyEvents, listQueens, type Colony } from '../api/colonies';
import { listInspectionsPage } from '../api/inspections';
import { listYards } from '../api/yards';
import { ChangeNoticeView } from '../components/ChangeNotice';
import { Facts } from '../components/Facts';
import { Loading } from '../components/Loading';
import { ProblemView } from '../components/ProblemView';
import { ScreenHeader } from '../components/ScreenHeader';
import { formatDay, queenMark, statusLabels } from '../format';
import { queryKeys, useInfiniteQuery, useQuery } from '../lib/query';
import { hrefFor } from '../routing/routes';
import { minTapTarget } from '../theme/honeyTheme';
import { describeEvent, type Names } from './describeEvent';
import { findingsText } from './describeInspection';
import { historyOf } from './history';
import { MergeDialog } from './MergeDialog';
import { MoveDialog } from './MoveDialog';
import { RequeenDialog } from './RequeenDialog';

const originLabels: Readonly<Record<Colony['origin'], string>> = {
  registered: 'Registered',
  'swarm-capture': 'Swarm capture',
  split: 'Split',
};

function ColonyLink({ id, names }: { readonly id: string; readonly names: Names }) {
  return (
    <Link
      href={hrefFor({ name: 'colony', id })}
      sx={{ display: 'inline-flex', alignItems: 'center', minHeight: minTapTarget, minWidth: minTapTarget }}
    >
      {names.colony(id) ?? 'another colony'}
    </Link>
  );
}

/** One colony: its state, its queen and its full lineage, with the actions that append to it (UC-3; FR-4–FR-6). */
export function ColonyDetail({ colonyId }: { readonly colonyId: string }) {
  const colony = useQuery({
    queryKey: queryKeys.colony(colonyId),
    queryFn: ({ signal }) => getColony(colonyId, signal),
  });
  const events = useQuery({
    queryKey: queryKeys.colonyEvents(colonyId),
    queryFn: ({ signal }) => listColonyEvents(colonyId, signal),
  });
  const inspections = useInfiniteQuery({
    queryKey: queryKeys.inspections(colonyId),
    queryFn: ({ pageParam, signal }) => listInspectionsPage(colonyId, pageParam, signal),
    initialPageParam: null as string | null,
    getNextPageParam: (last) => last.nextCursor,
  });
  const queens = useQuery({ queryKey: queryKeys.queens(colonyId), queryFn: ({ signal }) => listQueens(colonyId, signal) });
  const yards = useQuery({ queryKey: queryKeys.yards, queryFn: ({ signal }) => listYards(signal) });
  const colonies = useQuery({ queryKey: queryKeys.colonies, queryFn: ({ signal }) => listColonies(signal) });
  const [dialog, setDialog] = useState<'requeen' | 'move' | 'merge' | null>(null);

  if (colony.isPending) return <Loading label="Loading the colony" />;
  if (colony.isError) {
    return (
      <>
        <ScreenHeader title="Colony" back={{ href: hrefFor({ name: 'colonies' }), label: 'hives' }} />
        <ProblemView error={colony.error} onRetry={() => void colony.refetch()} />
      </>
    );
  }

  const names: Names = {
    colony: (id) => colonies.data?.find((c) => c.id === id)?.tagId,
    yard: (id) => yards.data?.find((y) => y.id === id)?.name,
  };
  const current = colony.data;
  const yardName = names.yard(current.yardId) ?? 'its yard';
  const active = current.status === 'active';
  const close = () => setDialog(null);

  return (
    <Stack spacing={3}>
      <ScreenHeader
        title={current.tagId}
        subtitle={`Hive ${current.hiveNumber} · ${yardName}`}
        back={{ href: hrefFor({ name: 'yard', id: current.yardId }), label: yardName }}
      />
      <ChangeNoticeView colonyId={colonyId} />
      <Paper sx={{ p: 2 }}>
        <Stack spacing={2}>
          <div>
            <Chip label={statusLabels[current.status]} color={active ? 'success' : 'default'} />
          </div>
          <Facts
            facts={[
              ['Hive type', current.hiveType],
              ['Boxes when recorded', String(current.boxCount)],
              ['Origin', originLabels[current.origin]],
              [
                'Split from',
                current.parentColonyId === null ? null : <ColonyLink key="parent" id={current.parentColonyId} names={names} />,
              ],
              [
                'Merged into',
                current.mergedIntoColonyId === null ? null : (
                  <ColonyLink key="merged" id={current.mergedIntoColonyId} names={names} />
                ),
              ],
            ]}
          />
        </Stack>
      </Paper>

      <Paper component="section" aria-labelledby="colony-queen" sx={{ p: 2 }}>
        <Typography id="colony-queen" variant="h6" component="h3" sx={{ mb: 1 }}>
          Queen
        </Typography>
        {current.queen === null ? (
          <Typography>No queen recorded.</Typography>
        ) : (
          <Facts
            facts={[
              ['Source', current.queen.source],
              ['Mark', queenMark(current.queen)],
              ['Introduced', formatDay(current.queen.introducedOn)],
            ]}
          />
        )}
      </Paper>

      <Button variant="contained" size="large" fullWidth href={hrefFor({ name: 'colony-inspect', id: colonyId })}>
        Inspect
      </Button>

      {active && (
        <Stack spacing={1}>
          <Stack direction="row" spacing={1}>
            <Button variant="contained" fullWidth onClick={() => setDialog('requeen')}>
              Requeen
            </Button>
            <Button variant="contained" fullWidth onClick={() => setDialog('move')}>
              Move
            </Button>
          </Stack>
          <Stack direction="row" spacing={1}>
            <Button variant="outlined" fullWidth href={hrefFor({ name: 'colony-split', id: colonyId })}>
              Split
            </Button>
            <Button variant="outlined" fullWidth onClick={() => setDialog('merge')}>
              Merge
            </Button>
          </Stack>
        </Stack>
      )}

      <section aria-labelledby="colony-history">
        <Typography id="colony-history" variant="h6" component="h3" sx={{ mb: 1 }}>
          History
        </Typography>
        {(events.isPending || inspections.isPending) && <Loading label="Loading the history" />}
        {events.isError && <ProblemView error={events.error} onRetry={() => void events.refetch()} />}
        {inspections.isError && (
          <Stack spacing={1} sx={{ mb: 1 }}>
            <Typography>
              {inspections.isFetchNextPageError
                ? 'Older inspections could not be read.'
                : `The inspections could not be read.${inspections.data === undefined ? ' The lineage below is without them.' : ''}`}
            </Typography>
            <ProblemView
              error={inspections.error}
              onRetry={() => void (inspections.isFetchNextPageError ? inspections.fetchNextPage() : inspections.refetch())}
            />
          </Stack>
        )}
        {events.data && !inspections.isPending && (
          <Paper>
            <List aria-label="History" disablePadding>
              {historyOf(
                events.data.toReversed(),
                inspections.data?.pages.flatMap((page) => page.items) ?? [],
                !inspections.hasNextPage,
              ).map((entry) => {
                if (entry.kind === 'inspection') {
                  const { inspection } = entry;
                  return (
                    <ListItem key={inspection.id} divider>
                      <ListItemText
                        primary={`${formatDay(inspection.inspectedOn)} · Inspection`}
                        secondary={
                          <>
                            {findingsText(inspection.findings).join(' · ')}
                            {inspection.notes !== null && (
                              <Box component="span" sx={{ display: 'block', whiteSpace: 'pre-wrap', overflowWrap: 'anywhere' }}>
                                {inspection.notes}
                              </Box>
                            )}
                          </>
                        }
                      />
                    </ListItem>
                  );
                }
                const { event } = entry;
                const { title, detail, related } = describeEvent(event, colonyId, names);
                const text = <ListItemText primary={`${formatDay(event.occurredOn)} · ${title}`} secondary={detail} />;
                // An event naming another colony opens it: the whole row is the tap target (NFR-3).
                return related === undefined ? (
                  <ListItem key={event.id} divider>
                    {text}
                  </ListItem>
                ) : (
                  <li key={event.id}>
                    <ListItemButton component="a" href={hrefFor({ name: 'colony', id: related })} divider>
                      {text}
                    </ListItemButton>
                  </li>
                );
              })}
            </List>
          </Paper>
        )}
        {inspections.data?.pages[0]?.items.length === 0 && (
          <Typography sx={{ mt: 1 }}>No inspections recorded yet.</Typography>
        )}
        {inspections.hasNextPage && (
          <Button
            variant="outlined"
            fullWidth
            sx={{ mt: 1 }}
            loading={inspections.isFetchingNextPage}
            onClick={() => void inspections.fetchNextPage()}
          >
            Show older
          </Button>
        )}
      </section>

      <section aria-labelledby="colony-queens">
        <Typography id="colony-queens" variant="h6" component="h3" sx={{ mb: 1 }}>
          Queens
        </Typography>
        {queens.isPending && <Loading label="Loading the queens" />}
        {queens.isError && <ProblemView error={queens.error} onRetry={() => void queens.refetch()} />}
        {queens.data?.length === 0 && <Typography>No queen recorded.</Typography>}
        {queens.data && queens.data.length > 0 && (
          <Paper>
            <List aria-label="Queens" disablePadding>
              {/* The current queen is the one not yet replaced, wherever the list puts her. */}
              {queens.data.map((queen) => (
                <ListItem key={queen.id} divider>
                  <ListItemText
                    primary={`${queen.source} · ${queenMark(queen)}${queen.replacedOn === null ? ' · Current' : ''}`}
                    secondary={`Introduced ${formatDay(queen.introducedOn)}${
                      queen.replacedOn === null ? '' : ` · replaced ${formatDay(queen.replacedOn)}`
                    }`}
                  />
                </ListItem>
              ))}
            </List>
          </Paper>
        )}
      </section>

      {dialog === 'requeen' && <RequeenDialog colony={current} onClose={close} />}
      {dialog === 'move' && <MoveDialog colony={current} yards={yards.data ?? []} onClose={close} />}
      {dialog === 'merge' && <MergeDialog colony={current} colonies={colonies.data ?? []} onClose={close} />}
    </Stack>
  );
}
