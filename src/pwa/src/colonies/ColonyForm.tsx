import Button from '@mui/material/Button';
import Checkbox from '@mui/material/Checkbox';
import FormControlLabel from '@mui/material/FormControlLabel';
import MenuItem from '@mui/material/MenuItem';
import Stack from '@mui/material/Stack';
import TextField from '@mui/material/TextField';
import { useState, type FormEvent } from 'react';
import {
  captureSwarm,
  getColony,
  registerColony,
  splitColony,
  type Colony,
  type ColonyInput,
} from '../api/colonies';
import { fieldErrorsOf } from '../api/fieldErrors';
import { listYards, type Yard } from '../api/yards';
import { useChangeNotice } from '../components/useChangeNotice';
import { FormProblem } from '../components/FormProblem';
import { Loading } from '../components/Loading';
import { ProblemView } from '../components/ProblemView';
import { ScreenHeader } from '../components/ScreenHeader';
import { todayIso } from '../format';
import { queryKeys, useMutation, useQuery, useQueryClient } from '../lib/query';
import { hrefFor } from '../routing/routes';
import { navigate } from '../routing/useRoute';
import { QueenFields } from './QueenFields';
import { blankQueen, queenInputOf, queenPaths, type QueenValues } from './queenValues';

/** How the new colony came to be: registered as it stands, captured as a swarm, or split from `parentId`. */
export type ColonyFormProps =
  | { readonly kind: 'registered' | 'swarm-capture'; readonly yardId: string | null }
  | { readonly kind: 'split'; readonly parentId: string };

interface Values {
  readonly tagId: string;
  readonly hiveNumber: string;
  readonly yardId: string;
  readonly hiveType: string;
  readonly boxCount: string;
  readonly occurredOn: string;
  readonly queenKnown: boolean;
  readonly queen: QueenValues;
}

const paths = {
  tagId: 'tagId',
  hiveNumber: 'hiveNumber',
  yardId: 'yardId',
  hiveType: 'hiveType',
  boxCount: 'boxCount',
  occurredOn: 'occurredOn',
} as const;

const knownPaths = [...Object.values(paths), ...Object.values(queenPaths('queen.'))];

function inputOf(values: Values): ColonyInput {
  const boxes = Number.parseInt(values.boxCount, 10);
  return {
    tagId: values.tagId,
    hiveNumber: values.hiveNumber,
    yardId: values.yardId,
    hiveType: values.hiveType,
    boxCount: Number.isNaN(boxes) ? null : boxes,
    occurredOn: values.occurredOn,
    queen: values.queenKnown ? queenInputOf(values.queen) : null,
  };
}

/** Record a new colony: register one, capture a swarm, or split one off (UC-3; FR-4, FR-5, FR-6). */
export function ColonyForm(props: ColonyFormProps) {
  const parentId = props.kind === 'split' ? props.parentId : null;
  const yards = useQuery({ queryKey: queryKeys.yards, queryFn: ({ signal }) => listYards(signal) });
  const parent = useQuery({
    queryKey: queryKeys.colony(parentId ?? ''),
    queryFn: ({ signal }) => getColony(parentId ?? '', signal),
    enabled: parentId !== null,
  });

  const title =
    props.kind === 'registered'
      ? 'Register colony'
      : props.kind === 'swarm-capture'
        ? 'Capture swarm'
        : `Split from ${parent.data?.tagId ?? 'colony'}`;
  const back =
    parentId !== null
      ? { href: hrefFor({ name: 'colony', id: parentId }), label: 'the colony' }
      : props.kind !== 'split' && props.yardId !== null
        ? { href: hrefFor({ name: 'yard', id: props.yardId }), label: 'the yard' }
        : { href: hrefFor({ name: 'colonies' }), label: 'hives' };

  const failed = yards.isError ? yards : parent.isError ? parent : null;
  if (failed) {
    return (
      <>
        <ScreenHeader title={title} back={back} />
        <ProblemView error={failed.error} onRetry={() => void failed.refetch()} />
      </>
    );
  }
  if (yards.data === undefined || (parentId !== null && parent.data === undefined)) {
    return <Loading label="Loading the form" />;
  }

  const today = todayIso();
  const initial: Values = {
    tagId: '',
    hiveNumber: '',
    yardId: parent.data?.yardId ?? (props.kind !== 'split' ? props.yardId : null) ?? '',
    hiveType: parent.data?.hiveType ?? '',
    boxCount: '',
    occurredOn: today,
    queenKnown: false,
    queen: blankQueen(today),
  };

  return (
    <>
      <ScreenHeader title={title} back={back} />
      <ColonyFields props={props} yards={yards.data} initial={initial} />
    </>
  );
}

