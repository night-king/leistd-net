# 同步模板

把项目同步到新的 `Leistd.Templates` 版本，或让没有来源标记的已有项目一次性接入。合并规则与退出码以 `scripts/sync-template.ps1` 的帮助（`Get-Help scripts/sync-template.ps1 -Full`）为准。

## 同步到新版本

1. 确认起点与目标。当前版本取 `.leistd/template.json` 的 `package.version`；目标是用户指定的版本，未指定时取最新的 `template-v*` Release。
2. 读取区间内每个版本的 Release 正文。区间是“当前版本之后、目标版本及之前”的全部 `template-v*`，不只读最后一版。模板仓库是模板包元数据里的源码仓库（nuget.org 上 `Leistd.Templates` 的 Source repository，`<所有者>/<仓库>` 形式）：

   ```bash
   gh release list --repo <模板仓库> --limit 100 --json tagName --jq '.[].tagName | select(startswith("template-v"))'
   gh release view "template-v<版本>" --repo <模板仓库> --json body,url
   ```

   记下变更说明与升级说明里需要项目配合的步骤。取不到 Release 正文时报告缺口，不凭记忆迁移。
3. 先提交或移走未提交的修改，再运行同步：

   ```bash
   pwsh scripts/sync-template.ps1 -To <版本>
   ```

   - 退出 0：全部自动合并，标记已更新，结果已暂存，转第 5 步。
   - 退出 1：读诊断。参数被删除或改名时，按诊断指向的 Release 迁移说明处理，不自行改写标记绕过。模板新增的路径上已有被忽略的本地文件时，先把它移走，合并后再按需并回。
   - 退出 2：有冲突，标记未更新，冲突是 git 的未合并路径（`git status`）。
4. 按语义解决冲突，结合 Release 正文判断模板改动的意图，同时保留项目自己的行为。每个路径解决后 `git add`：

   | `git status` | 现场 | 解决 |
   | --- | --- | --- |
   | `UU`、`AA` 文本 | diff3 冲突标记：`HEAD` 一侧是项目版本，`|||||||` 段是旧模板，另一侧是新模板 | 合并成同时满足两边意图的内容，删掉全部冲突标记 |
   | `UU` 二进制 | 工作区保留项目版本 | 二选一：`git checkout --ours -- <路径>`（项目）或 `--theirs`（新模板） |
   | `UD`、`DU` | 一方删除、另一方修改 | 保留就 `git add <路径>`，删除就 `git rm <路径>` |
   | `package.json` 冲突 | 锁文件没有重新生成 | 先解决 `package.json`，在其目录执行 `npm install --package-lock-only`，再 `git add` 锁文件。`-Complete` 不核对锁文件是否与清单一致，漏了重新生成时由 verify 的 `npm ci` 报出 |

   全部解决后运行：

   ```bash
   pwsh scripts/sync-template.ps1 -Complete
   ```

   还有未合并路径或冲突标记时会被拒绝，按输出逐项补齐，再次执行。想整体放弃这次同步时执行 `git reset --hard HEAD && git clean -fd`。
5. 执行 Release 正文里需要项目配合的步骤（如迁移、配置项），然后运行完整回归 `pwsh scripts/verify.ps1`；失败时按[审查与测试](quality.md)定位，属于合并结果的问题回到第 4 步的取舍。
6. 汇报：目标版本、未自动合并的各项及取舍理由、执行过的 Release 步骤、verify 结果与未验证项。用户要求提交时，把同步结果作为一次独立提交。

## 存量项目接入

适用于没有 `.leistd/template.json` 的已有项目。只做一次，之后按上一节同步。

1. 推断生成输入：
   - 项目名称，即生成时 `dotnet new -n` 的值，从解决方案与项目文件名得出；
   - 参数取值，从现有能力与目录判断（服务形态、前端、多租户等），未确定的向用户确认；
   - 生成所用的模板源码提交，从项目生成那次提交的时间与说明、用户提供的信息确定；
   - 项目里由模板生成的那次提交：通常是根提交；生成之前另有提交（如只含 README 的初始提交）或有多个根提交时，记下该提交，下一步用 `-BaselineCommit` 指定。
2. 准备基线源码与脚本，都放在项目之外的临时目录：
   - 取出该提交的模板源码目录，如 `git -C <模板仓库的克隆> archive <提交> template | tar -x -C <临时目录>`；
   - 从目标版本的模板包里取出包内 content/fullstack-app/scripts 下的 sync-template.ps1 与 template-marker.ps1。模板包可从 `https://api.nuget.org/v3-flatcontainer/leistd.templates/<版本>/leistd.templates.<版本>.nupkg` 下载，它是 zip 文件。
3. 在项目根目录运行：

   ```bash
   pwsh <临时目录>/sync-template.ps1 -Adopt -BaseSource <临时目录>/template -Name <名称> -Params <名称=取值,...> -To <版本> [-BaselineCommit <生成那次提交>]
   ```

   脚本先把重建的基线与生成那次提交比较。有差异时会列出并失败：成片的差异说明输入推断或提交有误，回到第 1 步；零星差异逐项核对，确认都是项目在该提交里自己的改动后，加 `-AcceptBaselineDiff` 重跑。之后的冲突处理与 `-Complete` 同上一节。
4. 项目改过的 `docs/standards/` 文件已迁到 docs/project 下的同名文件，其中还是整份内容。只保留项目自己的规则，删掉与模板基线重复的部分，并登记到 `docs/README.md`。
5. 运行 `pwsh scripts/verify.ps1`，删除临时目录，汇报内容同上一节。
