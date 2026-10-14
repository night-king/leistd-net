#!/usr/bin/env pwsh
param(
    [ValidateSet("Debug", "Release")]
    [string]$Configuration = "Release",
    [switch]$SkipPack
)

$ErrorActionPreference = "Stop"
$repoRoot = (Resolve-Path (Join-Path $PSScriptRoot "..")).Path
. (Join-Path $PSScriptRoot "docker-image.ps1")
$runId = "{0}-{1}" -f $PID, (Get-Date -Format "yyyyMMddHHmmssfff")
$runRoot = Join-Path $repoRoot ".tmp/postgresql-e2e/$runId"
# 开发环境以外（迁移作业默认即是）Data Protection 要求显式的持久密钥位置；API 与各 DbMigrator 也必须共用
# 同一密钥环，否则一方加密的租户连接串另一方解不开。子进程继承这个变量；结束时在 finally 里恢复原值，
# 同一 PowerShell 进程内用 & 调用本脚本时不影响后续命令。
$previousDataProtectionKeysPath = [Environment]::GetEnvironmentVariable("DataProtection__KeysPath")
$env:DataProtection__KeysPath = Join-Path $runRoot "data-protection-keys"
$feedRoot = if ($SkipPack) { Join-Path $repoRoot ".tmp/local-feed" } else { Join-Path $runRoot "local-feed" }
$hiveRoot = Join-Path $runRoot "template-hive"
$generatedRoot = Join-Path $runRoot "generated"
$nugetConfigPath = Join-Path $runRoot "NuGet.Config"
$packagesRoot = Join-Path $runRoot "nuget-cache"
$containerName = "leistd-template-e2e-$PID"
$postgresPassword = "pg$([Guid]::NewGuid().ToString('N'))"
$identityRuntimePassword = "id$([Guid]::NewGuid().ToString('N'))"
$resourceRuntimePassword = "rs$([Guid]::NewGuid().ToString('N'))"
$apiProcess = $null
$apiStdoutTask = $null
$apiStderrTask = $null

function Invoke-External([string]$Command, [string[]]$Arguments, [string]$WorkingDirectory = $repoRoot) {
    Push-Location $WorkingDirectory
    try {
        & $Command @Arguments
        if ($LASTEXITCODE -ne 0) {
            throw "Command failed with exit code ${LASTEXITCODE}: $Command"
        }
    }
    finally {
        Pop-Location
    }
}

function Get-FreeTcpPort {
    $listener = [Net.Sockets.TcpListener]::new([Net.IPAddress]::Loopback, 0)
    $listener.Start()
    try { return ([Net.IPEndPoint]$listener.LocalEndpoint).Port }
    finally { $listener.Stop() }
}

function Invoke-WithEnvironment([hashtable]$Variables, [scriptblock]$Action) {
    $previous = @{}
    foreach ($name in $Variables.Keys) {
        $previous[$name] = [Environment]::GetEnvironmentVariable($name)
        [Environment]::SetEnvironmentVariable($name, [string]$Variables[$name])
    }

    try { & $Action }
    finally {
        foreach ($name in $Variables.Keys) {
            # A variable that was absent must be REMOVED, not set to "". PowerShell coerces the
            # $null out of a hashtable to an empty string when binding the string parameter, and
            # IConfiguration reports an empty environment variable as "" rather than null -- so a
            # leaked empty ConnectionStrings__* survives ?? fallbacks and reaches the next step as
            # a configured-but-blank connection.
            $value = $previous[$name]
            if ($null -eq $value) {
                [Environment]::SetEnvironmentVariable($name, [NullString]::Value)
            }
            else {
                [Environment]::SetEnvironmentVariable($name, [string]$value)
            }
        }
    }
}

