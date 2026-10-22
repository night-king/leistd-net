import { beforeAll } from 'vitest';

// 网页字体用到时才加载：不预载，首次量宽、量行数量到的是后备字体（CI 的 Linux 后备字体更宽）。
// 每个 spec 文件开始前加载样式表登记的全部字体，用例里量到的就是生产字体，不必再逐个等字体。
beforeAll(async () => {
  await Promise.all([...document.fonts].map((face) => face.load()));
});
