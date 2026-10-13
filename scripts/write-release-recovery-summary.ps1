#!/usr/bin/env pwsh

<#
.SYNOPSIS
    发布 job 失败时，按实际到达的阶段写出可执行的恢复指引（GitHub job summary）。

.DESCRIPTION
    release.yml 的最后一步在 failure() 时调用本脚本。阶段只由各步骤的 outcome 与
    push-release-ref.ps1 的 result 输出判定，不解析任何日志或展示文字：

      before-push  推包步骤未开始（skipped/空）：包源与远端都没有本版本，不给补 tag 指引
      packages     推包步骤失败或被取消：可能部分已发布，从原产物只补缺失包，再补 tag 及后续
      tag          包已全部推送，tag 未落地（含远端同名 tag 冲突）
      main         stable：tag 已落地，main 版本回写未完成
      release      stable/beta：tag（与回写）已完成，Release 或附件未完成；nightly 不创建 Release
      unknown      上述发布步骤都成功，失败来自其他步骤

    输出首行是机器可读标记 <!-- release-recovery-stage: <阶段> -->，供测试断言。
    规则见 docs/framework/versioning.md「部分发布恢复」。
#>
[CmdletBinding()]
param(
    # 判定通道之前就失败时为空，此时推包必然未开始
    [ValidateSet('stable', 'beta', 'nightly', '')][string]$Channel = '',
    [string]$Version = '',
    [string]$Tag = '',
    [string]$CandidateSha = '',
    # tag 解引用后的提交：beta/nightly 等于候选 SHA，stable 有回写时是 runner 本地创建的版本提交
    [string]$ReleaseSha = '',
    [string]$Repository = '',
    [string]$ServerUrl = 'https://github.com',
    [string]$RunId = '',
    [string]$ArtifactName = '',
    [string]$ArtifactOutcome = '',
    [string]$PushOutcome = '',
    [string]$TagOutcome = '',
    [string]$TagResult = '',
    [string]$BranchOutcome = '',
    [string]$BranchResult = '',
    [string]$ReleaseOutcome = '',
    [string]$SummaryPath = $env:GITHUB_STEP_SUMMARY
)

$ErrorActionPreference = 'Stop'

$pushStarted = $PushOutcome -in 'success', 'failure', 'cancelled'
$hasVersionCommit = $Channel -eq 'stable' -and $ReleaseSha -and $ReleaseSha -ne $CandidateSha
$stage = if (-not $pushStarted) { 'before-push' }
    elseif ($PushOutcome -ne 'success') { 'packages' }
    elseif ($TagOutcome -ne 'success') { 'tag' }
    elseif ($Channel -eq 'stable' -and $BranchOutcome -ne 'success') { 'main' }
    elseif ($Channel -ne 'nightly' -and $ReleaseOutcome -ne 'success') { 'release' }
    else { 'unknown' }

$order = @('packages', 'tag', 'main', 'release')
function Test-Pending([string]$Section) {
    $at = [array]::IndexOf($order, $stage)
    return $at -ge 0 -and [array]::IndexOf($order, $Section) -ge $at
}

function Show([string]$Value) { if ($Value) { $Value } else { '（未产生）' } }

$download = ".tmp/release-$Version"
$runUrl = if ($Repository -and $RunId) { "$ServerUrl/$Repository/actions/runs/$RunId" } else { '' }
$feed = if ($Channel -eq 'nightly') { "https://nuget.pkg.github.com/$($Repository.Split('/')[0])/index.json" } else { 'https://api.nuget.org/v3/index.json' }

$lines = [Collections.Generic.List[string]]::new()
$lines.Add("<!-- release-recovery-stage: $stage -->")
$lines.Add('## 发布未完成：恢复指引')
$lines.Add('')
$lines.Add('| 项 | 值 |')
$lines.Add('| --- | --- |')
$lines.Add("| 通道 | $Channel |")
$lines.Add("| 版本 | $(Show $Version) |")
$lines.Add("| tag | $(Show $Tag) |")
$lines.Add("| 发布 SHA（tag 指向） | $(Show $ReleaseSha) |")
$lines.Add("| 候选 SHA | $(Show $CandidateSha) |")
$lines.Add("| 原产物 | Actions artifact ``$(Show $ArtifactName)``（上传：$(Show $ArtifactOutcome)）$(if ($runUrl) { "，$runUrl" }) |")
$lines.Add("| 失败阶段 | ``$stage`` |")
$lines.Add('')
$lines.Add("步骤结果：推包=$(Show $PushOutcome)；tag=$(Show $TagOutcome)$(if ($TagResult) { "（$TagResult）" })" +
    $(if ($Channel -eq 'stable') { "；main 回写=$(Show $BranchOutcome)$(if ($BranchResult) { "（$BranchResult）" })" }) +
    $(if ($Channel -ne 'nightly') { "；Release=$(Show $ReleaseOutcome)" } else { '；nightly 不创建 Release' }))
$lines.Add('')
$lines.Add('不要整体重跑本 job：它会重新推算并重打同一版本，已上传的包会被拒绝。不自动 unlist；理由与完整规则见 `docs/framework/versioning.md`「部分发布恢复」。')
$lines.Add('')

