import { createHmac } from 'node:crypto';

const STEP_SECONDS = 30;
const BASE32 = 'ABCDEFGHIJKLMNOPQRSTUVWXYZ234567';

/**
 * RFC 6238（HMAC-SHA1、30 秒、6 位）：与身份验证器应用同一算法，由页面显示的 Base32 密钥算出验证码。
 * 页面把密钥按四位分组显示，空格在这里去掉。
 */
export function totp(secret: string, at = Date.now()): string {
  const key: number[] = [];
  let buffer = 0;
  let bits = 0;
  for (const char of secret.replace(/[\s=-]/g, '').toUpperCase()) {
    const index = BASE32.indexOf(char);
    if (index < 0) throw new Error('The secret is not Base32.');
    buffer = ((buffer << 5) | index) & 0xffff;
    bits += 5;
    if (bits >= 8) {
      bits -= 8;
      key.push((buffer >> bits) & 0xff);
    }
  }
  const counter = Buffer.alloc(8);
  counter.writeBigUInt64BE(BigInt(Math.floor(at / 1000 / STEP_SECONDS)));
  const hash = createHmac('sha1', Buffer.from(key)).update(counter).digest();
  const offset = hash[hash.length - 1]! & 0x0f;
  return ((hash.readUInt32BE(offset) & 0x7fffffff) % 1_000_000).toString().padStart(6, '0');
}

/** 当前时间步只剩几秒时等到下一步再算，避免验证码刚提交就过期。 */
export async function freshTotp(secret: string): Promise<string> {
  const left = STEP_SECONDS - ((Date.now() / 1000) % STEP_SECONDS);
  if (left < 3) await new Promise((wait) => setTimeout(wait, left * 1000 + 100));
  return totp(secret);
}
