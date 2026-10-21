import {
  AVATAR_SIZE,
  AvatarImageRejected,
  isAvatarImageUrl,
  MAX_SOURCE_AVATAR_BYTES,
  prepareAvatarImage,
} from './avatar-image';

/**
 * 画一张指定尺寸的 PNG 当作原图。用同步的 toDataURL：toBlob 在主线程繁忙时可能超过 Vitest
 * 默认 5s 超时。
 */
function pngFile(
  width: number,
  height: number,
  paint: (context: CanvasRenderingContext2D) => void = (context) => {
    context.fillStyle = '#3366cc';
    context.fillRect(0, 0, width, height);
  },
): File {
  const canvas = document.createElement('canvas');
  canvas.width = width;
  canvas.height = height;
  const context = canvas.getContext('2d')!;
  paint(context);
  const base64 = canvas.toDataURL('image/png').split(',')[1];
  const bytes = Uint8Array.from(atob(base64), (char) => char.charCodeAt(0));
  return new File([bytes], 'photo.png', { type: 'image/png' });
}

function loadImage(src: string): Promise<HTMLImageElement> {
  return new Promise((resolve, reject) => {
    const image = new Image();
    image.onload = () => resolve(image);
    image.onerror = reject;
    image.src = src;
  });
}

/** 读出图片上一个像素的 RGB。 */
function pixelAt(image: HTMLImageElement, x: number, y: number): [number, number, number] {
  const canvas = document.createElement('canvas');
  canvas.width = image.naturalWidth;
  canvas.height = image.naturalHeight;
  const context = canvas.getContext('2d')!;
  context.drawImage(image, 0, 0);
  const [r, g, b] = context.getImageData(x, y, 1, 1).data;
  return [r, g, b];
}

describe('prepareAvatarImage', () => {
  it('center-crops a non-square source to a square and scales it to a fixed size', async () => {
    // 左右两侧各 200px 涂成红与蓝，中间 400×400 是绿：居中裁切后整张图只剩绿色，
    // 取的是左上角（偏移为 0）时左边缘会是红色
    const source = pngFile(800, 400, (context) => {
      context.fillStyle = '#ff0000';
      context.fillRect(0, 0, 200, 400);
      context.fillStyle = '#00ff00';
      context.fillRect(200, 0, 400, 400);
      context.fillStyle = '#0000ff';
      context.fillRect(600, 0, 200, 400);
    });
    const dataUrl = await prepareAvatarImage(source);

    expect(dataUrl).toMatch(/^data:image\/(webp|jpeg);base64,/);
    const image = await loadImage(dataUrl);
    expect([image.naturalWidth, image.naturalHeight]).toEqual([AVATAR_SIZE, AVATAR_SIZE]);
    // 离边缘留几个像素：缩放平滑与有损编码会让紧贴边界的像素混色
    const middle = AVATAR_SIZE / 2;
    for (const x of [4, AVATAR_SIZE - 5]) {
      const [r, g, b] = pixelAt(image, x, middle);
      expect({ x, r: r < 64, g: g > 192, b: b < 64 }).toEqual({ x, r: true, g: true, b: true });
    }
  });

  it('rejects unaccepted types without decoding them', async () => {
    const file = new File(['<svg/>'], 'logo.svg', { type: 'image/svg+xml' });

    await expect(prepareAvatarImage(file)).rejects.toEqual(new AvatarImageRejected('type'));
  });

  it('rejects an oversized source', async () => {
    const file = new File([new Uint8Array(MAX_SOURCE_AVATAR_BYTES + 1)], 'huge.png', {
      type: 'image/png',
    });

    await expect(prepareAvatarImage(file)).rejects.toEqual(new AvatarImageRejected('size'));
  });

  it('rejects as a decode failure when a declared image cannot be decoded', async () => {
    const file = new File(['not an image'], 'fake.png', { type: 'image/png' });

    await expect(prepareAvatarImage(file)).rejects.toEqual(new AvatarImageRejected('decode'));
  });
});

describe('isAvatarImageUrl', () => {
  it('recognizes site avatar URLs, external URLs and data URLs', () => {
    expect(isAvatarImageUrl('/api/v1/users/1/avatar?v=abc')).toBe(true);
    expect(isAvatarImageUrl('https://example.com/a.png')).toBe(true);
    expect(isAvatarImageUrl('data:image/webp;base64,AAAA')).toBe(true);
    expect(isAvatarImageUrl('')).toBe(false);
    expect(isAvatarImageUrl(undefined)).toBe(false);
  });
});
