import { http, HttpResponse } from 'msw';
import { describe, expect, it } from 'vitest';
import { problem } from '../test/fixtures';
import { server } from '../test/server';
import { ApiError } from './client';
import { attachMedia, putToBucket, requestMediaUpload, type MediaUpload } from './media';

const upload: MediaUpload = {
  objectKey: 'accounts/a1/inspections/i1/0123456789abcdef0123456789abcdef',
  uploadUrl: 'https://bucket.example.test/accounts/a1/inspections/i1/0123456789abcdef0123456789abcdef?X-Amz-Signature=sig',
  method: 'PUT',
  headers: { 'Content-Type': 'image/jpeg' },
  expiresAt: '2026-10-04T12:10:00+00:00',
};

// reference: INTERFACES.md D.1 *Media endpoints*, F.3
describe('requestMediaUpload', () => {
  it('should post the declared type and size and read the upload when the API signs a URL', async () => {
    let sent: unknown;
    server.use(
      http.post('/api/inspections/i%2F1/media/uploads', async ({ request }) => {
        sent = await request.json();
        return HttpResponse.json(upload);
      }),
    );

    const result = await requestMediaUpload('i/1', { contentType: 'image/jpeg', sizeBytes: 1234 });

    expect(sent).toEqual({ contentType: 'image/jpeg', sizeBytes: 1234 });
    expect(result).toEqual(upload);
  });

  it('should refuse an upload whose method is not PUT when the API changes the contract', async () => {
    server.use(http.post('/api/inspections/i1/media/uploads', () => HttpResponse.json({ ...upload, method: 'POST' })));

    await expect(requestMediaUpload('i1', { contentType: 'image/jpeg', sizeBytes: 1 })).rejects.toMatchObject({
      failure: { kind: 'unexpected' },
    });
  });

  it('should fail as unavailable when no bucket is configured', async () => {
    server.use(http.post('/api/inspections/i1/media/uploads', () => problem(503, 'Unavailable: no media bucket is configured.')));

    await expect(requestMediaUpload('i1', { contentType: 'image/jpeg', sizeBytes: 1 })).rejects.toMatchObject({
      failure: { kind: 'unavailable' },
    });
  });
});

describe('putToBucket', () => {
  it('should send the bytes with exactly the headers the upload names when it uploads', async () => {
    let seen: { method: string; type: string | null; size: number } | undefined;
    server.use(
      http.put('https://bucket.example.test/*', async ({ request }) => {
        seen = { method: request.method, type: request.headers.get('Content-Type'), size: (await request.arrayBuffer()).byteLength };
        return new HttpResponse(null, { status: 200 });
      }),
    );

    await putToBucket(upload, new Blob([new Uint8Array(10)], { type: 'image/jpeg' }));

    expect(seen).toEqual({ method: 'PUT', type: 'image/jpeg', size: 10 });
  });

  it('should fail as unreachable when the bucket refuses the upload', async () => {
    server.use(http.put('https://bucket.example.test/*', () => new HttpResponse('expired', { status: 403 })));

    await expect(putToBucket(upload, new Blob(['x']))).rejects.toSatisfy(
      (error) => error instanceof ApiError && error.failure.kind === 'unreachable',
    );
  });

  it('should fail as unreachable when the network fails', async () => {
    server.use(http.put('https://bucket.example.test/*', () => HttpResponse.error()));

    await expect(putToBucket(upload, new Blob(['x']))).rejects.toMatchObject({ failure: { kind: 'unreachable' } });
  });
});

describe('attachMedia', () => {
  it('should post the object key and read the media when the file is attached', async () => {
    let sent: unknown;
    server.use(
      http.post('/api/inspections/i1/media', async ({ request }) => {
        sent = await request.json();
        return HttpResponse.json(
          {
            id: 'm1',
            inspectionId: 'i1',
            contentType: 'image/jpeg',
            sizeBytes: 10,
            attachedAt: '2026-10-04T12:00:00+00:00',
            downloadUrl: 'https://bucket.example.test/x?sig',
            downloadUrlExpiresAt: '2026-10-04T12:15:00+00:00',
          },
          { status: 201 },
        );
      }),
    );

    const media = await attachMedia('i1', upload.objectKey);

    expect(sent).toEqual({ objectKey: upload.objectKey });
    expect(media.id).toBe('m1');
  });
});