if ($stage -eq 'before-push') {
    $lines.Add('### 推包未开始')
    $lines.Add('')
    $lines.Add('包源与远端都没有写入本版本（tag 只在 runner 本地创建过），不需要补包、tag 或 Release。修复失败原因后重新运行本 workflow 或等待下一次触发。')
}
elseif ($stage -eq 'unknown') {
    $lines.Add('### 发布步骤均已成功')
    $lines.Add('')
    $lines.Add('推包、tag、回写与 Release 步骤都成功，失败来自其他步骤；按步骤日志排查，不需要补发布元数据。')
}
else {
    $lines.Add('### 恢复步骤')
    $lines.Add('')
    $lines.Add('先取回本 run 推包前保存的原产物（不从当前分支重新打包）：')
    $lines.Add('')
    $lines.Add('```bash')
    $lines.Add("gh run download $RunId -R $Repository -n $ArtifactName -D $download")
    $lines.Add('```')
    $lines.Add('')
    if (Test-Pending 'packages') {
        $lines.Add("**包源**：推包中断，可能只有部分包已发布。按 ``$download/manifest.json`` 的包清单逐个核对 $feed 上的 ``$Version``，只推缺失的原 nupkg；不加 ``--skip-duplicate`` 批量重推，补齐后确认能还原全部精确版本。")
        $lines.Add('')
        $lines.Add('```bash')
        $lines.Add("dotnet nuget push $download/packages/<缺失的包>.$Version.nupkg --source $feed --api-key <有推送权限的 key>")
        $lines.Add('```')
        $lines.Add('')
    }
    if (Test-Pending 'tag') {
        if ($stage -eq 'tag' -and $TagResult -eq 'conflict') {
            $lines.Add("**tag**：远端已有 ``$Tag`` 但指向其他提交，本次没有覆盖。先核对远端 tag 指向、包源内容与本次产物，确定哪一份是已发布内容；不 force、不删除远端 tag。")
        }
        else {
            $lines.Add("**tag**：包已齐时按实际发布 SHA ``$ReleaseSha`` 补 tag。tag 对象取自原产物里的 bundle，不重新创建$(if ($hasVersionCommit) { '；stable 的版本提交只存在于这个 bundle 中' })：")
            $lines.Add('')
            $lines.Add('```bash')
            $lines.Add('git fetch origin')
            $lines.Add("git fetch $download/release.bundle refs/tags/${Tag}:refs/tags/$Tag")
            $lines.Add("test `"`$(git rev-parse '$Tag^{commit}')`" = $ReleaseSha")
            $lines.Add("pwsh scripts/push-release-ref.ps1 -Kind tag -Name $Tag -ExpectedSha $ReleaseSha")
            $lines.Add('```')
        }
        $lines.Add('')
    }
    if ($Channel -eq 'stable' -and (Test-Pending 'main') -and $hasVersionCommit) {
        # tag 阶段的命令已从 bundle 取回版本提交；只从 main 阶段开始时，版本提交不在任何远端分支上，
        # 普通 git fetch origin 取不到，要显式取回已推送的发布 tag 并核对它解引用到发布 SHA
        $fetchRelease = @(
            'git fetch origin',
            "git fetch origin refs/tags/${Tag}:refs/tags/$Tag   # 远端 tag 不可用时改从 $download/release.bundle 取",
            "test `"`$(git rev-parse '$Tag^{commit}')`" = $ReleaseSha"
        )
        if ($stage -eq 'main' -and $BranchResult -eq 'conflict') {
            $lines.Add("**main 回写**：main 已离开候选 SHA ``$CandidateSha``，没有快进。不 force；先取回版本提交，再经评审把它合入 main，保证 ``VERSION`` 与模板框架版本回写到位：")
            $lines.Add('')
            $lines.Add('```bash')
            $fetchRelease | ForEach-Object { $lines.Add($_) }
            $lines.Add("git merge --no-ff $Tag   # 在 main 的最新检出上合入，经评审后推送")
            $lines.Add('```')
        }
        else {
            $lines.Add("**main 回写**：tag 落地后把版本提交 ``$ReleaseSha`` 快进到 main（只允许从候选 SHA 前进）：")
            $lines.Add('')
            $lines.Add('```bash')
            if ($stage -eq 'main') { $fetchRelease | ForEach-Object { $lines.Add($_) } }
            $lines.Add("pwsh scripts/push-release-ref.ps1 -Kind branch -Name main -ExpectedSha $ReleaseSha -BaseSha $CandidateSha")
            $lines.Add('```')
            $lines.Add('脚本返回冲突（退出码 2）时 main 已前进：不 force，在 main 的最新检出上合入上面取回的版本提交并经评审推送。')
        }
        $lines.Add('')
    }
    if ($Channel -ne 'nightly' -and (Test-Pending 'release')) {
        $title = if ($Channel -eq 'stable') { "Release $Tag" } else { "Prerelease $Tag" }
        $flags = if ($Channel -eq 'stable') { '--latest' } else { '--prerelease --latest=false' }
        $lines.Add("**Release**：按 tag 查询实际状态，只补缺失项；说明与附件都取原产物，不覆盖已有附件：")
        $lines.Add('')
        $lines.Add('```bash')
        $lines.Add("gh release view $Tag -R $Repository --json isPrerelease,assets")
        $lines.Add("# 不存在时创建：")
        $lines.Add("gh release create $Tag -R $Repository --verify-tag --title '$title' --notes-file $download/release-notes.md $flags")
        $lines.Add("# 只上传缺失的附件（不加 --clobber）：")
        $lines.Add("gh release upload $Tag -R $Repository $download/packages/<缺失的包>.nupkg")
        $lines.Add('```')
        $lines.Add('')
    }
    if ($Channel -eq 'nightly') {
        $lines.Add('nightly 只发布 GitHub Packages 与 tag，不创建 Release。')
        $lines.Add('')
    }
}

$content = ($lines -join "`n") + "`n"
if ($SummaryPath) { $content | Out-File -LiteralPath $SummaryPath -Append -Encoding utf8 -NoNewline }
else { $content }
