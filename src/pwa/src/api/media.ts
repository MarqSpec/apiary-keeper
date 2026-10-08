// The media endpoints (FR-8) and the bucket itself: the bytes go from the phone to the bucket on a presigned URL and
// never through the API (D-13).
// reference: INTERFACES.md D.1 *Media endpoints*, F.3
import { z } from 'zod';
import { ApiError, apiRequest } from './client';

export const mediaUploadSchema = z.object({
  objectKey: z.string(),
  uploadUrl: z.url(),
  method: z.literal('PUT'),
  headers: z.record(z.string(), z.string()),
  expiresAt: z.string(),
});

export const mediaSchema = z.object({
  id: z.string(),
  inspectionId: z.string(),
  contentType: z.string(),
  sizeBytes: z.number().int(),
  attachedAt: z.string(),
  downloadUrl: z.string(),
  downloadUrlExpiresAt: z.string(),
});

export type MediaUpload = z.infer<typeof mediaUploadSchema>;
export type Media = z.infer<typeof mediaSchema>;

const mediaPath = (inspectionId: string) => `/api/inspections/${encodeURIComponent(inspectionId)}/media`;

/** Asks for a URL to upload one file of exactly `sizeBytes` and `contentType` to; nothing is recorded yet. */
export function requestMediaUpload(
  inspectionId: string,
  file: { readonly contentType: string; readonly sizeBytes: number },
): Promise<MediaUpload> {
  return apiRequest(`${mediaPath(inspectionId)}/uploads`, mediaUploadSchema, { method: 'POST', body: file });
}

/** Attaches the object an upload put in the bucket to the inspection it was requested for. */
export function attachMedia(inspectionId: string, objectKey: string): Promise<Media> {
  return apiRequest(mediaPath(inspectionId), mediaSchema, { method: 'POST', body: { objectKey } });
}

/**
 * Puts `body` on the presigned URL with exactly the headers the upload names. The URL is a bearer credential until it
 * expires (F.3): it is used here and never stored or logged. A refusal or a network failure is `unreachable`, which
 * the screen explains as the phone being offline and offers to try again.
 */
export async function putToBucket(upload: MediaUpload, body: Blob): Promise<void> {
  let response: Response;
  try {
    response = await fetch(upload.uploadUrl, { method: upload.method, headers: upload.headers, body });
  } catch {
    throw new ApiError({ kind: 'unreachable' });
  }
  if (!response.ok) throw new ApiError({ kind: 'unreachable' });
}
