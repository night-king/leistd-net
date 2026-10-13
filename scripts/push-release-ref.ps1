#!/usr/bin/env pwsh

<#
.SYNOPSIS
    把发布 tag 或 stable 版本回写提交推到远端：有限次短重试，每次推送前先查远端实际状态。

.DESCRIPTION
    release.yml 在包源写入后调用本脚本；部分发布恢复时，维护者在本地用同一脚本补推（见
    docs/framework/versioning.md「部分发布恢复」）。

    本脚本只推送，不创建也不改写 ref：tag 由 workflow 在打包前创建一次，这里重试的只有 push。
    每次推送前用 ls-remote 读取远端：
      - tag：同名 tag 已指向预期提交（按解引用后的提交比较）即视为完成；指向其他提交则停止。
      - branch：远端分支已等于预期提交即完成；等于 BaseSha 时快进推送；其他值说明分支已被别人推进，停止。
    推送一律不带 --force，也不删除远端 ref；服务端返回失败但实际已写入时，下一轮查询会认出并按完成处理。
    git 的退出码不区分权限错误与瞬时服务错误，因此对任何推送失败都只做有限次重试，不解析错误文字。

    退出码：0 完成；1 重试用尽或前置校验失败；2 远端已有冲突的 ref（不覆盖）。
    设置了 GITHUB_OUTPUT 时写出 result=pushed|already-present|conflict|failed，供后续步骤判定。

.PARAMETER Kind
    tag 或 branch。

.PARAMETER Name
    tag 名（如 v0.13.0-beta.499）或分支名（如 main）。

.PARAMETER ExpectedSha
    完整 40 位提交 SHA：tag 解引用后必须指向它；branch 推送后必须等于它。

.PARAMETER BaseSha
    仅 branch：允许快进的远端起点（stable 回写提交的父提交，即候选 SHA）。

.PARAMETER MaxAttempts
    推送尝试次数上限，默认 3。

.PARAMETER RetryDelaySeconds
    两次尝试之间的基础间隔（第 n 次失败后等待 n 倍），默认 10；测试传 0。
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)][ValidateSet('tag', 'branch')][string]$Kind,
    [Parameter(Mandatory)][string]$Name,
    [Parameter(Mandatory)][string]$ExpectedSha,
    [string]$BaseSha = '',
    [string]$Remote = 'origin',
    [ValidateRange(1, 10)][int]$MaxAttempts = 3,
    [ValidateRange(0, 120)][int]$RetryDelaySeconds = 10
)

$ErrorActionPreference = 'Stop'

function Write-Result([string]$Value) {
    if ($env:GITHUB_OUTPUT) { "result=$Value" | Out-File $env:GITHUB_OUTPUT -Append -Encoding utf8 }
}

function Stop-WithResult([string]$Value, [int]$Code, [string]$Message) {
    Write-Result $Value
    if ($Code -eq 0) { Write-Host $Message } else { Write-Host "::error::$Message" }
    exit $Code
}

$refName = if ($Kind -eq 'tag') { "refs/tags/$Name" } else { "refs/heads/$Name" }
if ($ExpectedSha -notmatch '^[0-9a-f]{40}$') { Stop-WithResult 'failed' 1 "ExpectedSha 必须是完整 40 位小写 SHA：$ExpectedSha" }

if ($Kind -eq 'tag') {
    # 推送的是本地已创建的那个 tag；它必须已指向预期提交，脚本不重建 tag
    $localSha = git rev-parse --verify --quiet "$refName^{commit}"
    if ($LASTEXITCODE -ne 0 -or $localSha -ne $ExpectedSha) {
        Stop-WithResult 'failed' 1 "本地 $refName 不存在或未指向 $ExpectedSha（实际：$localSha）；不推送"
    }
    $source = "${refName}:${refName}"
}
else {
    if ($BaseSha -notmatch '^[0-9a-f]{40}$') { Stop-WithResult 'failed' 1 "branch 推送需要完整 40 位 BaseSha：$BaseSha" }
    git cat-file -e "$ExpectedSha^{commit}" 2>$null
    if ($LASTEXITCODE -ne 0) { Stop-WithResult 'failed' 1 "本地没有提交 $ExpectedSha；不推送" }
    $source = "${ExpectedSha}:${refName}"
}

# 返回远端 ref 解引用后的提交；不存在返回 ''；查询本身失败返回 $null（状态未知）
function Get-RemoteSha {
    $lines = @(git ls-remote $Remote $refName "$refName^{}")
    if ($LASTEXITCODE -ne 0) { return $null }
    $direct = ''
    $peeled = ''
    foreach ($line in $lines) {
        $sha, $ref = "$line" -split "`t", 2
        if ($ref -ceq "$refName^{}") { $peeled = $sha }
        elseif ($ref -ceq $refName) { $direct = $sha }
    }
    if ($peeled) { return $peeled }
    return $direct
}

# 已完成返回 'already-present'；冲突返回 'conflict'；需要推送或状态未知返回 $null。
# 参数不声明 [string]：那会把"查询失败"的 $null 绑定成空串，与"ref 不存在"混为一谈
function Test-RemoteState($RemoteSha) {
    if ($null -eq $RemoteSha) { return $null }
    if ($RemoteSha -eq $ExpectedSha) { return 'already-present' }
    if ($Kind -eq 'tag') { return $(if ($RemoteSha) { 'conflict' } else { $null }) }
    if ($RemoteSha -eq $BaseSha) { return $null }
    return 'conflict'
}

for ($attempt = 1; $attempt -le $MaxAttempts; $attempt++) {
    $remoteSha = Get-RemoteSha
    $state = Test-RemoteState $remoteSha
    if ($state -eq 'already-present') {
        Stop-WithResult 'already-present' 0 "远端 $refName 已指向 $ExpectedSha，视为完成（第 $attempt 次查询）"
    }
    if ($state -eq 'conflict') {
        $expectation = if ($Kind -eq 'tag') { $ExpectedSha } else { "$ExpectedSha 或起点 $BaseSha" }
        Stop-WithResult 'conflict' 2 "远端 $refName 指向 $remoteSha，不是 $expectation；不覆盖、不删除，需人工核查"
    }
    # 状态未知不是冲突：照常推送（不带 --force，不会覆盖已有 ref），失败则进入下一轮重试
    if ($null -eq $remoteSha) { Write-Host "第 $attempt 次：查询远端 $refName 失败，仍尝试推送（推送不带 --force，不会覆盖已有 ref）" }

    git push $Remote $source
    if ($LASTEXITCODE -eq 0) {
        Stop-WithResult 'pushed' 0 "已推送 $refName -> $ExpectedSha（第 $attempt 次）"
    }
    Write-Host "第 $attempt/$MaxAttempts 次推送 $refName 失败（退出码 $LASTEXITCODE）"
    if ($attempt -lt $MaxAttempts -and $RetryDelaySeconds -gt 0) { Start-Sleep -Seconds ($RetryDelaySeconds * $attempt) }
}

# 最后一次推送可能已在服务端生效而本地收到失败：再查一次
$state = Test-RemoteState (Get-RemoteSha)
if ($state -eq 'already-present') {
    Stop-WithResult 'already-present' 0 "远端 $refName 已指向 $ExpectedSha，视为完成（推送报错但服务端已写入）"
}
if ($state -eq 'conflict') { Stop-WithResult 'conflict' 2 "远端 $refName 已被写成其他提交；不覆盖、不删除，需人工核查" }
Stop-WithResult 'failed' 1 "推送 $refName 在 $MaxAttempts 次尝试后仍失败（远端状态未能确认为完成或冲突）"
