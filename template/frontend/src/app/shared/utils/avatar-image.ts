/** 头像的边长（像素）。显示处最大也就几十像素，256 足够清晰，体积又小。 */
export const AVATAR_SIZE = 256;

/** 接受的原图类型，与后端 AvatarPolicy 按文件头核实的类型一致。 */
export const ACCEPTED_AVATAR_TYPES: readonly string[] = ['image/png', 'image/jpeg', 'image/webp'];

/** 原图体积上限，只防读入巨图卡住页面；提交的是缩放后的结果，服务端另有上限。 */
export const MAX_SOURCE_AVATAR_BYTES = 10 * 1024 * 1024;

export type AvatarImageRejection = 'type' | 'size' | 'decode';

/** 选的文件不能作为头像：类型不对、原图过大，或浏览器解不开。 */
export class AvatarImageRejected extends Error {
  constructor(readonly reason: AvatarImageRejection) {
    super(`Avatar image rejected: ${reason}`);
    this.name = 'AvatarImageRejected';
  }
}

/**
 * 把用户选的图片居中裁成正方形、缩放到 {@link AVATAR_SIZE} 并编码成 data URL。在浏览器里处理，
 * 服务端不必引入图像库；优先 WebP，不支持 WebP 编码时改用 JPEG 并先铺白底（JPEG 无透明通道）。
 */
export async function prepareAvatarImage(file: File): Promise<string> {
  if (!ACCEPTED_AVATAR_TYPES.includes(file.type)) {
    throw new AvatarImageRejected('type');
  }

  if (file.size > MAX_SOURCE_AVATAR_BYTES) {
    throw new AvatarImageRejected('size');
  }

  let bitmap: ImageBitmap;
  try {
    bitmap = await createImageBitmap(file);
  } catch {
    throw new AvatarImageRejected('decode');
  }

  try {
    const side = Math.min(bitmap.width, bitmap.height);
    const sourceX = (bitmap.width - side) / 2;
    const sourceY = (bitmap.height - side) / 2;
    const draw = (background?: string) => {
      const canvas = document.createElement('canvas');
      canvas.width = AVATAR_SIZE;
      canvas.height = AVATAR_SIZE;
      const context = canvas.getContext('2d');
      if (!context) {
        throw new AvatarImageRejected('decode');
      }

      if (background) {
        context.fillStyle = background;
        context.fillRect(0, 0, AVATAR_SIZE, AVATAR_SIZE);
      }

      context.imageSmoothingQuality = 'high';
      context.drawImage(bitmap, sourceX, sourceY, side, side, 0, 0, AVATAR_SIZE, AVATAR_SIZE);
      return canvas;
    };

    const webp = draw().toDataURL('image/webp', 0.85);
    return webp.startsWith('data:image/webp')
      ? webp
      : draw('#ffffff').toDataURL('image/jpeg', 0.85);
  } finally {
    bitmap.close();
  }
}

/** 这个值能直接放进 `<img src>`：上传后的站内地址（`/api/v1/users/{id}/avatar?v=…`）、外部地址或尚未提交的 data URL。 */
export function isAvatarImageUrl(value: string | null | undefined): boolean {
  return (
    !!value &&
    (value.startsWith('data:image/') ||
      value.startsWith('http://') ||
      value.startsWith('https://') ||
      value.startsWith('/'))
  );
}
