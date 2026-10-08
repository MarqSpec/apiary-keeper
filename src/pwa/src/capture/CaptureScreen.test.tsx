import { screen, within } from '@testing-library/react';
import { http, HttpResponse } from 'msw';
import { describe, expect, it } from 'vitest';
import type { InspectionInput } from '../api/inspections';
import { colonyA01, inspectionOf, noAccount, problem, stubApi } from '../test/fixtures';
import { renderScreen } from '../test/render';
import { server } from '../test/server';
import { todayIso } from '../format';
import { CaptureScreen } from './CaptureScreen';
import { PhotoUnreadable, type DeviceMedia } from './deviceMedia';
import { readReadings } from './captureTiming';

const bucket = 'https://bucket.example.test';

/** A phone with no camera: photos "shrink" to a small JPEG, videos last as long as their name says, a clock the test sets. */
function fakePhone(options: { clock?: number[]; unreadable?: boolean } = {}): DeviceMedia {
  const clock = [...(options.clock ?? [0])];
  return {
    preparePhoto: async () => {
      if (options.unreadable) throw new PhotoUnreadable();
      return new Blob(['shrunk'], { type: 'image/jpeg' });
    },
    videoSeconds: async (file) => Number.parseFloat((file as File).name),
    now: () => (clock.length > 1 ? (clock.shift() ?? 0) : (clock[0] ?? 0)),
  };
}

/** The API as the screen uses it, recording the calls in the order they arrive. */
function acceptCapture(options: { failFirstPut?: boolean } = {}) {
  const calls: string[] = [];
  const sent: { inspection?: InspectionInput; uploads: unknown[]; attached: unknown[] } = { uploads: [], attached: [] };
  let puts = 0;
  server.use(
    http.post('/api/colonies/colony-1/inspections', async ({ request }) => {
      calls.push('record');
      sent.inspection = (await request.json()) as InspectionInput;
      return HttpResponse.json(inspectionOf({ id: 'insp-1', inspectedOn: sent.inspection.inspectedOn }), { status: 201 });
    }),
    http.post('/api/inspections/insp-1/media/uploads', async ({ request }) => {
      calls.push('request-upload');
      const body = (await request.json()) as { contentType: string };
      sent.uploads.push(body);
      const key = `accounts/a1/inspections/insp-1/${String(sent.uploads.length).padStart(32, '0')}`;
      return HttpResponse.json({
        objectKey: key,
        uploadUrl: `${bucket}/${key}?X-Amz-Signature=sig`,
        method: 'PUT',
        headers: { 'Content-Type': body.contentType },
        expiresAt: '2026-10-04T12:10:00+00:00',
      });
    }),
    http.put(`${bucket}/*`, () => {
      calls.push('put');
      puts += 1;
      return options.failFirstPut && puts === 1
        ? new HttpResponse('refused', { status: 403 })
        : new HttpResponse(null, { status: 200 });
    }),
    http.post('/api/inspections/insp-1/media', async ({ request }) => {
      calls.push('attach');
      const body = (await request.json()) as { objectKey: string };
      sent.attached.push(body);
      return HttpResponse.json(
        {
          id: `media-${sent.attached.length}`,
          inspectionId: 'insp-1',
          contentType: 'image/jpeg',
          sizeBytes: 6,
          attachedAt: '2026-10-04T12:00:00+00:00',
          downloadUrl: `${bucket}/x?sig`,
          downloadUrlExpiresAt: '2026-10-04T12:15:00+00:00',
        },
        { status: 201 },
      );
    }),
  );
  return { calls, sent };
}

type User = ReturnType<typeof renderScreen>['user'];

const group = (name: string) => within(screen.getByRole('group', { name }));
const tap = (user: User, groupName: string, option: string) =>
  user.click(group(groupName).getByRole('button', { name: option }));
const save = (user: User) => user.click(screen.getByRole('button', { name: 'Save inspection' }));

async function open(device: DeviceMedia = fakePhone()) {
  stubApi({ colonies: [colonyA01] });
  const view = renderScreen(<CaptureScreen colonyId="colony-1" deviceMedia={device} />, '#/colonies/colony-1/inspect');
  await screen.findByRole('heading', { name: 'Inspect A-01' });
  return view;
}

