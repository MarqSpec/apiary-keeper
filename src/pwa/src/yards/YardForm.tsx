import Button from '@mui/material/Button';
import MenuItem from '@mui/material/MenuItem';
import Stack from '@mui/material/Stack';
import TextField from '@mui/material/TextField';
import Typography from '@mui/material/Typography';
import { useState, type FormEvent } from 'react';
import { fieldErrorsOf } from '../api/fieldErrors';
import { createYard, getYard, landownerTerms, updateYard, type Yard, type YardInput } from '../api/yards';
import { FormProblem } from '../components/FormProblem';
import { Loading } from '../components/Loading';
import { ProblemView } from '../components/ProblemView';
import { ScreenHeader } from '../components/ScreenHeader';
import { termsLabels } from '../format';
import { queryKeys, useMutation, useQuery, useQueryClient } from '../lib/query';
import { hrefFor } from '../routing/routes';
import { navigate } from '../routing/useRoute';

interface Values {
  readonly name: string;
  readonly latitude: string;
  readonly longitude: string;
  readonly state: string;
  readonly county: string;
  readonly accessNotes: string;
  readonly forageNotes: string;
  readonly contact: string;
  readonly terms: string;
  readonly nearbyCrops: string;
  readonly sprayContact: string;
}

const blank: Values = {
  name: '',
  latitude: '',
  longitude: '',
  state: 'TX',
  county: '',
  accessNotes: '',
  forageNotes: '',
  contact: '',
  terms: '',
  nearbyCrops: '',
  sprayContact: '',
};

function valuesOf(yard: Yard): Values {
  return {
    name: yard.name,
    latitude: String(yard.location.latitude),
    longitude: String(yard.location.longitude),
    state: yard.state,
    county: yard.county,
    accessNotes: yard.accessNotes ?? '',
    forageNotes: yard.forageNotes ?? '',
    contact: yard.landowner.contact,
    terms: yard.landowner.terms,
    nearbyCrops: yard.landowner.nearbyCrops ?? '',
    sprayContact: yard.landowner.sprayContact ?? '',
  };
}

/** A blank or unreadable coordinate goes as null, so the API names it as missing. */
function coordinate(text: string): number | null {
  const value = Number.parseFloat(text);
  return text.trim() === '' || Number.isNaN(value) ? null : value;
}

function inputOf(values: Values): YardInput {
  return {
    name: values.name,
    location: { latitude: coordinate(values.latitude), longitude: coordinate(values.longitude) },
    state: values.state,
    county: values.county,
    accessNotes: values.accessNotes,
    forageNotes: values.forageNotes,
    landowner: {
      contact: values.contact,
      terms: values.terms,
      nearbyCrops: values.nearbyCrops,
      sprayContact: values.sprayContact,
    },
  };
}

/** Each input and the JSON path the API reports its errors under. */
const paths: Readonly<Record<keyof Values, string>> = {
  name: 'name',
  latitude: 'location.latitude',
  longitude: 'location.longitude',
  state: 'state',
  county: 'county',
  accessNotes: 'accessNotes',
  forageNotes: 'forageNotes',
  contact: 'landowner.contact',
  terms: 'landowner.terms',
  nearbyCrops: 'landowner.nearbyCrops',
  sprayContact: 'landowner.sprayContact',
};

/** Create a yard, or edit one, with its landowner (UC-9; FR-1, FR-2). */
export function YardForm({ yardId }: { readonly yardId: string | null }) {
  const yard = useQuery({
    queryKey: queryKeys.yard(yardId ?? ''),
    queryFn: ({ signal }) => getYard(yardId ?? '', signal),
    enabled: yardId !== null,
  });

  const back =
    yardId === null
      ? { href: hrefFor({ name: 'yards' }), label: 'yards' }
      : { href: hrefFor({ name: 'yard', id: yardId }), label: 'the yard' };
  const title = yardId === null ? 'New yard' : 'Edit yard';

  if (yardId !== null && yard.isPending) return <Loading label="Loading the yard" />;
  if (yardId !== null && yard.isError) {
    return (
      <>
        <ScreenHeader title={title} back={back} />
        <ProblemView error={yard.error} onRetry={() => void yard.refetch()} />
      </>
    );
  }

  return (
    <>
      <ScreenHeader title={title} back={back} />
      <YardFields key={yard.data?.id ?? 'new'} yardId={yardId} initial={yard.data ? valuesOf(yard.data) : blank} />
    </>
  );
}

function YardFields({ yardId, initial }: { readonly yardId: string | null; readonly initial: Values }) {
  const [values, setValues] = useState(initial);
  const queryClient = useQueryClient();
  const save = useMutation({
    mutationFn: (input: YardInput) => (yardId === null ? createYard(input) : updateYard(yardId, input)),
    onSuccess: async (saved) => {
      queryClient.setQueryData(queryKeys.yard(saved.id), saved);
      await queryClient.invalidateQueries({ queryKey: queryKeys.yards, exact: true });
      navigate({ name: 'yard', id: saved.id });
    },
  });

  const errors = fieldErrorsOf(save.error);
  const field = (name: keyof Values) => {
    const message = errors[paths[name]]?.[0];
    return {
      id: `yard-${name}`,
      value: values[name],
      onChange: (event: { target: { value: string } }) => setValues({ ...values, [name]: event.target.value }),
      error: message !== undefined,
      helperText: message,
      fullWidth: true,
    };
  };

  function submit(event: FormEvent) {
    event.preventDefault();
    save.mutate(inputOf(values));
  }

  return (
    <form noValidate onSubmit={submit}>
      <Stack spacing={2}>
        <FormProblem error={save.error} fields={Object.values(paths)} />
        <TextField label="Name" required {...field('name')} />
        <Stack direction="row" spacing={2}>
          <TextField label="Latitude" required type="number" slotProps={{ htmlInput: { inputMode: 'decimal', step: 'any' } }} {...field('latitude')} />
          <TextField label="Longitude" required type="number" slotProps={{ htmlInput: { inputMode: 'decimal', step: 'any' } }} {...field('longitude')} />
        </Stack>
        <TextField label="State" required select {...field('state')}>
          <MenuItem value="TX">Texas</MenuItem>
        </TextField>
        <TextField label="County" required {...field('county')} />
        <TextField label="Access notes" multiline minRows={2} {...field('accessNotes')} />
        <TextField label="Forage notes" multiline minRows={2} {...field('forageNotes')} />
        <Typography variant="h6" component="h3">
          Landowner
        </Typography>
        <TextField label="Landowner contact" required {...field('contact')} />
        <TextField label="Terms" required select {...field('terms')}>
          {landownerTerms.map((terms) => (
            <MenuItem key={terms} value={terms}>
              {termsLabels[terms]}
            </MenuItem>
          ))}
        </TextField>
        <TextField label="Nearby crops" {...field('nearbyCrops')} />
        <TextField label="Spray contact" {...field('sprayContact')} />
        <Button type="submit" variant="contained" loading={save.isPending}>
          Save yard
        </Button>
      </Stack>
    </form>
  );
}
