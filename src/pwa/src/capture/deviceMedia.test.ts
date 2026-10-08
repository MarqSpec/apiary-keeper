import { describe, expect, it, vi } from 'vitest';
import {
  fitWithin,
  maxPhotoEdge,
  maxVideoSeconds,
  PhotoUnreadable,
  resizePhoto,
  videoTooLong,
  type DecodedPhoto,
} from './deviceMedia';

// reference: REQUIREMENTS.md NFR-4 (photos resized on the device, field video capped at 1 minute)
describe('fitWithin', () => {
  it('should scale the long edge down to the limit when the photo is larger', () => {
    expect(fitWithin(4000, 3000, 1600)).toEqual({ width: 1600, height: 1200 });
    expect(fitWithin(3000, 4000, 1600)).toEqual({ width: 1200, height: 1600 });
  });

  it('should leave the size alone when the photo already fits', () => {
    expect(fitWithin(1200, 800, 1600)).toEqual({ width: 1200, height: 800 });
    expect(fitWithin(1600, 1600, 1600)).toEqual({ width: 1600, height: 1600 });
  });

  it('should keep both edges at least one pixel when the photo is a thin strip', () => {
    expect(fitWithin(10000, 2, 1600)).toEqual({ width: 1600, height: 1 });
  });
});

describe('videoTooLong', () => {
  it('should allow exactly one minute and refuse anything longer', () => {
    expect(maxVideoSeconds).toBe(60);
    expect([videoTooLong(59.9), videoTooLong(60), videoTooLong(60.1)]).toEqual([false, false, true]);
  });

  it('should refuse a length that cannot be known when it is not a finite number', () => {
    expect([videoTooLong(Number.NaN), videoTooLong(Number.POSITIVE_INFINITY)]).toEqual([true, true]);
  });
});

function decoded(width: number, height: number, encoded: Blob | null = new Blob(['jpeg'], { type: 'image/jpeg' })) {
  const encode = vi.fn(() => Promise.resolve(encoded));
  const close = vi.fn();
  const photo: DecodedPhoto = { width, height, encode, close };
  return { photo, encode, close };
}

describe('resizePhoto', () => {
  it('should encode a smaller JPEG when the photo is larger than the limit', async () => {
    const { photo, encode, close } = decoded(4000, 3000);

    const result = await resizePhoto(new Blob(['x']), () => Promise.resolve(photo));

    expect(encode).toHaveBeenCalledWith(maxPhotoEdge, 1200);
    expect(result.type).toBe('image/jpeg');
    expect(close).toHaveBeenCalledOnce();
  });

  it('should still re-encode a photo that already fits when it is picked', async () => {
    const { photo, encode } = decoded(800, 600);

    await resizePhoto(new Blob(['x']), () => Promise.resolve(photo));

    expect(encode).toHaveBeenCalledWith(800, 600);
  });

  it('should fail as unreadable and release the photo when nothing can be encoded', async () => {
    const { photo, close } = decoded(800, 600, null);

    await expect(resizePhoto(new Blob(['x']), () => Promise.resolve(photo))).rejects.toBeInstanceOf(PhotoUnreadable);
    expect(close).toHaveBeenCalledOnce();
  });

  it('should fail as unreadable when the phone cannot decode the file', async () => {
    await expect(resizePhoto(new Blob(['x']), () => Promise.reject(new Error('no decoder')))).rejects.toBeInstanceOf(
      PhotoUnreadable,
    );
  });
});