// UC-1; FR-7, FR-8; NFR-2, NFR-3, NFR-4
describe('CaptureScreen structured fields', () => {
  it('should offer today and no guessed answers when it opens', async () => {
    await open();

    expect(screen.getByLabelText(/^Day inspected/)).toHaveValue(todayIso());
    expect(screen.queryAllByRole('button', { pressed: true })).toEqual([]);
  });

  it('should record the day and one tapped answer when it is saved with a single tap', async () => {
    const { calls, sent } = acceptCapture();
    const { user } = await open();

    await tap(user, 'Queen seen', 'Yes');
    await save(user);

    expect(await screen.findByRole('heading', { name: 'Inspection saved' })).toBeInTheDocument();
    expect(sent.inspection).toEqual({ inspectedOn: todayIso(), findings: { queenSeen: true }, notes: null });
    expect(calls).toEqual(['record']);
  });

  it('should send every kind of finding with the notes trimmed when each is answered', async () => {
    const { sent } = acceptCapture();
    const { user } = await open();

    await tap(user, 'Queen seen', 'Yes');
    await tap(user, 'Eggs seen', 'No');
    await user.click(screen.getByRole('button', { name: 'More brood frames' }));
    await user.click(screen.getByRole('button', { name: 'More brood frames' }));
    await tap(user, 'Brood pattern', 'Solid');
    await tap(user, 'Food stores', 'Low');
    await tap(user, 'Temperament', 'Calm');
    await tap(user, 'Disease signs', 'Varroa seen');
    await tap(user, 'Disease signs', 'Wax moth');
    await tap(user, 'Actions taken', 'Fed');
    await user.type(screen.getByLabelText('Notes'), '  Heavy flow on huisache.  ');
    await save(user);

    await screen.findByRole('heading', { name: 'Inspection saved' });
    expect(sent.inspection).toEqual({
      inspectedOn: todayIso(),
      findings: {
        queenSeen: true,
        eggsSeen: false,
        broodFrames: 2,
        broodPattern: 'solid',
        foodStores: 'low',
        temperament: 'calm',
        diseaseSigns: ['varroa-seen', 'wax-moth'],
        actionsTaken: ['fed'],
      },
      notes: 'Heavy flow on huisache.',
    });
  });

  it('should leave a field out when its selected answer is tapped again', async () => {
    const { sent } = acceptCapture();
    const { user } = await open();

    await tap(user, 'Queen seen', 'Yes');
    await tap(user, 'Queen seen', 'Yes');
    await tap(user, 'Brood pattern', 'Spotty');
    await tap(user, 'Disease signs', 'Wax moth');
    await tap(user, 'Disease signs', 'Wax moth');
    await save(user);

    await screen.findByRole('heading', { name: 'Inspection saved' });
    expect(sent.inspection?.findings).toEqual({ broodPattern: 'spotty' });
  });

  it('should count brood frames within the API bounds and clear the count when asked', async () => {
    const { sent } = acceptCapture();
    const { user } = await open();
    const fewer = screen.getByRole('button', { name: 'Fewer brood frames' });

    expect(fewer).toBeDisabled();
    await user.click(screen.getByRole('button', { name: 'More brood frames' }));
    await user.click(fewer);
    expect(screen.getByTestId('brood-frames')).toHaveTextContent('0');
    expect(fewer).toBeDisabled();
    await user.click(screen.getByRole('button', { name: 'Clear brood frames' }));
    expect(screen.getByTestId('brood-frames')).toHaveTextContent('Not counted');
    await tap(user, 'Queen seen', 'No');
    await save(user);

    await screen.findByRole('heading', { name: 'Inspection saved' });
    expect(sent.inspection?.findings).toEqual({ queenSeen: false });
  });

  it('should say nothing was recorded and send nothing when saved with no answer and no note', async () => {
    const { calls } = acceptCapture();
    const { user } = await open();

    await save(user);

    expect(await screen.findByText(/Tap at least one answer or write a note/)).toBeInTheDocument();
    expect(calls).toEqual([]);
  });

  it('should record a note alone when nothing was answered', async () => {
    const { sent } = acceptCapture();
    const { user } = await open();

    await user.type(screen.getByLabelText('Notes'), 'Gate was open');
    await save(user);

    await screen.findByRole('heading', { name: 'Inspection saved' });
    expect(sent.inspection).toEqual({ inspectedOn: todayIso(), findings: {}, notes: 'Gate was open' });
  });

  it('should show the API refusal on the day field when the day is not allowed', async () => {
    server.use(
      http.post('/api/colonies/colony-1/inspections', () =>
        problem(400, 'Validation: inspectedOn is later than today.', {
          inspectedOn: ['Validation: it is later than today.'],
        }),
      ),
    );
    const { user } = await open();

    await tap(user, 'Queen seen', 'Yes');
    await save(user);

    expect(await screen.findByText('it is later than today.')).toBeInTheDocument();
    expect(screen.getByLabelText(/^Day inspected/)).toBeInvalid();
  });

  it('should explain the server when the server has no sign-in (so no account) and keep the answers', async () => {
    server.use(http.post('/api/colonies/colony-1/inspections', noAccount));
    const { user } = await open();

    await tap(user, 'Queen seen', 'Yes');
    await save(user);

    expect(await screen.findByText('The server is not ready')).toBeInTheDocument();
    expect(group('Queen seen').getByRole('button', { name: 'Yes' })).toHaveAttribute('aria-pressed', 'true');
  });

  it('should explain the colony is not found when it does not exist', async () => {
    stubApi({});
    renderScreen(<CaptureScreen colonyId="nope" deviceMedia={fakePhone()} />, '#/colonies/nope/inspect');

    expect(await screen.findByText('Not found')).toBeInTheDocument();
  });
});

