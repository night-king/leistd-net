/** 把内容存成本地文件（触发浏览器下载）；用完即 `revokeObjectURL`，否则每次下载都泄漏一份 blob。 */
export function saveBlob(blob: Blob, fileName: string): void {
  const url = URL.createObjectURL(blob);
  const anchor = document.createElement('a');
  anchor.href = url;
  anchor.download = fileName;
  anchor.click();
  URL.revokeObjectURL(url);
}
