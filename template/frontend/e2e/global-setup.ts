import { type ChildProcess, execFile, execFileSync, spawn } from 'node:child_process';
import { randomBytes } from 'node:crypto';
import {
  type WriteStream,
  createWriteStream,
  existsSync,
  mkdirSync,
  mkdtempSync,
  rmSync,
} from 'node:fs';
import { createServer } from 'node:net';
import { tmpdir } from 'node:os';
import { join, resolve } from 'node:path';
import { promisify } from 'node:util';

import { request } from '@playwright/test';
import { PostgreSqlContainer, type StartedPostgreSqlContainer } from '@testcontainers/postgresql';

import {
  RESTRICTED_PERMISSIONS,
  admin,
  authStatePath,
  restricted,
  signIn,
} from './support/accounts';
import { json } from './support/http';

// 名称随项目生成；拼路径而不是写成一个长字面量，换了项目名也不改变格式化结果
const project = 'CompanyName.ProjectName';
const backend = resolve(__dirname, '../../backend/src');
const apiDll = join(backend, `${project}.Api/bin/Release/net10.0/${project}.Api.dll`);
const migratorDll = join(
  backend,
  `${project}.DbMigrator/bin/Release/net10.0/${project}.DbMigrator.dll`,
);
const site = 'companyname-projectname-web';
const webRoot = resolve(__dirname, '../dist', site, 'browser');
const outputDir = resolve(__dirname, '../test-results');

/**
 * 起一套只属于本次运行的环境：新库迁移后启动 API，API 首次启动按 DefaultAdmin 建超级管理员；
 * 受限账号与它的角色经公开管理接口创建。返回的函数在全部用例结束后清理进程、容器与临时目录。
 */
export default async function globalSetup(): Promise<() => Promise<void>> {
  for (const [file, hint] of [
    [apiDll, 'dotnet build -c Release'],
    [migratorDll, 'dotnet build -c Release'],
    [join(webRoot, 'index.html'), 'npm run build'],
  ]) {
    if (!existsSync(file)) throw new Error(`E2E needs ${file}; run '${hint}' first.`);
  }

  const workDir = mkdtempSync(join(tmpdir(), 'e2e-'));
  let database: StartedPostgreSqlContainer | undefined;
  let api: ChildProcess | undefined;
  let log: WriteStream | undefined;
  const teardown = async () => {
    // 先等 API 退出、日志关闭，再停库删目录：进程仍占着文件时 Windows 删除会报 EBUSY
    await stopProcess(api);
    await new Promise<void>((done) => (log ? log.end(done) : done()));
    await database?.stop();
    rmSync(workDir, { recursive: true, force: true });
  };

  try {
    // Testcontainers 只认 DOCKER_HOST 与默认套接字；未设置时沿用 docker CLI 当前上下文的端点（与后端集成测试一致）
    process.env['DOCKER_HOST'] ??= dockerContextHost();
    database = await new PostgreSqlContainer('postgres:18-alpine').start();
    const connection = [
      `Host=${database.getHost()}`,
      `Port=${database.getPort()}`,
      `Database=${database.getDatabase()}`,
      `Username=${database.getUsername()}`,
      `Password=${database.getPassword()}`,
    ].join(';');
    const keysPath = join(workDir, 'keys');

    await promisify(execFile)('dotnet', [migratorDll, '--apply'], {
      env: {
        ...process.env,
        DOTNET_ENVIRONMENT: 'Production',
        ConnectionStrings__Default: connection,
        DataProtection__KeysPath: keysPath,
      },
    });

    const port = await freePort();
    // 通行密钥的 RP ID 只能是域名、不能是 IP，页面走 localhost（浏览器视同安全上下文，http 也能用）
    const baseURL = `http://localhost:${port}`;
    mkdirSync(outputDir, { recursive: true });
    log = createWriteStream(join(outputDir, 'api.log'));
    api = spawn('dotnet', [apiDll], {
      cwd: resolve(apiDll, '..'),
      // 开发环境：会话 Cookie 不带 __Host- 前缀、OpenIddict 用开发证书并允许 HTTP，才能跑在本机 http 上；
      // 开发环境也会加载本机 user-secrets，上面注入的连接串与口令优先于它
      env: {
        ...process.env,
        ASPNETCORE_ENVIRONMENT: 'Development',
        ASPNETCORE_URLS: baseURL,
        ASPNETCORE_WEBROOT: webRoot,
        ConnectionStrings__Default: connection,
        ConnectionStrings__Redis: '',
        DataProtection__KeysPath: keysPath,
        DefaultAdmin__Password: admin().password,
        Leistd__Security__VerificationCodes__Key: randomBytes(32).toString('base64'),
        Passkeys__RelyingPartyId: 'localhost',
        Passkeys__Origins__0: baseURL,
      },
      stdio: ['ignore', 'pipe', 'pipe'],
    });
    api.stdout?.pipe(log, { end: false });
    api.stderr?.pipe(log, { end: false });
    await waitUntilLive(api, `${baseURL}/api/health/live`);

    process.env['E2E_BASE_URL'] = baseURL;
    await seedRestrictedAccount(baseURL);
    for (const account of [admin(), restricted()]) {
      const context = await request.newContext({ baseURL });
      await signIn(context, account);
      await context.storageState({ path: authStatePath(account) });
      await context.dispose();
    }
    return teardown;
  } catch (error) {
    await teardown();
    throw error;
  }
}

