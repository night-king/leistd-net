import { defineConfig, devices } from '@playwright/test';

// 端到端测试：真实 PostgreSQL（Testcontainers）+ Release 构建的 API 托管生产构建的前端（同源，与部署形态一致）。
// 环境由 e2e/global-setup.ts 起停；前提是已执行后端 Release 构建与前端生产构建（scripts/verify.ps1 的前序步骤）。
// 地址与账号由全局准备写入环境变量，测试进程按同一份配置读取。
export default defineConfig({
  testDir: './e2e',
  testMatch: '**/*.e2e.ts',
  globalSetup: './e2e/global-setup.ts',
  forbidOnly: !!process.env['CI'],
  retries: 0,
  timeout: 30_000,
  reporter: [['list'], ['html', { open: 'never' }]],
  use: {
    baseURL: process.env['E2E_BASE_URL'],
    // 文案按英文断言；本地化项目也按浏览器语言选英文
    locale: 'en-US',
    trace: 'retain-on-failure',
    screenshot: 'only-on-failure',
  },
  projects: [{ name: 'chromium', use: { ...devices['Desktop Chrome'] } }],
});