function save(props: ColonyFormProps, input: ColonyInput): Promise<Colony> {
  switch (props.kind) {
    case 'registered':
      return registerColony(input);
    case 'swarm-capture':
      return captureSwarm(input);
    case 'split':
      return splitColony(props.parentId, input);
    default: {
      const unreachable: never = props;
      return unreachable;
    }
  }
}

function ColonyFields({
  props,
  yards,
  initial,
}: {
  readonly props: ColonyFormProps;
  readonly yards: readonly Yard[];
  readonly initial: Values;
}) {
  const [values, setValues] = useState(initial);
  const queryClient = useQueryClient();
  const notice = useChangeNotice();
  const mutation = useMutation({
    mutationFn: (input: ColonyInput) => save(props, input),
    onSuccess: async (colony) => {
      notice.show(colony.id, colony.warnings);
      await queryClient.invalidateQueries({ queryKey: queryKeys.colonies });
      navigate({ name: 'colony', id: colony.id });
    },
  });

  const errors = fieldErrorsOf(mutation.error);
  const field = (name: keyof typeof paths, hint?: string) => {
    const message = errors[paths[name]]?.[0];
    return {
      id: `colony-${name}`,
      value: values[name],
      onChange: (event: { target: { value: string } }) => setValues({ ...values, [name]: event.target.value }),
      error: message !== undefined,
      helperText: message ?? hint,
      fullWidth: true,
    };
  };

  function submit(event: FormEvent) {
    event.preventDefault();
    mutation.mutate(inputOf(values));
  }

  return (
    <form noValidate onSubmit={submit}>
      <Stack spacing={2}>
        <FormProblem error={mutation.error} fields={knownPaths} />
        <TextField label="Tag ID" required {...field('tagId', "The id on the box's QR or NFC tag")} />
        <TextField label="Hive number" required {...field('hiveNumber')} />
        <TextField label="Yard" required select {...field('yardId')}>
          {yards.map((yard) => (
            <MenuItem key={yard.id} value={yard.id}>
              {yard.name}
            </MenuItem>
          ))}
        </TextField>
        <TextField label="Hive type" required placeholder="Langstroth 10-frame" {...field('hiveType')} />
        <Stack direction="row" spacing={2}>
          <TextField
            label="Boxes"
            required
            type="number"
            slotProps={{ htmlInput: { inputMode: 'numeric', min: 1, max: 30 } }}
            {...field('boxCount')}
          />
          <TextField
            label="Date"
            required
            type="date"
            slotProps={{ inputLabel: { shrink: true } }}
            {...field('occurredOn')}
          />
        </Stack>
        <FormControlLabel
          control={
            <Checkbox
              checked={values.queenKnown}
              onChange={(event) =>
                setValues({
                  ...values,
                  queenKnown: event.target.checked,
                  queen: { ...values.queen, introducedOn: values.occurredOn },
                })
              }
            />
          }
          label="The queen is known"
        />
        {values.queenKnown && (
          <QueenFields
            idPrefix="colony-queen"
            values={values.queen}
            onChange={(queen) => setValues({ ...values, queen })}
            errors={errors}
            errorPrefix="queen."
          />
        )}
        <Button type="submit" variant="contained" loading={mutation.isPending}>
          Save colony
        </Button>
      </Stack>
    </form>
  );
}
