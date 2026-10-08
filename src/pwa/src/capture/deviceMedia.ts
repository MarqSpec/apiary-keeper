// What the capture screen asks of the phone's camera files: a photo shrunk before it leaves the device and a video
// held to a minute (NFR-4). The browser's decoding is behind small seams so the rules are unit-tested.
// reference: REQUIREMENTS.md NFR-4

/** The longest edge, in pixels, a photo is uploaded at: sharp enough to read brood on a phone, small enough for a field link. */
export const maxPhotoEdge = 1600;
const photoQuality = 0.8;

/** The longest video the screen accepts, in seconds (NFR-4). */
export const maxVideoSeconds = 60;

/** `width` by `height` scaled down, keeping its shape, until its longer edge is `maxEdge`; never scaled up. */
export function fitWithin(width: number, height: number, maxEdge: number): { width: number; height: number } {
  const scale = Math.min(1, maxEdge / Math.max(width, height));
  return { width: Math.max(1, Math.round(width * scale)), height: Math.max(1, Math.round(height * scale)) };
}

/** True for a video longer than the cap, or whose length the phone could not report. */
export function videoTooLong(seconds: number): boolean {
  return !Number.isFinite(seconds) || seconds > maxVideoSeconds;
}

/** A photo the phone has decoded, ready to be drawn at another size. */
export interface DecodedPhoto {
  readonly width: number;
  readonly height: number;
  encode(width: number, height: number): Promise<Blob | null>;
  close(): void;
}

/** The phone could not read the file as a photo it can shrink. */
export class PhotoUnreadable extends Error {
  constructor() {
    super('The photo could not be read.');
    this.name = 'PhotoUnreadable';
  }
}

/** Decodes a camera file with the browser. */
export async function decodeInBrowser(file: Blob): Promise<DecodedPhoto> {
  const bitmap = await createImageBitmap(file);
  return {
    width: bitmap.width,
    height: bitmap.height,
    close: () => bitmap.close(),
    encode(width, height) {
      const canvas = document.createElement('canvas');
      canvas.width = width;
      canvas.height = height;
      canvas.getContext('2d')?.drawImage(bitmap, 0, 0, width, height);
      return new Promise((resolve) => canvas.toBlob(resolve, 'image/jpeg', photoQuality));
    },
  };
}

/** A JPEG of `file` no larger than `maxPhotoEdge` on its long edge. The original is never uploaded. */
export async function resizePhoto(file: Blob, decode: (file: Blob) => Promise<DecodedPhoto> = decodeInBrowser): Promise<Blob> {
  let photo: DecodedPhoto;
  try {
    photo = await decode(file);
  } catch {
    throw new PhotoUnreadable();
  }
  try {
    const { width, height } = fitWithin(photo.width, photo.height, maxPhotoEdge);
    const resized = await photo.encode(width, height);
    if (resized === null) throw new PhotoUnreadable();
    return resized;
  } finally {
    photo.close();
  }
}

/** The length of a video file in seconds, read from its metadata; `NaN` when the phone cannot say. */
export function videoSecondsInBrowser(file: Blob): Promise<number> {
  return new Promise((resolve) => {
    const url = URL.createObjectURL(file);
    const video = document.createElement('video');
    const done = (seconds: number) => {
      URL.revokeObjectURL(url);
      video.removeAttribute('src');
      resolve(seconds);
    };
    video.preload = 'metadata';
    video.onloadedmetadata = () => done(video.duration);
    video.onerror = () => done(Number.NaN);
    video.src = url;
  });
}

/** What the capture screen uses of the device; tests supply their own where jsdom has no camera files to decode. */
export interface DeviceMedia {
  /** The photo shrunk for upload, as a JPEG. */
  preparePhoto(file: Blob): Promise<Blob>;
  videoSeconds(file: Blob): Promise<number>;
  /** Milliseconds on a monotonic clock. */
  now(): number;
}

export const browserMedia: DeviceMedia = {
  preparePhoto: (file) => resizePhoto(file),
  videoSeconds: videoSecondsInBrowser,
  now: () => performance.now(),
};