function Invoke-Postgres(
    [string]$Database,
    [string]$Sql,
    [string]$Username = "postgres",
    [string]$Password = $postgresPassword,
    [switch]$ExpectFailure) {
    # 用容器自带的 psql，宿主机不必安装 PostgreSQL 客户端；经 TCP 连接，按指定账号走口令认证
    $output = & docker exec -e "PGPASSWORD=$Password" $containerName psql -h 127.0.0.1 -U $Username -d $Database `
        -v ON_ERROR_STOP=1 -At -c $Sql 2>&1
    $exitCode = $LASTEXITCODE
    if ($ExpectFailure) {
        if ($exitCode -eq 0) { throw "PostgreSQL command unexpectedly succeeded." }
        # 把错误文本还给调用方：只断言"失败了"会让打错的表名、写错的列名一样通过，
        # 调用方需要能核对失败原因正是它预期的那一个
        return ($output -join [Environment]::NewLine).Trim()
    }
    if ($exitCode -ne 0) {
        throw "PostgreSQL command failed: $($output -join [Environment]::NewLine)"
    }
    return ($output -join [Environment]::NewLine).Trim()
}

function Assert-Equal([string]$Expected, [string]$Actual, [string]$Message) {
    if ($Expected -ne $Actual) {
        throw "$Message Expected '$Expected', actual '$Actual'."
    }
}

function Invoke-Migrator([string]$Assembly, [hashtable]$Environment, [switch]$Apply) {
    $arguments = if ($Apply) { @($Assembly, "--apply") } else { @($Assembly) }
    Invoke-WithEnvironment $Environment {
        Invoke-External "dotnet" $arguments
    }
}

try {
    New-Item -ItemType Directory -Path $runRoot, $hiveRoot, $generatedRoot, $packagesRoot -Force | Out-Null
    if (-not $SkipPack) {
        New-Item -ItemType Directory -Path $feedRoot -Force | Out-Null
        Invoke-External "dotnet" @("pack", "framework/Leistd.Framework.slnx", "-c", $Configuration, "-o", $feedRoot)
    }
    elseif (-not (Test-Path -LiteralPath $feedRoot -PathType Container) -or
            -not (Get-ChildItem -LiteralPath $feedRoot -Filter "Leistd.*.nupkg" -File)) {
        throw "-SkipPack requires Leistd.*.nupkg packages in $feedRoot."
    }

    $escapedFeed = [Security.SecurityElement]::Escape($feedRoot)
    $escapedPackages = [Security.SecurityElement]::Escape($packagesRoot)
    $nugetConfig = @"
<?xml version="1.0" encoding="utf-8"?>
<configuration>
  <config><add key="globalPackagesFolder" value="$escapedPackages" /></config>
  <packageSources>
    <clear />
    <add key="local-feed" value="$escapedFeed" />
    <add key="nuget.org" value="https://api.nuget.org/v3/index.json" />
  </packageSources>
</configuration>
"@
    [IO.File]::WriteAllText($nugetConfigPath, $nugetConfig, [Text.UTF8Encoding]::new($false))
    Invoke-External "dotnet" @("new", "--debug:custom-hive", $hiveRoot, "install", (Join-Path $repoRoot "template"), "--force")
    $projects = @(
        # 参数以能力布尔表达：Identity 形态两者皆开，Resource 形态两者皆关。
        # Identity 侧把三个可选特性全开，经 DbMigrator 命令行把迁移应用到多库拆分的真实拓扑。
        # 曾漏过的实例：ExternalLoginConnections 表不在初始迁移里，开启外部登录的项目
        # 用自己的迁移建不出库（EF 报 PendingModelChangesWarning），而单场景编译一切正常。
        @{ Name = "E2E.Identity"
           Arguments = @("--include-notifications","--include-external-login","--include-localization")
           Root = Join-Path $generatedRoot "identity" },
        @{ Name = "E2E.Resource"
           Arguments = @("--service-role", "Resource")
           Root = Join-Path $generatedRoot "resource" }
    )
    foreach ($project in $projects) {
        # 先拼成一个变量再传：在参数位置直接写 @(...) + $x，PowerShell 会把 + 当成
        # 下一个位置参数的分隔，导致 Invoke-External 的 $Arguments/$WorkingDirectory 错位
        $newArguments = @(
            "new", "--debug:custom-hive", $hiveRoot, "fullstack-app",
            "-n", $project.Name, "-o", $project.Root, "--force") + $project.Arguments
        Invoke-External "dotnet" $newArguments
        $solution = Join-Path $project.Root "backend/$($project.Name).sln"
        Invoke-External "dotnet" @("restore", $solution, "--configfile", $nugetConfigPath, "--force")
        Invoke-External "dotnet" @("build", $solution, "-c", $Configuration, "--no-restore")
    }

    $script:postgresPort = Get-FreeTcpPort
    $postgresImage = "postgres:18-alpine"
    Initialize-DockerImage $postgresImage
    Invoke-External "docker" @(
        "run", "--detach", "--name", $containerName,
        "-e", "POSTGRES_PASSWORD=$postgresPassword",
        "-p", "${script:postgresPort}:5432", $postgresImage)

    $deadline = [DateTimeOffset]::UtcNow.AddSeconds(60)
    do {
        # The image briefly starts a socket-only bootstrap server before the final TCP server.
        & docker exec $containerName pg_isready -h 127.0.0.1 -U postgres *> $null
        if ($LASTEXITCODE -eq 0) { break }
        Start-Sleep -Milliseconds 500
    } while ([DateTimeOffset]::UtcNow -lt $deadline)
    if ($LASTEXITCODE -ne 0) { throw "PostgreSQL container did not become ready." }

    Invoke-External "docker" @("exec", $containerName, "createdb", "-U", "postgres", "leistd_shared")
    Invoke-External "docker" @("exec", $containerName, "createdb", "-U", "postgres", "leistd_dedicated")
    # Two more databases so that "control plane on its own database" can be asserted as a
    # separation, not just as a code path that ran.
    Invoke-External "docker" @("exec", $containerName, "createdb", "-U", "postgres", "leistd_control")
    Invoke-External "docker" @("exec", $containerName, "createdb", "-U", "postgres", "leistd_split_business")
    # 部分失败的迁移：排在后面的健康租户库在前一个库失败后照常迁移，作业以失败结束
    Invoke-External "docker" @("exec", $containerName, "createdb", "-U", "postgres", "leistd_late")

    $adminShared = "Host=127.0.0.1;Port=$script:postgresPort;Database=leistd_shared;Username=postgres;Password=$postgresPassword"
    $adminDedicated = "Host=127.0.0.1;Port=$script:postgresPort;Database=leistd_dedicated;Username=postgres;Password=$postgresPassword"
    $adminControl = "Host=127.0.0.1;Port=$script:postgresPort;Database=leistd_control;Username=postgres;Password=$postgresPassword"
    $adminSplitBusiness = "Host=127.0.0.1;Port=$script:postgresPort;Database=leistd_split_business;Username=postgres;Password=$postgresPassword"
    $adminLate = "Host=127.0.0.1;Port=$script:postgresPort;Database=leistd_late;Username=postgres;Password=$postgresPassword"
    $identityMigrator = Join-Path $generatedRoot "identity/backend/src/E2E.Identity.DbMigrator/bin/$Configuration/net10.0/E2E.Identity.DbMigrator.dll"
    $resourceMigrator = Join-Path $generatedRoot "resource/backend/src/E2E.Resource.DbMigrator/bin/$Configuration/net10.0/E2E.Resource.DbMigrator.dll"

    # A bare invocation is a dry run: it must report the pending migrations and change nothing.
    # This is the guarantee that matters most for a production migration job, so it is asserted
    # against a real database before anything is applied.
    Invoke-Migrator $identityMigrator @{ ConnectionStrings__Default = $adminShared }
    Assert-Equal "" (Invoke-Postgres "leistd_shared" 'SELECT to_regclass(''"e2e-identity"."__EFMigrationsHistory"'');') "The dry run created schema objects."

    # An unknown argument must fail loudly rather than degrade into a dry run: a typo such as
    # --aply would otherwise leave the operator believing the migration was applied.
    # Assert the exact exit code and message so that "it failed" cannot pass for the wrong reason.
    $typoOutput = Invoke-WithEnvironment @{ ConnectionStrings__Default = $adminShared } {
        & dotnet $identityMigrator "--aply" 2>&1
    }
    Assert-Equal "2" "$LASTEXITCODE" "An unknown DbMigrator argument did not exit with code 2."
    if (($typoOutput -join "`n") -notmatch "Unknown argument: --aply") {
        throw "DbMigrator did not name the unknown argument it rejected."
    }

    Invoke-Migrator $identityMigrator @{ ConnectionStrings__Default = $adminShared } -Apply
    # 资源服务的部署配置里总有控制面地址（远端租户连接存储启动时校验它）；显式目标模式不回源，
    # 这里给一个不会被访问的地址，与真实部署的配置形态一致
    $resourceControlPlane = "http://identity.invalid"
    Invoke-Migrator $resourceMigrator @{
        ConnectionStrings__MigrationTarget = $adminShared
        Leistd__ServiceClients__Identity__BaseAddress = $resourceControlPlane
    } -Apply
    Invoke-Migrator $identityMigrator @{ ConnectionStrings__MigrationTarget = $adminDedicated } -Apply
    Invoke-Migrator $resourceMigrator @{
        ConnectionStrings__MigrationTarget = $adminDedicated
        Leistd__ServiceClients__Identity__BaseAddress = $resourceControlPlane
    } -Apply
    # A second pass proves that both initial and explicit-target modes are idempotent.
    Invoke-Migrator $identityMigrator @{ ConnectionStrings__Default = $adminShared } -Apply
    Invoke-Migrator $identityMigrator @{ ConnectionStrings__MigrationTarget = $adminDedicated } -Apply

    # A dry run against an already-migrated target reports "up to date" and still changes nothing.
    Invoke-Migrator $identityMigrator @{ ConnectionStrings__Default = $adminShared }

    Assert-Equal "1" (Invoke-Postgres "leistd_shared" 'SELECT count(*) FROM "e2e-identity"."__EFMigrationsHistory_Control";') "Identity Control migration is missing."
    Assert-Equal "1" (Invoke-Postgres "leistd_shared" 'SELECT count(*) FROM "e2e-identity"."__EFMigrationsHistory";') "Identity business migration is missing."
    Assert-Equal "1" (Invoke-Postgres "leistd_shared" 'SELECT count(*) FROM "e2e-resource"."__EFMigrationsHistory";') "Resource migration is missing."
    Assert-Equal "1" (Invoke-Postgres "leistd_dedicated" 'SELECT count(*) FROM "e2e-identity"."__EFMigrationsHistory";') "Dedicated Identity migration is missing."
    Assert-Equal "1" (Invoke-Postgres "leistd_dedicated" 'SELECT count(*) FROM "e2e-resource"."__EFMigrationsHistory";') "Dedicated Resource migration is missing."
    Assert-Equal "" (Invoke-Postgres "leistd_dedicated" 'SELECT to_regclass(''"e2e-identity"."TenantRecord"'');') "Identity Control tables leaked into the dedicated business target."

    # Control plane pinned to its own database. DbMigrator must follow the SAME fallback chain as
    # the runtime (IdentityControl ?? Default). Reading only Default would migrate the control
    # schema into the business database while the API reads IdentityControl -- the failure then
    # surfaces as "relation does not exist" at startup, after deployment. The assertions below are
    # a separation in both directions, because "the control tables exist somewhere" would pass
    # even with the bug.
    # The dry run report is the human review gate before --apply, so it must name a DISTINCT
    # physical target for the control plane. Printing "default" for both would let an operator
    # approve a plan while believing the control schema lands in the business database.
    $splitReport = Invoke-WithEnvironment @{
        ConnectionStrings__Default = $adminSplitBusiness
        ConnectionStrings__IdentityControl = $adminControl
    } {
        & dotnet $identityMigrator 2>&1
    }
    $splitReportText = $splitReport -join "`n"
    if ($splitReportText -notmatch '\[control\] target [0-9A-F]{64}') {
        throw "The dry run did not report a distinct physical target for the control plane when IdentityControl was configured separately. Report was:`n$splitReportText"
    }
    if ($splitReportText -notmatch '\[business\] target default') {
        throw "The dry run did not report the business target as 'default'. Report was:`n$splitReportText"
    }

    Invoke-Migrator $identityMigrator @{
        ConnectionStrings__Default = $adminSplitBusiness
        ConnectionStrings__IdentityControl = $adminControl
    } -Apply

    # Existence is checked through pg_tables rather than a count against the table itself: a
    # missing table makes psql fail, and the raw "relation does not exist" then replaces the
    # assertion message that would have explained WHICH separation broke.
    Assert-Equal "1" (Invoke-Postgres "leistd_control" 'SELECT count(*) FROM pg_tables WHERE schemaname = ''e2e-identity'' AND tablename = ''__EFMigrationsHistory_Control'';') "Control migration did not go to the IdentityControl database."
    Assert-Equal "1" (Invoke-Postgres "leistd_control" 'SELECT count(*) FROM pg_tables WHERE schemaname = ''e2e-identity'' AND tablename = ''__EFMigrationsHistory_OpenIddict'';') "OpenIddict migration did not go to the IdentityControl database."
    Assert-Equal "1" (Invoke-Postgres "leistd_split_business" 'SELECT count(*) FROM pg_tables WHERE schemaname = ''e2e-identity'' AND tablename = ''__EFMigrationsHistory'';') "Business migration did not go to the Default database."
    Assert-Equal "1" (Invoke-Postgres "leistd_control" 'SELECT count(*) FROM "e2e-identity"."__EFMigrationsHistory_Control";') "The control migration history in the IdentityControl database is empty."
    Assert-Equal "" (Invoke-Postgres "leistd_split_business" 'SELECT to_regclass(''"e2e-identity"."TenantRecord"'');') "Control tables leaked into the Default database when IdentityControl was configured separately."

    # A migrated control database whose tenant registry is missing is CORRUPTION, not a first
    # install. Treating 42P01 as "first install, no dedicated targets" and exiting 0 would let a
    # deployment believe every target was migrated -- the table could have been dropped, renamed,
    # or created under the wrong schema. The first-install exemption therefore also requires that
    # this is a dry run AND that the control database still had pending migrations.
    Invoke-Postgres "leistd_control" 'DROP TABLE "e2e-identity"."TenantConnectionRecord";' | Out-Null

    $corruptOutput = Invoke-WithEnvironment @{
        ConnectionStrings__Default = $adminSplitBusiness
        ConnectionStrings__IdentityControl = $adminControl
    } {
        & dotnet $identityMigrator "--apply" 2>&1
    }
    $corruptExit = $LASTEXITCODE
    if ($corruptExit -eq 0) {
        throw "DbMigrator exited 0 after --apply although the tenant registry was missing from an already-migrated control database. Output was:`n$($corruptOutput -join "`n")"
    }

    # A dry run against the same corrupt state must also fail: the control database has no pending
    # migrations, so "the table does not exist yet" cannot be true.
    Invoke-WithEnvironment @{
        ConnectionStrings__Default = $adminSplitBusiness
        ConnectionStrings__IdentityControl = $adminControl
    } {
        & dotnet $identityMigrator 2>&1 | Out-Null
    }
    if ($LASTEXITCODE -eq 0) {
        throw "A dry run exited 0 although the tenant registry was missing from an already-migrated control database."
    }

    # No restore: re-running --apply cannot recreate the table, because the migration history still
    # records that migration as applied. It is not needed either -- leistd_control is only used by
    # the assertions above plus the business-history check below, and neither depends on the
    # registry table existing.
    Assert-Equal "" (Invoke-Postgres "leistd_control" 'SELECT to_regclass(''"e2e-identity"."__EFMigrationsHistory"'');') "Business migrations leaked into the IdentityControl database."

    Invoke-Postgres "postgres" "CREATE ROLE e2e_identity_runtime LOGIN PASSWORD '$identityRuntimePassword'; CREATE ROLE e2e_resource_runtime LOGIN PASSWORD '$resourceRuntimePassword';" | Out-Null
    foreach ($database in @("leistd_shared", "leistd_dedicated")) {
        Invoke-Postgres $database @"
GRANT CONNECT ON DATABASE $database TO e2e_identity_runtime, e2e_resource_runtime;
GRANT USAGE ON SCHEMA "e2e-identity" TO e2e_identity_runtime;
GRANT SELECT, INSERT, UPDATE, DELETE ON ALL TABLES IN SCHEMA "e2e-identity" TO e2e_identity_runtime;
GRANT USAGE, SELECT, UPDATE ON ALL SEQUENCES IN SCHEMA "e2e-identity" TO e2e_identity_runtime;
GRANT USAGE ON SCHEMA "e2e-resource" TO e2e_resource_runtime;
GRANT SELECT, INSERT, UPDATE, DELETE ON ALL TABLES IN SCHEMA "e2e-resource" TO e2e_resource_runtime;
GRANT USAGE, SELECT, UPDATE ON ALL SEQUENCES IN SCHEMA "e2e-resource" TO e2e_resource_runtime;
"@ | Out-Null
    }

    Invoke-Postgres "leistd_shared" 'CREATE TABLE "e2e-identity"."RuntimeMustNotCreateTables"("Id" integer);' `
        -Username "e2e_identity_runtime" -Password $identityRuntimePassword -ExpectFailure

    $identityRuntimeShared = "Host=127.0.0.1;Port=$script:postgresPort;Database=leistd_shared;Username=e2e_identity_runtime;Password=$identityRuntimePassword"
    $apiPort = Get-FreeTcpPort
    $baseUrl = "http://127.0.0.1:$apiPort"
    $apiAssembly = Join-Path $generatedRoot "identity/backend/src/E2E.Identity.Api/bin/$Configuration/net10.0/E2E.Identity.Api.dll"
    $apiDirectory = Split-Path $apiAssembly
    $startInfo = [Diagnostics.ProcessStartInfo]::new()
    $startInfo.FileName = (Get-Command dotnet -ErrorAction Stop).Source
    $startInfo.WorkingDirectory = $apiDirectory
    $startInfo.UseShellExecute = $false
    $startInfo.RedirectStandardOutput = $true
    $startInfo.RedirectStandardError = $true
    $startInfo.ArgumentList.Add($apiAssembly)
    $startInfo.Environment["ASPNETCORE_ENVIRONMENT"] = "Development"
    $startInfo.Environment["ASPNETCORE_URLS"] = $baseUrl
    $startInfo.Environment["ConnectionStrings__Default"] = $identityRuntimeShared
    $startInfo.Environment["ConnectionStrings__Redis"] = ""
    $startInfo.Environment["OAuth__Issuer"] = "$baseUrl/"
    $startInfo.Environment["OAuth__DisableHttpsRequirement"] = "true"
    $startInfo.Environment["DefaultAdmin__Username"] = "admin"
    # 不用模板曾发布过的示例密码：生产校验会拒绝它们，沿用就等于测不到那条校验
    $startInfo.Environment["DefaultAdmin__Password"] = "E2ETests!Adm1n"
    $startInfo.Environment["Leistd__Security__VerificationCodes__Key"] = "AAECAwQFBgcICQoLDA0ODxAREhMUFRYXGBkaGxwdHh8="

    $apiProcess = [Diagnostics.Process]::new()
    $apiProcess.StartInfo = $startInfo
    if (-not $apiProcess.Start()) { throw "Identity API failed to start." }
    $apiStdoutTask = $apiProcess.StandardOutput.ReadToEndAsync()
    $apiStderrTask = $apiProcess.StandardError.ReadToEndAsync()

    $deadline = [DateTimeOffset]::UtcNow.AddSeconds(90)
    do {
        if ($apiProcess.HasExited) { throw "Identity API exited with code $($apiProcess.ExitCode)." }
        try {
            $health = Invoke-WebRequest -Uri "$baseUrl/api/health/live" -TimeoutSec 2
            if ($health.StatusCode -eq 200) { break }
        }
        catch { Start-Sleep -Milliseconds 500 }
    } while ([DateTimeOffset]::UtcNow -lt $deadline)
    if (-not $health -or $health.StatusCode -ne 200) { throw "Identity API did not become healthy." }

    $loginBody = @{ usernameOrEmail = "admin"; password = "E2ETests!Adm1n" } | ConvertTo-Json
    $login = Invoke-WebRequest -Uri "$baseUrl/api/v1/auth/session-login" -Method Post `
        -ContentType "application/json" -Body $loginBody -SessionVariable apiSession
    Assert-Equal "200" ([string]$login.StatusCode) "Host administrator login failed."

    $sharedBody = @{
        name = "shared-e2e"; displayName = "Shared E2E"; adminEmail = "shared@example.test"
        adminPassword = "E2ETenant!Adm1n"
    } | ConvertTo-Json
    $sharedResponse = Invoke-WebRequest -Uri "$baseUrl/api/v1/tenants" -Method Post `
        -ContentType "application/json" -Body $sharedBody -WebSession $apiSession
    $sharedTenant = $sharedResponse.Content | ConvertFrom-Json

    # 分库在建租户时定案：连接与租户在同一个事务里登记，登记先于播种，种子因此直接落进专属库。
    # 这里只给默认名一条，所有服务都回落到它——一租户一库、各服务不同 schema；
    # 契约本身收的是命名连接数组，多服务部署可以一次给多条。
    # 运行与迁移共用这一条，因此要 DDL 权限：用 admin 连接。它只写不读，由 API 加密后落库。
    $dedicatedBody = @{
        name = "dedicated-e2e"; displayName = "Dedicated E2E"; adminEmail = "dedicated@example.test"
        adminPassword = "E2ETenant!Adm1n"
        connections = @(@{ name = "default"; connectionString = $adminDedicated })
    } | ConvertTo-Json -Depth 5
    $dedicatedResponse = Invoke-WebRequest -Uri "$baseUrl/api/v1/tenants" -Method Post `
        -ContentType "application/json" -Body $dedicatedBody -WebSession $apiSession
    $dedicatedTenant = $dedicatedResponse.Content | ConvertFrom-Json

    # 建租户那一步已经把连接登记好了，这里只核对结果
    $dedicatedConnections = (Invoke-WebRequest `
        -Uri "$baseUrl/api/v1/tenant-connections/$($dedicatedTenant.id)" -WebSession $apiSession).Content | ConvertFrom-Json
    Assert-Equal "1" ([string]$dedicatedConnections.Count) "The connection supplied at creation was not registered."
    Assert-Equal "default" ([string]$dedicatedConnections[0].name) "The connection was not registered under the default name."

    # 事后把一个在用租户改成分库必须被拒（409）：它已有的数据连同租户管理员都在回落库里，
    # 登记一落解析就改指空库，租户当场登不上。这是本流程唯一能分库的时点之外的那条路。
    $connectionBody = @{ expectedVersion = $null; connectionString = $adminDedicated } | ConvertTo-Json
    $lateShardStatus = $null
    try {
        Invoke-WebRequest -Uri "$baseUrl/api/v1/tenant-connections/$($sharedTenant.id)/default" -Method Put `
            -ContentType "application/json" -Body $connectionBody -WebSession $apiSession | Out-Null
        $lateShardStatus = 200
    }
    catch {
        if ($_.Exception.Response) { $lateShardStatus = [int]$_.Exception.Response.StatusCode }
    }
    Assert-Equal "409" "$lateShardStatus" "Sharding an already-serving tenant after creation was not rejected."

    # 共享租户一条都不登记：列表必须是空的——既是"不分库"在接口上的唯一表征，
    # 也证明上面那次被拒的登记没有留下半条记录
    $sharedConnections = (Invoke-WebRequest `
        -Uri "$baseUrl/api/v1/tenant-connections/$($sharedTenant.id)" -WebSession $apiSession).Content | ConvertFrom-Json
    if ($sharedConnections.Count -ne 0) {
        throw "A tenant without registrations must report an empty connection list."
    }

    $sharedTenantUserSql = 'SELECT count(*) FROM "e2e-identity"."Users" WHERE "TenantId" = ''{0}'' AND NOT "IsDeleted";' -f $sharedTenant.id
    $dedicatedTenantUserSql = 'SELECT count(*) FROM "e2e-identity"."Users" WHERE "TenantId" = ''{0}'' AND NOT "IsDeleted";' -f $dedicatedTenant.id
    $protectedConnectionSql = 'SELECT "ProtectedConnectionString" FROM "e2e-identity"."TenantConnectionRecord" WHERE "TenantId" = ''{0}'' AND "Name" = ''default'';' -f $dedicatedTenant.id
    Assert-Equal "1" (Invoke-Postgres "leistd_shared" $sharedTenantUserSql) "Shared tenant data was not stored in the default target."
    Assert-Equal "0" (Invoke-Postgres "leistd_dedicated" $sharedTenantUserSql) "Shared tenant data leaked into the dedicated target."
    Assert-Equal "0" (Invoke-Postgres "leistd_shared" $dedicatedTenantUserSql) "Dedicated tenant data leaked into the default target."
    Assert-Equal "1" (Invoke-Postgres "leistd_dedicated" $dedicatedTenantUserSql) "Dedicated tenant data was not stored in its target."
    # Encrypted at rest: the column must hold a value, and that value must not be the plaintext
    # connection string (which would put the database password in every backup and read replica).
    $protectedConnection = [string](Invoke-Postgres "leistd_shared" $protectedConnectionSql)
    if ([string]::IsNullOrWhiteSpace($protectedConnection)) {
        throw "The dedicated tenant's connection string was not stored."
    }
    if ($protectedConnection -match 'Host=|Password=') {
        throw "The dedicated tenant's connection string was stored in plaintext."
    }

    # 超管只属于宿主：CK_User_SuperAdminIsHostOnly。领域服务那道关由集成测试覆盖；
    # 这里用直接 SQL 证明数据库约束本身拦得住数据修复脚本与批量导入。用刚种出来的真实数据断言，不另造行：
    #   1. 宿主种子确实产出了一个超管（否则下面的 UPDATE 命中 0 行，会假装"约束生效"）；
    #   2. 租户种子产出的管理员没有被标成超管；
    #   3. 把那个超管挪进租户必须被数据库拒绝。
    Assert-Equal "1" (Invoke-Postgres "leistd_shared" 'SELECT count(*) FROM "e2e-identity"."Users" WHERE "IsSuperAdmin" AND "TenantId" IS NULL;') "Host seeding did not produce exactly one super admin."
    Assert-Equal "0" (Invoke-Postgres "leistd_shared" 'SELECT count(*) FROM "e2e-identity"."Users" WHERE "IsSuperAdmin" AND "TenantId" IS NOT NULL;') "Tenant seeding produced a tenant-scoped super admin."
    $constraintViolation = Invoke-Postgres "leistd_shared" ('UPDATE "e2e-identity"."Users" SET "TenantId" = ''{0}'' WHERE "IsSuperAdmin";' -f $sharedTenant.id) -ExpectFailure
    if ($constraintViolation -notmatch "CK_User_SuperAdminIsHostOnly") {
        throw "The super-admin UPDATE failed for a reason other than the check constraint: $constraintViolation"
    }

    # 连接名不合法必须在入口就被拒（400），不能落成一行谁也解析不出来的登记。
    # 名字归一化为小写后须匹配 ^[a-z0-9-]{1,64}$，下划线不在其中。
    $badNameStatus = $null
    try {
        Invoke-WebRequest -Uri "$baseUrl/api/v1/tenant-connections/$($sharedTenant.id)/crm_db" -Method Put `
            -ContentType "application/json" -Body $connectionBody -WebSession $apiSession | Out-Null
        $badNameStatus = 200
    }
    catch {
        if ($_.Exception.Response) { $badNameStatus = [int]$_.Exception.Response.StatusCode }
    }
    Assert-Equal "400" "$badNameStatus" "An invalid connection name was not rejected with 400."
    Assert-Equal "0" (Invoke-Postgres "leistd_shared" 'SELECT count(*) FROM "e2e-identity"."TenantConnectionRecord" WHERE "Name" NOT IN (''default'');') "An invalid connection name was persisted."

    # 迁移目标里一个租户出问题，不能挡住其他租户的库，也不能被静默略过：
    # 其余库照常预演或迁移，问题逐个报出，作业以非零退出；修好之后重跑即成功。
    # 三类目标同时在场（租户标识按时间有序，目标按它排序，专属库排在 late 之前）——
    #   解析不出：共享租户停用后只登记了别的服务（billing）的连接，本服务的名字解析不出；
    #   迁移出错：专属库的迁移历史少了一行，施加时重复建表失败；
    #   健康待迁：late 租户的库排在失败库之后，业务 schema 被删掉、有待施加的迁移。
    Invoke-Migrator $identityMigrator @{ ConnectionStrings__MigrationTarget = $adminLate } -Apply
    $lateBody = @{
        name = "late-e2e"; displayName = "Late E2E"; adminEmail = "late@example.test"
        adminPassword = "E2ETenant!Adm1n"
        connections = @(@{ name = "default"; connectionString = $adminLate })
    } | ConvertTo-Json -Depth 5
    Invoke-WebRequest -Uri "$baseUrl/api/v1/tenants" -Method Post `
        -ContentType "application/json" -Body $lateBody -WebSession $apiSession | Out-Null
    Invoke-Postgres "leistd_late" 'DROP SCHEMA "e2e-identity" CASCADE;' | Out-Null
    $lateHistorySql = 'SELECT count(*) FROM pg_tables WHERE schemaname = ''e2e-identity'' AND tablename = ''__EFMigrationsHistory'';'

    $brokenHistory = [string](Invoke-Postgres "leistd_dedicated" 'SELECT "MigrationId" || ''|'' || "ProductVersion" FROM "e2e-identity"."__EFMigrationsHistory" ORDER BY "MigrationId" LIMIT 1;')
    $brokenMigrationId, $brokenProductVersion = $brokenHistory.Split('|')
    Invoke-Postgres "leistd_dedicated" ('DELETE FROM "e2e-identity"."__EFMigrationsHistory" WHERE "MigrationId" = ''{0}'';' -f $brokenMigrationId) | Out-Null

    Invoke-WebRequest -Method Put -WebSession $apiSession `
        -Uri "$baseUrl/api/v1/tenants/$($sharedTenant.id)/activation" `
        -ContentType "application/json" -Body (@{ isActive = $false } | ConvertTo-Json) | Out-Null
    Invoke-WebRequest -Uri "$baseUrl/api/v1/tenant-connections/$($sharedTenant.id)/billing" -Method Put `
        -ContentType "application/json" -Body $connectionBody -WebSession $apiSession | Out-Null

    $partialDryRun = Invoke-WithEnvironment @{ ConnectionStrings__Default = $adminShared } {
        & dotnet $identityMigrator 2>&1
    }
    $partialDryRunExit = $LASTEXITCODE
    $partialDryRunText = $partialDryRun -join "`n"
    Set-Content -LiteralPath (Join-Path $runRoot "migrator-partial-dry-run.txt") -Value $partialDryRunText
    Assert-Equal "1" "$partialDryRunExit" "A dry run with an unresolvable tenant did not exit with code 1. Output was:`n$partialDryRunText"
    if ($partialDryRunText -notmatch [regex]::Escape([string]$sharedTenant.id)) {
        throw "The dry run did not name the tenant whose connection cannot be resolved. Output was:`n$partialDryRunText"
    }
    Assert-Equal "0" (Invoke-Postgres "leistd_late" $lateHistorySql) "The dry run changed a healthy target."

    $partialApply = Invoke-WithEnvironment @{ ConnectionStrings__Default = $adminShared } {
        & dotnet $identityMigrator "--apply" 2>&1
    }
    $partialApplyExit = $LASTEXITCODE
    $partialApplyText = $partialApply -join "`n"
    Set-Content -LiteralPath (Join-Path $runRoot "migrator-partial-apply.txt") -Value $partialApplyText
    Assert-Equal "1" "$partialApplyExit" "Applying with an unresolvable tenant and a failing target did not exit with code 1. Output was:`n$partialApplyText"
    if ($partialApplyText -notmatch [regex]::Escape([string]$sharedTenant.id)) {
        throw "Apply did not name the tenant whose connection cannot be resolved. Output was:`n$partialApplyText"
    }
    if ($partialApplyText -notmatch [regex]::Escape([string]$dedicatedTenant.id)) {
        throw "Apply did not name the tenant whose database failed to migrate. Output was:`n$partialApplyText"
    }
    if ($partialApplyText -match [regex]::Escape($postgresPassword)) {
        throw "The migrator output exposed a connection string."
    }
    # 排在失败库之后的健康库照样迁完。先核对前提——失败库确实先执行——否则"健康库迁好了"
    # 也可能出自"先迁健康库、遇到失败就停"的实现，这条断言就证明不了"失败之后继续"
    $dedicatedFingerprint = [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData([Text.Encoding]::UTF8.GetBytes($adminDedicated)))
    $lateFingerprint = [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData([Text.Encoding]::UTF8.GetBytes($adminLate)))
    $failedAt = $partialApplyText.IndexOf("Migrating tenant database $dedicatedFingerprint", [StringComparison]::Ordinal)
    $lateAppliedAt = $partialApplyText.IndexOf("to target $lateFingerprint", [StringComparison]::Ordinal)
    if ($failedAt -lt 0 -or $lateAppliedAt -lt 0 -or $failedAt -gt $lateAppliedAt) {
        throw "The failing target was not migrated before the healthy one (failure at $failedAt, healthy at $lateAppliedAt). Output was:`n$partialApplyText"
    }
    Assert-Equal "1" (Invoke-Postgres "leistd_late" $lateHistorySql) "A healthy target after a failing one was not migrated."

    # 修好两处后重跑：撤掉那条别的服务的登记（租户已停用，允许改动），补回缺的迁移历史
    $sharedVersion = ((Invoke-WebRequest -Uri "$baseUrl/api/v1/tenant-connections/$($sharedTenant.id)" `
        -WebSession $apiSession).Content | ConvertFrom-Json)[0].version
    Invoke-WebRequest -Method Delete -WebSession $apiSession `
        -Uri "$baseUrl/api/v1/tenant-connections/$($sharedTenant.id)/billing?expectedVersion=$sharedVersion" | Out-Null
    Invoke-Postgres "leistd_dedicated" ('INSERT INTO "e2e-identity"."__EFMigrationsHistory" ("MigrationId", "ProductVersion") VALUES (''{0}'', ''{1}'');' -f $brokenMigrationId, $brokenProductVersion) | Out-Null
    Invoke-Migrator $identityMigrator @{ ConnectionStrings__Default = $adminShared } -Apply

    # 删除等同于把路由改回共享库，而该租户已有的数据连同它的管理员都在专属库里——
    # 放行就是让那些数据搁浅。所以与"事后把在用租户改成分库"同一条判据：租户还在服务时必须被拒。
    # 先把这条契约本身钉住，再验证停用之后确实删得掉；只做后者的话，这条保护改没了也不会有人发现。
    $version = ((Invoke-WebRequest -Uri "$baseUrl/api/v1/tenant-connections/$($dedicatedTenant.id)" `
        -WebSession $apiSession).Content | ConvertFrom-Json)[0].version
    $activeRemoveStatus = $null
    try {
        Invoke-WebRequest -Method Delete -WebSession $apiSession `
            -Uri "$baseUrl/api/v1/tenant-connections/$($dedicatedTenant.id)/default?expectedVersion=$version" | Out-Null
        $activeRemoveStatus = 200
    }
    catch {
        if ($_.Exception.Response) { $activeRemoveStatus = [int]$_.Exception.Response.StatusCode }
    }
    Assert-Equal "409" "$activeRemoveStatus" "Removing a connection while the tenant is still serving was not rejected."

    # 停用之后才允许删：删掉登记就回到"不分库"，该名字之后回落到服务自己的配置。
    # 被拒的那次不写入、也不推进租户版本（IsActive 检查在版本自增之前），因此沿用同一个 $version。
    Invoke-WebRequest -Method Put -WebSession $apiSession `
        -Uri "$baseUrl/api/v1/tenants/$($dedicatedTenant.id)/activation" `
        -ContentType "application/json" -Body (@{ isActive = $false } | ConvertTo-Json) | Out-Null
    Invoke-WebRequest -Method Delete -WebSession $apiSession `
        -Uri "$baseUrl/api/v1/tenant-connections/$($dedicatedTenant.id)/default?expectedVersion=$version" | Out-Null
    Assert-Equal "0" (Invoke-Postgres "leistd_shared" ('SELECT count(*) FROM "e2e-identity"."TenantConnectionRecord" WHERE "TenantId" = ''{0}'';' -f $dedicatedTenant.id)) "Removing the registration left the row behind."

    Write-Host "PostgreSQL template E2E passed: package -> generate -> migrate -> API -> shared/dedicated isolation." -ForegroundColor Green
    Write-Host "Artifacts: $runRoot"
}
catch {
    if ($apiProcess -and -not $apiProcess.HasExited) {
        $apiProcess.Kill($true)
        $apiProcess.WaitForExit()
    }
    if ($apiStdoutTask -and $apiStderrTask) {
        $logs = (($apiStdoutTask.GetAwaiter().GetResult()) + [Environment]::NewLine + ($apiStderrTask.GetAwaiter().GetResult())).Trim()
        if ($logs.Length -gt 6000) { $logs = $logs.Substring($logs.Length - 6000) }
        if ($logs) { Write-Error "Identity API log:`n$logs" -ErrorAction Continue }
    }
    throw
}
finally {
    if ($apiProcess) {
        if (-not $apiProcess.HasExited) { $apiProcess.Kill($true) }
        $apiProcess.WaitForExit()
        $apiProcess.Dispose()
    }
    & docker rm --force --volumes $containerName *> $null
    [Environment]::SetEnvironmentVariable("DataProtection__KeysPath", $previousDataProtectionKeysPath)
}
