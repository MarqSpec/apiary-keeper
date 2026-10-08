import Alert from '@mui/material/Alert';
import Box from '@mui/material/Box';
import Button from '@mui/material/Button';
import IconButton from '@mui/material/IconButton';
import List from '@mui/material/List';
import ListItem from '@mui/material/ListItem';
import ListItemText from '@mui/material/ListItemText';
import Paper from '@mui/material/Paper';
import Stack from '@mui/material/Stack';
import TextField from '@mui/material/TextField';
import Typography from '@mui/material/Typography';
import { useRef, useState, type ChangeEvent, type FormEvent } from 'react';
import { ApiError } from '../api/client';
import { getColony, type Colony } from '../api/colonies';
import { fieldErrorsOf } from '../api/fieldErrors';
import { recordInspection, type Finding, type Inspection } from '../api/inspections';
import { attachMedia, putToBucket, requestMediaUpload } from '../api/media';
import { FormProblem } from '../components/FormProblem';
import { Loading } from '../components/Loading';
import { ProblemView } from '../components/ProblemView';
import { ScreenHeader } from '../components/ScreenHeader';
import { todayIso } from '../format';
import { queryKeys, useQuery, useQueryClient } from '../lib/query';
import { hrefFor } from '../routing/routes';
import { navigate } from '../routing/useRoute';
import { recordReading } from './captureTiming';
import { browserMedia, maxVideoSeconds, PhotoUnreadable, videoTooLong, type DeviceMedia } from './deviceMedia';
import { BroodFrames, ChoiceField, MultiChoiceField } from './fieldInputs';
import { answersOf, blankAnswers, fieldGroups, hasAnything, withAnswer, type Answers } from './fields';

/** `refused` is the API saying no (a file over its size limit, a type it does not take): trying again cannot help. */
type UploadStatus = 'waiting' | 'uploading' | 'done' | 'failed' | 'refused';

interface QueuedFile {
  readonly id: number;
  readonly kind: 'photo' | 'video';
  readonly name: string;
  readonly blob: Blob;
  readonly contentType: string;
  readonly status: UploadStatus;
  /** Why the API refused the file, in its words. */
  readonly reason?: string;
}

const statusText: Readonly<Record<UploadStatus, string>> = {
  waiting: 'Waiting to upload',
  uploading: 'Uploading',
  done: 'Uploaded',
  failed: 'Did not upload',
  refused: 'Refused',
};

const secondsSince = (from: number, to: number) => Math.round((to - from) / 100) / 10;

/** Hidden from sight but still reachable by keyboard and screen reader: the button around it is what is tapped. */
const visuallyHidden = {
  position: 'absolute',
  width: 1,
  height: 1,
  overflow: 'hidden',
  clip: 'rect(0 0 0 0)',
  whiteSpace: 'nowrap',
} as const;

/** Record an inspection in the field: taps for the findings, then photos and video straight to storage (UC-1; FR-7, FR-8). */
export function CaptureScreen({
  colonyId,
  deviceMedia = browserMedia,
}: {
  readonly colonyId: string;
  readonly deviceMedia?: DeviceMedia;
}) {
  const colony = useQuery({
    queryKey: queryKeys.colony(colonyId),
    queryFn: ({ signal }) => getColony(colonyId, signal),
  });
  const back = { href: hrefFor({ name: 'colony', id: colonyId }), label: 'the colony' };

  if (colony.isPending) return <Loading label="Loading the colony" />;
  if (colony.isError) {
    return (
      <>
        <ScreenHeader title="Inspect" back={back} />
        <ProblemView error={colony.error} onRetry={() => void colony.refetch()} />
      </>
    );
  }
  return <Capture colony={colony.data} deviceMedia={deviceMedia} />;
}

