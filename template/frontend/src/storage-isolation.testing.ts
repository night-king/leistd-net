import { beforeEach } from 'vitest';

// 每个 spec 文件在独立页面里执行，但同源的 localStorage、sessionStorage 在整次运行中共享：
// 一个文件存下的设备语言、租户提示会被后面的文件读到，结果随文件执行顺序变化。
// 每条用例开始前清空，用例需要的存储状态由用例自己写入。
beforeEach(() => {
  localStorage.clear();
  sessionStorage.clear();
});