describe('CaptureScreen photos and video', () => {
  const photo = new File(['raw camera bytes'], 'IMG_0001.jpg', { type: 'image/jpeg' });

  it('should upload a photo resized, straight to the bucket, and attach it before it says saved', async () => {
    const { calls, sent } = acceptCapture();
    const { user } = await open();

    await user.upload(screen.getByLabelText('Add photo'), photo);
    await tap(user, 'Queen seen', 'Yes');
    await save(user);

    expect(await screen.findByRole('heading', { name: 'Inspection saved' })).toBeInTheDocument();
    expect(calls).toEqual(['record', 'request-upload', 'put', 'attach']);
    // The declared size is the shrunk file's (6 bytes), not the camera original's.
    expect(sent.uploads).toEqual([{ contentType: 'image/jpeg', sizeBytes: 6 }]);
    expect(sent.attached).toEqual([{ objectKey: `accounts/a1/inspections/insp-1/${'1'.padStart(32, '0')}` }]);
  });

  it('should not show saved while a file is still uploading', async () => {
    acceptCapture();
    let release: () => void = () => {};
    const gate = new Promise<void>((resolve) => (release = resolve));
    server.use(
      http.put(`${bucket}/*`, async () => {
        await gate;
        return new HttpResponse(null, { status: 200 });
      }),
    );
    const { user } = await open();

    await user.upload(screen.getByLabelText('Add photo'), photo);
    await tap(user, 'Queen seen', 'Yes');
    await save(user);

    expect(await screen.findByText(/· Uploading/)).toBeInTheDocument();
    expect(screen.queryByRole('heading', { name: 'Inspection saved' })).not.toBeInTheDocument();
    release();
    expect(await screen.findByRole('heading', { name: 'Inspection saved' })).toBeInTheDocument();
  });

  it('should not show saved until the API has attached the file', async () => {
    acceptCapture();
    let release: () => void = () => {};
    const gate = new Promise<void>((resolve) => (release = resolve));
    server.use(
      http.post('/api/inspections/insp-1/media', async () => {
        await gate;
        return HttpResponse.json(
          {
            id: 'media-1',
            inspectionId: 'insp-1',
            contentType: 'image/jpeg',
            sizeBytes: 6,
            attachedAt: '2026-10-04T12:00:00+00:00',
            downloadUrl: `${bucket}/x?sig`,
            downloadUrlExpiresAt: '2026-10-04T12:15:00+00:00',
          },
          { status: 201 },
        );
      }),
    );
    const { user } = await open();

    await user.upload(screen.getByLabelText('Add photo'), photo);
    await tap(user, 'Queen seen', 'Yes');
    await save(user);

    await screen.findByText(/· Uploading/);
    expect(screen.queryByRole('heading', { name: 'Inspection saved' })).not.toBeInTheDocument();
    release();
    expect(await screen.findByRole('heading', { name: 'Inspection saved' })).toBeInTheDocument();
  });

  it('should accept a video of exactly a minute and upload it as the type the phone gave', async () => {
    const { sent } = acceptCapture();
    const { user } = await open();

    await user.upload(screen.getByLabelText(/^Add video/), new File(['clip'], '60.mp4', { type: 'video/mp4' }));
    expect(await screen.findByText('60.mp4')).toBeInTheDocument();
    await tap(user, 'Queen seen', 'Yes');
    await save(user);

    await screen.findByRole('heading', { name: 'Inspection saved' });
    expect(sent.uploads).toEqual([{ contentType: 'video/mp4', sizeBytes: 4 }]);
  });

  it('should refuse a video longer than a minute and never upload it', async () => {
    const { calls } = acceptCapture();
    const { user } = await open();

    await user.upload(screen.getByLabelText(/^Add video/), new File(['clip'], '61.mp4', { type: 'video/mp4' }));

    expect(await screen.findByText(/longer than 1 minute/)).toBeInTheDocument();
    expect(screen.queryByText('61.mp4')).not.toBeInTheDocument();
    await tap(user, 'Queen seen', 'Yes');
    await save(user);
    await screen.findByRole('heading', { name: 'Inspection saved' });
    expect(calls).toEqual(['record']);
  });

  it('should say the photo could not be read and not queue it when the phone cannot decode it', async () => {
    const { user } = await open(fakePhone({ unreadable: true }));

    await user.upload(screen.getByLabelText('Add photo'), photo);

    expect(await screen.findByText(/could not be read/)).toBeInTheDocument();
    expect(screen.queryByText('IMG_0001.jpg')).not.toBeInTheDocument();
  });

  it('should drop a queued file when it is removed before saving', async () => {
    const { calls } = acceptCapture();
    const { user } = await open();

    await user.upload(screen.getByLabelText('Add photo'), photo);
    await user.click(await screen.findByRole('button', { name: 'Remove IMG_0001.jpg' }));
    await tap(user, 'Queen seen', 'Yes');
    await save(user);

    await screen.findByRole('heading', { name: 'Inspection saved' });
    expect(calls).toEqual(['record']);
  });

  it('should keep the recorded inspection and retry only the file that failed with a fresh URL', async () => {
    const { calls, sent } = acceptCapture({ failFirstPut: true });
    const { user } = await open();

    await user.upload(screen.getByLabelText('Add photo'), photo);
    await tap(user, 'Queen seen', 'Yes');
    await save(user);

    expect(await screen.findByText(/1 file did not upload/)).toBeInTheDocument();
    expect(screen.queryByRole('heading', { name: 'Inspection saved' })).not.toBeInTheDocument();
    expect(calls).toEqual(['record', 'request-upload', 'put']);

    await user.click(screen.getByRole('button', { name: 'Try the uploads again' }));

    expect(await screen.findByRole('heading', { name: 'Inspection saved' })).toBeInTheDocument();
    expect(calls).toEqual(['record', 'request-upload', 'put', 'request-upload', 'put', 'attach']);
    expect(sent.uploads).toHaveLength(2);
  });

  it('should show the API reason on a video it refuses and offer no retry that cannot succeed', async () => {
    const { calls } = acceptCapture();
    server.use(
      http.post('/api/inspections/insp-1/media/uploads', async ({ request }) => {
        const body = (await request.json()) as { contentType: string };
        calls.push('request-upload');
        return body.contentType.startsWith('video/')
          ? problem(400, 'Validation: the video is larger than 200 MiB.', { sizeBytes: ['Validation: larger than 200 MiB.'] })
          : HttpResponse.json({
              objectKey: 'accounts/a1/inspections/insp-1/' + '2'.padStart(32, '0'),
              uploadUrl: bucket + '/photo?X-Amz-Signature=sig',
              method: 'PUT',
              headers: { 'Content-Type': 'image/jpeg' },
              expiresAt: '2026-10-04T12:10:00+00:00',
            });
      }),
    );
    const { user } = await open();

    await user.upload(screen.getByLabelText(/^Add video/), new File(['clip'], '30.mp4', { type: 'video/mp4' }));
    await user.upload(screen.getByLabelText('Add photo'), photo);
    await tap(user, 'Queen seen', 'Yes');
    await save(user);

    expect(await screen.findByText(/The video is larger than 200 MiB/)).toBeInTheDocument();
    expect(screen.getByText(/1 file was refused/)).toBeInTheDocument();
    expect(screen.getByText(/Photo · Uploaded/)).toBeInTheDocument();
    expect(screen.queryByRole('button', { name: 'Try the uploads again' })).not.toBeInTheDocument();
    expect(screen.queryByRole('heading', { name: 'Inspection saved' })).not.toBeInTheDocument();
    await user.click(screen.getByRole('button', { name: 'Leave without them' }));
    expect(window.location.hash).toBe('#/colonies/colony-1');
  });

  it('should retry only the file that failed in transit and never send the refused one again', async () => {
    acceptCapture({ failFirstPut: true });
    let videoRequests = 0;
    server.use(
      http.post('/api/inspections/insp-1/media/uploads', async ({ request }) => {
        const body = (await request.json()) as { contentType: string };
        if (body.contentType.startsWith('video/')) videoRequests += 1;
        return body.contentType.startsWith('video/')
          ? problem(400, 'Validation: the video is larger than 200 MiB.')
          : HttpResponse.json({
              objectKey: 'accounts/a1/inspections/insp-1/' + '3'.padStart(32, '0'),
              uploadUrl: bucket + '/photo?X-Amz-Signature=sig',
              method: 'PUT',
              headers: { 'Content-Type': 'image/jpeg' },
              expiresAt: '2026-10-04T12:10:00+00:00',
            });
      }),
    );
    const { user } = await open();

    await user.upload(screen.getByLabelText(/^Add video/), new File(['clip'], '30.mp4', { type: 'video/mp4' }));
    await user.upload(screen.getByLabelText('Add photo'), photo);
    await tap(user, 'Queen seen', 'Yes');
    await save(user);

    await user.click(await screen.findByRole('button', { name: 'Try the uploads again' }));

    expect(await screen.findByText(/Photo · Uploaded/)).toBeInTheDocument();
    expect(videoRequests).toBe(1);
    expect(screen.queryByRole('heading', { name: 'Inspection saved' })).not.toBeInTheDocument();
  });

  it('should leave for the colony without the failed files when asked, the inspection already recorded', async () => {
    acceptCapture({ failFirstPut: true });
    const { user } = await open();

    await user.upload(screen.getByLabelText('Add photo'), photo);
    await tap(user, 'Queen seen', 'Yes');
    await save(user);
    await user.click(await screen.findByRole('button', { name: 'Leave without them' }));

    expect(window.location.hash).toBe('#/colonies/colony-1');
  });
});

