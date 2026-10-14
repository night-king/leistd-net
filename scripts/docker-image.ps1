# 自启容器的测试脚本共用的镜像拉取：Docker Hub 偶发拒绝匿名拉取（unauthorized、限流或短暂故障），
# 先经有限次退避重试拉到本地，再照常 docker run；耗尽后带最后一次的退出码失败。
# Testcontainers 与 workflow 的 services 各有自己的重试，不经过这里。
function Invoke-DockerPullWithRetry {
    param(
        [Parameter(Mandatory)][string]$Image,
        # 执行一次拉取并返回退出码；默认用当前上下文的 docker CLI，需要指定上下文的调用方传自己的拉取
        [scriptblock]$Pull = { param($Name) & docker pull $Name | Out-Host; $LASTEXITCODE },
        [int]$DelaySeconds = 5
    )

    $attempts = 3
    for ($attempt = 1; ; $attempt++) {
        $exitCode = & $Pull $Image
        if ($exitCode -eq 0) { return }
        if ($attempt -ge $attempts) {
            throw "docker pull $Image failed after $attempts attempts (last exit code $exitCode)."
        }
        $delay = $DelaySeconds * $attempt
        Write-Host "docker pull $Image failed with exit code $exitCode; retrying in ${delay}s ($attempt/$attempts)." -ForegroundColor Yellow
        Start-Sleep -Seconds $delay
    }
}

# 与 docker run 默认的 --pull missing 一致：本地已有镜像就直接用，不访问镜像仓库；缺失时才有限重试拉取。
function Initialize-DockerImage {
    param(
        [Parameter(Mandatory)][string]$Image,
        [scriptblock]$Exists = { param($Name) & docker image inspect $Name *> $null; $LASTEXITCODE -eq 0 },
        [scriptblock]$Pull = { param($Name) & docker pull $Name | Out-Host; $LASTEXITCODE },
        [int]$DelaySeconds = 5
    )

    if (& $Exists $Image) { return }
    Invoke-DockerPullWithRetry $Image -Pull $Pull -DelaySeconds $DelaySeconds
}

# 自检：本地已有不拉取；首次成功只拉一次；失败后成功不报错；次数耗尽时失败并带出最后的退出码。由 check-all 调用。
function Test-DockerPullRetry {
    $failures = @()
    $cases = @(
        @{ Name = "本地已有"; Exists = $true; Codes = @(); Calls = 0; Throws = $false }
        @{ Name = "首次成功"; Exists = $false; Codes = @(0); Calls = 1; Throws = $false }
        @{ Name = "失败一次后成功"; Exists = $false; Codes = @(1, 0); Calls = 2; Throws = $false }
        @{ Name = "三次都失败"; Exists = $false; Codes = @(1, 1, 7); Calls = 3; Throws = $true }
    )
    foreach ($case in $cases) {
        $state = @{ Calls = 0; Codes = $case.Codes; Exists = $case.Exists }
        $exists = { param($Name) $state.Exists }.GetNewClosure()
        $pull = { param($Name) $state.Codes[$state.Calls++] }.GetNewClosure()
        $message = $null
        try { Initialize-DockerImage -Image "example:1" -Exists $exists -Pull $pull -DelaySeconds 0 } catch { $message = $_.Exception.Message }
        $thrown = $null -ne $message
        if ($state.Calls -ne $case.Calls -or $thrown -ne $case.Throws -or ($case.Throws -and -not $message.Contains("last exit code 7"))) {
            $failures += "$($case.Name)：拉取 $($state.Calls) 次、$(if ($thrown) { "失败（$message）" } else { '成功' })"
        }
    }
    if ($failures) { $failures | ForEach-Object { Write-Host "  ❌ $_" -ForegroundColor Red }; return $false }
    Write-Host "✅ 镜像拉取重试自检通过（$($cases.Count) 例）。" -ForegroundColor Green
    return $true
}
