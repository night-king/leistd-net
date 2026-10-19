import { randomBytes } from 'node:crypto';
import { resolve } from 'node:path';

import type { APIRequestContext } from '@playwright/test';

export interface Account {
  key: 'admin' | 'restricted';
  username: string;
  password: string;
}

/** 受限角色只持有的权限：能进角色列表，不能新建、编辑或删除。 */
export const RESTRICTED_PERMISSIONS = ['App.Roles'];

/** 口令每次运行随机生成：全局准备写入环境变量，测试进程从同一处读取。 */
function password(variable: string): string {
  process.env[variable] ??= `E2e!${randomBytes(12).toString('hex')}Aa1`;
  return process.env[variable];
}

export function admin(): Account {
  return { key: 'admin', username: 'admin', password: password('E2E_ADMIN_PASSWORD') };
}

export function restricted(): Account {
  return {
    key: 'restricted',
    username: 'e2e_viewer',
    password: password('E2E_RESTRICTED_PASSWORD'),
  };
}

/** 经接口登录后保存的会话；用例按 storageState 复用，不再走登录界面。 */
export function authStatePath(account: Account): string {
  return resolve(__dirname, `../../playwright/.auth/${account.key}.json`);
}

export async function signIn(context: APIRequestContext, account: Account): Promise<void> {
  const response = await context.post('/api/v1/auth/session-login', {
    data: { usernameOrEmail: account.username, password: account.password },
  });
  if (!response.ok()) {
    throw new Error(
      `Sign-in as ${account.username} failed: ${response.status()} ${await response.text()}`,
    );
  }
}