function Capture({ colony, deviceMedia }: { readonly colony: Colony; readonly deviceMedia: DeviceMedia }) {
  const queryClient = useQueryClient();
  // The clock starts when the screen opens: NFR-2 measures what the beekeeper experiences, not the request.
  const [openedAt] = useState(() => deviceMedia.now());
  const [day, setDay] = useState(todayIso);
  const [answers, setAnswers] = useState<Answers>(blankAnswers);
  const [notes, setNotes] = useState('');
  const [files, setFiles] = useState<readonly QueuedFile[]>([]);
  const [mediaProblems, setMediaProblems] = useState<readonly string[]>([]);
  const [saving, setSaving] = useState(false);
  const [saveError, setSaveError] = useState<unknown>(null);
  const [nothing, setNothing] = useState(false);
  const [recorded, setRecorded] = useState<Inspection | null>(null);
  const [savedAfter, setSavedAfter] = useState<number | null>(null);
  const nextId = useRef(0);
  const tappedSaveAt = useRef<number | null>(null);

  const back = { href: hrefFor({ name: 'colony', id: colony.id }), label: colony.tagId };
  const setStatus = (id: number, status: UploadStatus, reason?: string) =>
    setFiles((current) => current.map((file) => (file.id === id ? { ...file, status, reason } : file)));

  async function addPhoto(event: ChangeEvent<HTMLInputElement>) {
    const picked = event.target.files?.[0];
    event.target.value = '';
    if (picked === undefined) return;
    try {
      const blob = await deviceMedia.preparePhoto(picked);
      setFiles((current) => [
        ...current,
        { id: nextId.current++, kind: 'photo', name: picked.name, blob, contentType: 'image/jpeg', status: 'waiting' },
      ]);
    } catch (error) {
      const unreadable = error instanceof PhotoUnreadable;
      setMediaProblems((current) => [
        ...current,
        unreadable ? `${picked.name} could not be read as a photo.` : `${picked.name} could not be prepared. Try again.`,
      ]);
    }
  }

  async function addVideo(event: ChangeEvent<HTMLInputElement>) {
    const picked = event.target.files?.[0];
    event.target.value = '';
    if (picked === undefined) return;
    if (videoTooLong(await deviceMedia.videoSeconds(picked))) {
      setMediaProblems((current) => [
        ...current,
        `${picked.name} is longer than 1 minute, or its length is unknown. Record a clip of ${maxVideoSeconds} seconds or less.`,
      ]);
      return;
    }
    setFiles((current) => [
      ...current,
      { id: nextId.current++, kind: 'video', name: picked.name, blob: picked, contentType: picked.type, status: 'waiting' },
    ]);
  }

  /** Uploads each file not yet attached, one after another; the URL lives only in this call (INTERFACES.md F.3). */
  async function uploadAll(inspectionId: string, queue: readonly QueuedFile[]): Promise<boolean> {
    // A file the API refused earlier is still missing: the inspection is not saved while it is.
    let allDone = queue.every((f) => f.status !== 'refused');
    for (const file of queue.filter((f) => f.status !== 'done' && f.status !== 'refused')) {
      setStatus(file.id, 'uploading');
      try {
        const upload = await requestMediaUpload(inspectionId, { contentType: file.contentType, sizeBytes: file.blob.size });
        await putToBucket(upload, file.blob);
        await attachMedia(inspectionId, upload.objectKey);
        setStatus(file.id, 'done');
      } catch (error) {
        allDone = false;
        // The PWA cannot know the server's size limit (Media__*), so it shows the API's own words and stops retrying.
        if (error instanceof ApiError && error.failure.kind === 'validation') {
          const reason = error.failure.message || Object.values(error.failure.errors).flat()[0] || 'The server refused it.';
          setStatus(file.id, 'refused', reason);
        } else {
          setStatus(file.id, 'failed');
        }
      }
    }
    return allDone;
  }

  async function finish(inspection: Inspection, queue: readonly QueuedFile[]) {
    const uploaded = await uploadAll(inspection.id, queue);
    setSaving(false);
    if (!uploaded) return;
    const savedAt = deviceMedia.now();
    const openToSave = secondsSince(openedAt, tappedSaveAt.current ?? savedAt);
    const openToSaved = secondsSince(openedAt, savedAt);
    recordReading(window.localStorage, {
      at: new Date().toISOString(),
      openToSaveSeconds: openToSave,
      openToSavedSeconds: openToSaved,
    });
    setSavedAfter(openToSaved);
    await queryClient.invalidateQueries({ queryKey: queryKeys.inspections(colony.id) });
  }

  async function submit(event: FormEvent) {
    event.preventDefault();
    if (saving) return;
    const findings: Record<string, Finding> = answersOf(answers);
    const note = notes.trim();
    if (!hasAnything(findings, note)) {
      setNothing(true);
      return;
    }
    setNothing(false);
    setSaveError(null);
    setSaving(true);
    tappedSaveAt.current = deviceMedia.now();
    try {
      const inspection = await recordInspection(colony.id, {
        inspectedOn: day,
        findings,
        notes: note === '' ? null : note,
      });
      setRecorded(inspection);
      await finish(inspection, files);
    } catch (error) {
      setSaving(false);
      setSaveError(error);
    }
  }

  async function retryUploads() {
    if (recorded === null) return;
    setSaving(true);
    await finish(recorded, files);
  }

  if (savedAfter !== null) {
    return (
      <>
        <ScreenHeader title={`Inspect ${colony.tagId}`} back={back} />
        <Paper sx={{ p: 2 }}>
          <Stack spacing={2}>
            <Typography variant="h6" component="h3">
              Inspection saved
            </Typography>
            <Typography>{`Saved ${savedAfter} s after you opened this screen.`}</Typography>
            <Button variant="contained" component="a" href={hrefFor({ name: 'colony', id: colony.id })}>
              {`View ${colony.tagId}`}
            </Button>
          </Stack>
        </Paper>
      </>
    );
  }

  const errors = fieldErrorsOf(saveError);
  const failed = files.filter((file) => file.status === 'failed').length;
  const refused = files.filter((file) => file.status === 'refused').length;
  const plural = (n: number) => (n === 1 ? 'file' : 'files');
  const locked = recorded !== null;

  return (
    <>
      <ScreenHeader title={`Inspect ${colony.tagId}`} subtitle={`Hive ${colony.hiveNumber}`} back={back} />
      <form noValidate onSubmit={(event) => void submit(event)}>
        <Stack spacing={3}>
          <FormProblem error={saveError} fields={['inspectedOn', 'notes']} />
          {nothing && <Alert severity="warning">Tap at least one answer or write a note before saving.</Alert>}
          {locked && (
            <Alert severity={failed + refused > 0 ? 'warning' : 'info'}>
              {failed + refused > 0
                ? `The inspection is recorded, but ${[
                    failed > 0 ? `${failed} ${plural(failed)} did not upload` : '',
                    refused > 0 ? `${refused} ${plural(refused)} ${refused === 1 ? 'was' : 'were'} refused` : '',
                  ]
                    .filter(Boolean)
                    .join(' and ')}.`
                : 'The inspection is recorded. Finishing the uploads.'}
            </Alert>
          )}

          {!locked && (
            <>
              <TextField
                id="inspection-day"
                label="Day inspected"
                required
                type="date"
                value={day}
                onChange={(event) => setDay(event.target.value)}
                error={errors.inspectedOn !== undefined}
                helperText={errors.inspectedOn?.[0]}
                slotProps={{ inputLabel: { shrink: true } }}
                fullWidth
              />

              {fieldGroups.map((field) => {
                switch (field.kind) {
                  case 'flag':
                  case 'choice':
                    return (
                      <ChoiceField
                        key={field.name}
                        label={field.label}
                        options={field.kind === 'choice' ? field.options : undefined}
                        value={answers.single[field.name] ?? null}
                        onChange={(value) => setAnswers(withAnswer(answers, 'single', field.name, value))}
                      />
                    );
                  case 'multi':
                    return (
                      <MultiChoiceField
                        key={field.name}
                        label={field.label}
                        options={field.options}
                        value={answers.lists[field.name] ?? []}
                        onChange={(value) => setAnswers(withAnswer(answers, 'lists', field.name, value))}
                      />
                    );
                  case 'count':
                    return (
                      <BroodFrames
                        key={field.name}
                        label={field.label}
                        max={field.max}
                        value={answers.counts[field.name] ?? null}
                        onChange={(value) => setAnswers(withAnswer(answers, 'counts', field.name, value))}
                      />
                    );
                  default: {
                    const unreachable: never = field;
                    return unreachable;
                  }
                }
              })}

              <TextField
                id="inspection-notes"
                label="Notes"
                multiline
                minRows={2}
                value={notes}
                onChange={(event) => setNotes(event.target.value)}
                error={errors.notes !== undefined}
                helperText={errors.notes?.[0]}
                fullWidth
              />
            </>
          )}

          <Paper component="section" aria-labelledby="inspection-media" sx={{ p: 2 }}>
            <Typography id="inspection-media" variant="h6" component="h3" sx={{ mb: 1 }}>
              Photos and video
            </Typography>
            {!locked && (
              <Stack direction="row" spacing={1}>
                <Button component="label" variant="outlined" fullWidth>
                  Add photo
                  <Box component="input" type="file" accept="image/*" capture="environment" onChange={(e: ChangeEvent<HTMLInputElement>) => void addPhoto(e)} sx={visuallyHidden} />
                </Button>
                <Button component="label" variant="outlined" fullWidth>
                  {`Add video (${maxVideoSeconds / 60} min max)`}
                  <Box component="input" type="file" accept="video/*" capture="environment" onChange={(e: ChangeEvent<HTMLInputElement>) => void addVideo(e)} sx={visuallyHidden} />
                </Button>
              </Stack>
            )}
            {mediaProblems.map((problem) => (
              <Alert key={problem} severity="warning" sx={{ mt: 1 }}>
                {problem}
              </Alert>
            ))}
            {files.length > 0 && (
              <List aria-label="Files to upload" disablePadding sx={{ mt: 1 }}>
                {files.map((file) => (
                  <ListItem
                    key={file.id}
                    divider
                    secondaryAction={
                      !locked && (
                        <IconButton edge="end" aria-label={`Remove ${file.name}`} onClick={() => setFiles(files.filter((f) => f.id !== file.id))}>
                          ×
                        </IconButton>
                      )
                    }
                  >
                    <ListItemText primary={file.name} secondary={`${file.kind === 'photo' ? 'Photo' : 'Video'} · ${statusText[file.status]}${file.reason === undefined ? '' : `: ${file.reason}`}`} />
                  </ListItem>
                ))}
              </List>
            )}
          </Paper>

          {!locked && (
            <Button type="submit" variant="contained" loading={saving}>
              Save inspection
            </Button>
          )}
          {locked && failed + refused > 0 && !saving && (
            <Stack spacing={1}>
              {failed > 0 && (
                <Button variant="contained" onClick={() => void retryUploads()}>
                  Try the uploads again
                </Button>
              )}
              <Button variant="outlined" onClick={() => navigate({ name: 'colony', id: colony.id })}>
                Leave without them
              </Button>
            </Stack>
          )}
        </Stack>
      </form>
    </>
  );
}
