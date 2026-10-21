/**
 * 存在 sessionStorage 里的字符串映射，每次读写都直达存储、不在内存里缓存：同一页面刷新后仍读得到，
 * 模拟后端数据库里持久的那部分状态；新页面的初始内容与该页的 Mock 会话同属一套存储（见 current-user.ts）。
 * 只实现 Mock 用到的几个 Map 方法。
 */
export class StoredMap {
  constructor(private readonly storageKey: string) {}

  get size(): number {
    return Object.keys(this.read()).length;
  }

  get(key: string): string | undefined {
    return this.read()[key];
  }

  has(key: string): boolean {
    return key in this.read();
  }

  set(key: string, value: string): this {
    this.write({ ...this.read(), [key]: value });
    return this;
  }

  delete(key: string): boolean {
    const entries = this.read();
    if (!(key in entries)) {
      return false;
    }
    delete entries[key];
    this.write(entries);
    return true;
  }

  clear(): void {
    sessionStorage.removeItem(this.storageKey);
  }

  private read(): Record<string, string> {
    const raw = sessionStorage.getItem(this.storageKey);
    return raw ? (JSON.parse(raw) as Record<string, string>) : {};
  }

  private write(entries: Record<string, string>): void {
    sessionStorage.setItem(this.storageKey, JSON.stringify(entries));
  }
}