/** 受限账号：一个只持有角色查看权限的新角色。 */
async function seedRestrictedAccount(baseURL: string): Promise<void> {
  const context = await request.newContext({ baseURL });
  try {
    await signIn(context, admin());
    const role = await json<{ id: string }>(
      await context.post('/api/v1/roles', {
        data: { name: 'e2e_viewer', displayName: 'E2E viewer' },
      }),
    );
    const grants = await json<{ version: number }>(
      await context.get(`/api/v1/permissions/grants/roles/${role.id}`),
    );
    await json(
      await context.put(`/api/v1/permissions/grants/roles/${role.id}`, {
        data: { expectedVersion: grants.version, permissionNames: RESTRICTED_PERMISSIONS },
      }),
    );
    await json(
      await context.post('/api/v1/users', {
        data: {
          username: restricted().username,
          email: `${restricted().username}@example.com`,
          password: restricted().password,
          isEmailVerified: true,
          roleIds: [role.id],
        },
      }),
    );
  } finally {
    await context.dispose();
  }
}

/** 请求退出并等它结束；超时后强制结束，不让清理卡住。 */
async function stopProcess(child: ChildProcess | undefined): Promise<void> {
  if (!child || child.exitCode !== null || child.signalCode !== null) return;
  const exited = new Promise<void>((done) => child.once('exit', () => done()));
  child.kill();
  const timer = setTimeout(() => child.kill('SIGKILL'), 10_000);
  await exited;
  clearTimeout(timer);
}

function freePort(): Promise<number> {
  return new Promise((resolvePort, reject) => {
    const server = createServer();
    server.once('error', reject);
    server.listen(0, '127.0.0.1', () => {
      const address = server.address();
      server.close(() =>
        typeof address === 'object' && address
          ? resolvePort(address.port)
          : reject(new Error('No free port')),
      );
    });
  });
}

async function waitUntilLive(api: ChildProcess, url: string): Promise<void> {
  const deadline = Date.now() + 120_000;
  while (Date.now() < deadline) {
    if (api.exitCode !== null) {
      throw new Error(`API exited with code ${api.exitCode}; see test-results/api.log.`);
    }
    try {
      if ((await fetch(url)).ok) return;
    } catch {
      // 尚未监听
    }
    await new Promise((wait) => setTimeout(wait, 500));
  }
  throw new Error(`API did not become live within 120 s; see test-results/api.log.`);
}

function dockerContextHost(): string | undefined {
  try {
    const host = execFileSync(
      'docker',
      ['context', 'inspect', '--format', '{{.Endpoints.docker.Host}}'],
      {
        encoding: 'utf8',
        stdio: ['ignore', 'pipe', 'ignore'],
      },
    ).trim();
    return host || undefined;
  } catch {
    // 没有 docker CLI（如 CI 只提供 DOCKER_HOST）时交给 Testcontainers 自己探测
    return undefined;
  }
}