describe('CaptureScreen timing', () => {
  it('should record the time from opening to save and to saved, and show the second', async () => {
    acceptCapture();
    // opened at 1 s, Save tapped at 21.04 s, saved at 26.5 s
    const { user } = await open(fakePhone({ clock: [1_000, 21_040, 26_500] }));

    await tap(user, 'Queen seen', 'Yes');
    await save(user);

    expect(await screen.findByText('Saved 25.5 s after you opened this screen.')).toBeInTheDocument();
    expect(readReadings(window.localStorage)).toEqual([
      expect.objectContaining({ openToSaveSeconds: 20, openToSavedSeconds: 25.5 }),
    ]);
  });

  it('should record nothing when the save fails', async () => {
    server.use(http.post('/api/colonies/colony-1/inspections', noAccount));
    const { user } = await open();

    await tap(user, 'Queen seen', 'Yes');
    await save(user);
    await screen.findByText('The server is not ready');

    expect(readReadings(window.localStorage)).toEqual([]);
  });

  it('should return to the colony from the saved screen', async () => {
    acceptCapture();
    const { user } = await open();

    await tap(user, 'Queen seen', 'Yes');
    await save(user);
    await user.click(await screen.findByRole('link', { name: 'View A-01' }));

    expect(window.location.hash).toBe('#/colonies/colony-1');
  });
});
