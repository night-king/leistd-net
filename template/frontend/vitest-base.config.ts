import { defineConfig } from 'vitest/config';

// 由 angular.json 的 test.options.runnerConfig 加载；构建、浏览器与用例范围仍由 Angular 的 unit-test 构建器决定。
export default defineConfig({
  test: {
    // 每条用例开始前还原 vi.spyOn 创建的替身与 vi.stubGlobal 替换的全局值：
    // 用例改了原型方法、console 或 URL 静态方法时，不能漏到后面的用例里。
    // 只管这两种；Object.defineProperty 之类的直接修改与假计时器仍由用例自己还原
    restoreMocks: true,
    unstubGlobals: true,
  },
});
