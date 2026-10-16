#!/usr/bin/env pwsh

<#
.SYNOPSIS
    从当前模板生成四个服务，以真实 PostgreSQL 和 OpenIddict 验证跨进程认证。
.DESCRIPTION
    运行 S2–S8、S10；生成项目、隔离包源、日志和临时凭据位于 .tmp/oidc-e2e/<run>。
    依赖 PowerShell 7、.NET 10 SDK 和 Docker，不依赖 Python、OpenSSL 或宿主 psql。
    默认不等待令牌真实到期；-IncludeExpiryWait 以快速档（访问令牌 90 秒）执行撤销到期与交换令牌到期边界，CI 在 full 档传入。
    -IncludeBrowserScenarios 追加有头浏览器 S1/S9/S11、官方外部登录协议闭环，以及组织要求两步验证、确认身份与恢复码登录的身份链路。
    -BrowserOnly 只运行浏览器场景，不重复 HTTP 场景；另需 Node.js、npm 与 agent-browser。
    -IncludeMultiTenantScenarios 在浏览器场景后追加多租户验收 MT0–MT5（隐含浏览器场景）：
    租户行级/库级隔离、Resource 本地授权、委托调用的租户传递、两个 Resource 单点登录、退出与租户停用。
.PARAMETER DockerContext
    显式 Docker 上下文；其次使用 DOCKER_CONTEXT。未指定时本机优先 windows/orbstack，
    Linux 使用当前上下文，并可回落 default。不切换全局上下文。
#>
param(
    [ValidateSet("Debug", "Release")]
    [string]$Configuration = "Release",
    [switch]$SkipPack,
    [string]$LocalFeedPath,
    [string]$DockerContext,
    [switch]$IncludeExpiryWait,
    [switch]$IncludeBrowserScenarios,
    [switch]$BrowserOnly,
    [switch]$IncludeMultiTenantScenarios
)

$IncludeBrowserScenarios = $IncludeBrowserScenarios -or $BrowserOnly -or $IncludeMultiTenantScenarios
# 快速档：真实到期的场景把签发方的访问令牌寿命改为 90 秒，走生成项目的正式配置键。
# 签发寿命的断言按档位取固定期望值，键名写错或没传到子进程都会变红。
$quickAccessTokenLifetime = "00:01:30"
$quickAccessTokenSeconds = 90
$defaultAccessTokenSeconds = 600
$quickAccessTokens = $false
function Use-QuickAccessTokens([hashtable]$Environment) {
    $Environment.OAuth__AccessTokenLifetime = $quickAccessTokenLifetime
    $script:quickAccessTokens = $true
}
$ErrorActionPreference = "Stop"
Set-StrictMode -Version Latest
$repoRoot = (Resolve-Path (Join-Path $PSScriptRoot "..")).Path
$runId = "{0}-{1}" -f $PID, (Get-Date -Format "yyyyMMddHHmmssfff")
$runRoot = Join-Path $repoRoot ".tmp/oidc-e2e/$runId"
New-Item -ItemType Directory -Path $runRoot -Force | Out-Null
$utf8 = [Text.UTF8Encoding]::new($false)
$processes = @{}
$httpClients = [Collections.Generic.List[Net.Http.HttpClient]]::new()
$results = [Collections.Generic.List[object]]::new()
$cleanupErrors = [Collections.Generic.List[string]]::new()
$services = @{}
$ports = @{}
$urls = @{}
$allocatedPorts = [Collections.Generic.HashSet[int]]::new()
$context = $null
$container = "leistd-oidc-$runId"
$volume = "$container-data"
$image = "postgres:18-alpine"
$hadImage = $true
$createdContainer = $false
$createdVolume = $false
$assertionPath = Join-Path $runRoot "assertions.jsonl"
$baseEnvironment = @{
    MSBUILDDISABLENODEREUSE = "1"
    DOTNET_CLI_TELEMETRY_OPTOUT = "1"
}
$hostToken = $null
$sharedToken = $null
$sharedSession = $null
$admin = $null
$machine = $null
$sharedTenant = $null
$dedicatedTenant = $null
$redirect = $null
$issuer = $null
$sharedConnection = $null
$dedicatedConnection = $null
$plainClient = $null
$noDelegateClient = $null
$exchangeClient = $null
$serviceTokens = @{}
$delegatedExpiryToken = $null

function Write-JsonFile([string]$Path, $Value) {
    [IO.File]::WriteAllText($Path, (ConvertTo-Json -InputObject $Value -Depth 30), $utf8)
}

function New-RandomValue([string]$Prefix) {
    return $Prefix + [Convert]::ToHexString([Security.Cryptography.RandomNumberGenerator]::GetBytes(24))
}

function New-ProcessInfo([string]$Command, [string[]]$Arguments, [string]$WorkingDirectory, [hashtable]$Variables) {
    $info = [Diagnostics.ProcessStartInfo]::new()
    # PATH 上可能有同名的多个可执行文件（GitHub 的 Ubuntu 上 /usr/bin 与 /bin 都有 docker），只取第一个
    $info.FileName = (Get-Command $Command -CommandType Application -ErrorAction Stop | Select-Object -First 1).Source
    $info.WorkingDirectory = $WorkingDirectory
    $info.UseShellExecute = $false
    $info.RedirectStandardOutput = $true
    $info.RedirectStandardError = $true
    $info.RedirectStandardInput = $true
    foreach ($argument in $Arguments) { $info.ArgumentList.Add($argument) }
    foreach ($name in $baseEnvironment.Keys) { $info.Environment[$name] = $baseEnvironment[$name] }
    foreach ($name in $Variables.Keys) { $info.Environment[$name] = [string]$Variables[$name] }
    return $info
}

function Invoke-Tool(
    [string]$Command, [string[]]$Arguments, [string]$Log,
    [string]$WorkingDirectory = $repoRoot, [hashtable]$Variables = @{},
    [string]$StandardInput = "", [switch]$AllowFailure, [int]$TimeoutSeconds = 0
) {
    $process = [Diagnostics.Process]::new()
    $process.StartInfo = New-ProcessInfo $Command $Arguments $WorkingDirectory $Variables
    try {
        if (-not $process.Start()) { throw "无法启动 $Command。" }
        $stdoutTask = $process.StandardOutput.ReadToEndAsync()
        $stderrTask = $process.StandardError.ReadToEndAsync()
        if ($StandardInput) { $process.StandardInput.Write($StandardInput) }
        $process.StandardInput.Close()
        if ($TimeoutSeconds -gt 0 -and -not $process.WaitForExit($TimeoutSeconds * 1000)) {
            $process.Kill($true)
        }
        $process.WaitForExit()
        $stdout = $stdoutTask.GetAwaiter().GetResult()
        $stderr = $stderrTask.GetAwaiter().GetResult()
        [IO.File]::AppendAllText((Join-Path $runRoot "$Log.log"), "$stdout`n$stderr", $utf8)
        if ($process.ExitCode -ne 0 -and -not $AllowFailure) {
            throw "$Log 失败（退出 $($process.ExitCode)），见 $runRoot/$Log.log。"
        }
        return @{ ExitCode = $process.ExitCode; Output = $stdout.Trim(); Error = $stderr.Trim() }
    }
    finally { $process.Dispose() }
}

function Invoke-Docker([string[]]$Arguments, [switch]$AllowFailure, [hashtable]$Variables = @{}) {
    return Invoke-Tool "docker" (@("--context", $context) + $Arguments) "docker" -Variables $Variables -AllowFailure:$AllowFailure
}

function Select-DockerContext {
    $selected = if ($DockerContext) { $DockerContext } else { $env:DOCKER_CONTEXT }
    if ($selected) {
        $probe = Invoke-Tool "docker" @("--context", $selected, "version") "docker-explicit" -AllowFailure
        if ($probe.ExitCode -ne 0) { throw "指定的 Docker 上下文不可用：$selected。" }
        return $selected
    }
    $current = Invoke-Tool "docker" @("context", "show") "docker-current" -AllowFailure
    $candidates = if ($IsLinux) { @($current.Output, "default") } else { @("windows", "orbstack", $current.Output, "default") }
    foreach ($candidate in ($candidates | Where-Object { $_ } | Select-Object -Unique)) {
        $probe = Invoke-Tool "docker" @("--context", $candidate, "version") "docker-context-$candidate" -AllowFailure
        if ($probe.ExitCode -eq 0) { return $candidate }
    }
    throw "未找到可用的 Docker 上下文；可传 -DockerContext 或设置 DOCKER_CONTEXT。"
}

function Get-FreeTcpPort {
    do {
        $listener = [Net.Sockets.TcpListener]::new([Net.IPAddress]::Loopback, 0)
        $listener.Start()
        try { $port = ([Net.IPEndPoint]$listener.LocalEndpoint).Port }
        finally { $listener.Stop() }
    } while ($port -in @(4200, 5240, 5340, 4300) -or -not $allocatedPorts.Add($port))
    return $port
}

function New-TestCertificate([string]$Purpose) {
    $rsa = [Security.Cryptography.RSA]::Create(2048)
    try {
        $request = [Security.Cryptography.X509Certificates.CertificateRequest]::new(
            "CN=oidc-e2e-$Purpose", $rsa, [Security.Cryptography.HashAlgorithmName]::SHA256,
            [Security.Cryptography.RSASignaturePadding]::Pkcs1)
        $usage = if ($Purpose -eq "signing") {
            [Security.Cryptography.X509Certificates.X509KeyUsageFlags]::DigitalSignature
        } else { [Security.Cryptography.X509Certificates.X509KeyUsageFlags]::KeyEncipherment }
        $request.CertificateExtensions.Add([Security.Cryptography.X509Certificates.X509KeyUsageExtension]::new($usage, $true))
        $certificate = $request.CreateSelfSigned([DateTimeOffset]::UtcNow.AddMinutes(-5), [DateTimeOffset]::UtcNow.AddDays(2))
        try {
            [IO.File]::WriteAllBytes((Join-Path $runRoot "$Purpose.pfx"),
                $certificate.Export([Security.Cryptography.X509Certificates.X509ContentType]::Pfx, ""))
        }
        finally { $certificate.Dispose() }
    }
    finally { $rsa.Dispose() }
}

function Start-Api([string]$Name) {
    if ($processes.ContainsKey($Name)) { throw "$Name 已启动。" }
    $service = $services[$Name]
    $process = [Diagnostics.Process]::new()
    $process.StartInfo = New-ProcessInfo "dotnet" @($service.Api) (Split-Path $service.Api) $service.Environment
    $stdoutStream = [IO.FileStream]::new((Join-Path $runRoot "$Name.log"), [IO.FileMode]::Append,
        [IO.FileAccess]::Write, [IO.FileShare]::ReadWrite, 1, $true)
    $stderrStream = [IO.FileStream]::new((Join-Path $runRoot "$Name.stderr.log"), [IO.FileMode]::Append,
        [IO.FileAccess]::Write, [IO.FileShare]::ReadWrite, 1, $true)
    $processes[$Name] = @{ Process = $process; Stdout = $stdoutStream; Stderr = $stderrStream; Tasks = @() }
    if (-not $process.Start()) { throw "$Name 无法启动。" }
    $process.StandardInput.Close()
    $processes[$Name].Tasks = @(
        $process.StandardOutput.BaseStream.CopyToAsync($stdoutStream),
        $process.StandardError.BaseStream.CopyToAsync($stderrStream))
    Wait-Http $Name
}

function Stop-Api([string]$Name) {
    if (-not $processes.ContainsKey($Name)) { return }
    $entry = $processes[$Name]
    $process = $entry.Process
    if (-not $process.HasExited) { $process.Kill($true) }
    if (-not $process.WaitForExit(10000)) { throw "$Name 停止超时。" }
    foreach ($task in $entry.Tasks) { $task.GetAwaiter().GetResult() | Out-Null }
    $entry.Stdout.Dispose()
    $entry.Stderr.Dispose()
    $process.Dispose()
    $processes.Remove($Name)
}

function Get-LogOffsets([string]$Name) {
    return @{
        Stdout = [IO.File]::ReadAllText((Join-Path $runRoot "$Name.log")).Length
        Stderr = [IO.File]::ReadAllText((Join-Path $runRoot "$Name.stderr.log")).Length
    }
}

function Read-ApiLog([string]$Name, [hashtable]$Offsets = @{ Stdout = 0; Stderr = 0 }) {
    $stdout = [IO.File]::ReadAllText((Join-Path $runRoot "$Name.log"))
    $stderr = [IO.File]::ReadAllText((Join-Path $runRoot "$Name.stderr.log"))
    return $stdout.Substring($Offsets.Stdout) + "`n" + $stderr.Substring($Offsets.Stderr)
}

# 浏览器模式全程 HTTPS，以部署环境运行，让真实浏览器验证 __Host-Http- 会话 Cookie；
# 纯 HTTP 模式的签发方是 http 地址，只能以 Development 运行（不要求 HTTPS 元数据、Cookie 不带前缀）。
$serviceEnvironment = if ($IncludeBrowserScenarios) { "Production" } else { "Development" }
function Get-SessionCookieName([string]$Title) {
    if ($serviceEnvironment -eq "Development") { "E2E.$Title.Auth" } else { "__Host-Http-E2E.$Title.Auth" }
}

function New-HttpSession([switch]$Cookies) {
    $handler = if ($IncludeBrowserScenarios) { [OidcBrowserTls]::CreateHandler($browserCertificateThumbprint) } else { [Net.Http.HttpClientHandler]::new() }
    $handler.AllowAutoRedirect = $false
    $handler.UseCookies = $Cookies.IsPresent
    $client = [Net.Http.HttpClient]::new($handler)
    $client.Timeout = [TimeSpan]::FromSeconds(10)
    $httpClients.Add($client)
    return $client
}

function ConvertTo-Form([hashtable]$Values) {
    return (($Values.GetEnumerator() | ForEach-Object {
        [Uri]::EscapeDataString([string]$_.Key) + "=" + [Uri]::EscapeDataString([string]$_.Value)
    }) -join "&")
}

function Invoke-Http(
    [string]$Url, [string]$Method = "GET", $Body = $null,
    [string]$Token, [hashtable]$Headers = @{}, [Net.Http.HttpClient]$Session = $http
) {
    $request = [Net.Http.HttpRequestMessage]::new([Net.Http.HttpMethod]::new($Method), $Url)
    try {
        if ($Token) { $request.Headers.Authorization = [Net.Http.Headers.AuthenticationHeaderValue]::new("Bearer", $Token) }
        foreach ($name in $Headers.Keys) { $request.Headers.TryAddWithoutValidation($name, [string]$Headers[$name]) | Out-Null }
        if ($null -ne $Body) {
            $contentType = if ($Body -is [string]) { "application/x-www-form-urlencoded" } else { "application/json" }
            $text = if ($Body -is [string]) { $Body } else { ConvertTo-Json -InputObject $Body -Depth 20 -Compress }
            $request.Content = [Net.Http.StringContent]::new($text, [Text.Encoding]::UTF8, $contentType)
        }
        $response = $Session.SendAsync($request).GetAwaiter().GetResult()
        try {
            $raw = $response.Content.ReadAsStringAsync().GetAwaiter().GetResult()
            $data = try { ConvertFrom-Json -InputObject $raw -AsHashtable -ErrorAction Stop } catch { $raw }
            $location = if ($response.Headers.Location) { $response.Headers.Location.ToString() } else { $null }
            return @{ Status = [int]$response.StatusCode; Data = $data; Location = $location; Cookies = @(if ($response.Headers.Contains("Set-Cookie")) { $response.Headers.GetValues("Set-Cookie") }) }
        }
        finally { $response.Dispose() }
    }
    finally { $request.Dispose() }
}

function Assert-Value([string]$Label, $Expected, $Actual, [hashtable]$Details = @{}) {
    $passed = (ConvertTo-Json -InputObject $Expected -Depth 20 -Compress) -ceq
        (ConvertTo-Json -InputObject $Actual -Depth 20 -Compress)
    $entry = [ordered]@{ label = $Label; expected = $Expected; actual = $Actual; passed = $passed }
    foreach ($key in $Details.Keys) { $entry[$key] = $Details[$key] }
    [IO.File]::AppendAllText($assertionPath, ((ConvertTo-Json -InputObject $entry -Depth 20 -Compress) + "`n"), $utf8)
    if (-not $passed) { throw "${Label}：期望 '$Expected'，实际 '$Actual'。" }
}

function Write-Skipped([string]$Label, [string]$Reason) {
    [IO.File]::AppendAllText($assertionPath,
        ((@{ label = $Label; skipped = $true; reason = $Reason } | ConvertTo-Json -Compress) + "`n"), $utf8)
    Write-Host "跳过 ${Label}：$Reason"
}

function Assert-Http(
    [string]$Label, [int]$Status, [string]$Url, [string]$Method = "GET", $Body = $null,
    [string]$Token, [hashtable]$Headers = @{}, [Net.Http.HttpClient]$Session = $http
) {
    $response = Invoke-Http $Url -Method $Method -Body $Body -Token $Token -Headers $Headers -Session $Session
    # 仅保留状态和路由，不记录 token、Cookie、应用 Secret 或连接串响应。
    Assert-Value $Label $Status $response.Status @{ method = $Method; url = $Url }
    return $response
}

function Wait-Http([string]$Name, [string]$Path = "/api/health/live", [int]$Status = 200, [int]$TimeoutSeconds = 120) {
    $deadline = [DateTimeOffset]::UtcNow.AddSeconds($TimeoutSeconds)
    $last = $null
    do {
        if ($processes[$Name].Process.HasExited) { throw "$Name 已退出，见 $runRoot/$Name.log。" }
        try {
            $last = (Invoke-Http ($urls[$Name] + $Path)).Status
            if ($last -eq $Status) { return }
        }
        catch { $last = $_.Exception.GetType().Name }
        Start-Sleep -Milliseconds 500
    } while ([DateTimeOffset]::UtcNow -lt $deadline)
    throw "$Name$Path 未返回 $Status（最后状态 $last）。"
}

function Invoke-Sql([string]$Database, [string]$Sql) {
    $result = Invoke-Tool "docker" @("--context", $context, "exec", "-i", $container,
        "psql", "-U", "postgres", "-d", $Database, "-v", "ON_ERROR_STOP=1", "-At") "sql" -StandardInput $Sql
    return $result.Output
}

function Read-TokenClaims([string]$Token) {
    $part = $Token.Split('.')[1].Replace('-', '+').Replace('_', '/')
    $part = $part.PadRight([int]([Math]::Ceiling($part.Length / 4.0) * 4), '=')
    return ConvertFrom-Json ([Text.Encoding]::UTF8.GetString([Convert]::FromBase64String($part))) -AsHashtable
}

# 授权与退出请求启用了 OpenIddict 请求缓存：首个请求存为 request token 后重定向回同一端点（只带 request_uri），
# 这一跳之后才到控制器。断言这一跳确实发生，再按重入地址取控制器的响应。
function Assert-CachedHttp(
    [string]$Label, [int]$Status, [string]$Url, [string]$Method = "GET", $Body = $null,
    [hashtable]$Headers = @{}, [Net.Http.HttpClient]$Session = $http
) {
    $cached = Assert-Http "$Label-cached" 302 $Url -Method $Method -Body $Body -Headers $Headers -Session $Session
    $reentry = [Uri]::new([Uri]$Url, $cached.Location)
    Assert-Value "$Label-reentry-endpoint" ([Uri]$Url).AbsolutePath $reentry.AbsolutePath
    Assert-Value "$Label-reentry-request-uri" $true ((Read-Query $reentry.AbsoluteUri).ContainsKey("request_uri"))
    return Assert-Http $Label $Status $reentry.AbsoluteUri -Headers $Headers -Session $Session
}

# 官方 FormPost：Resource 以自动提交的表单把授权、退出请求发往 Identity，参数只在表单里，不在跳转地址上。
function Read-FormPost($Response, [string]$Action) {
    Assert-Value "form-post-no-location" $true ([string]::IsNullOrEmpty($Response.Location))
    $html = [string]$Response.Data
    $form = [regex]::Match($html, '<form(?=[^>]*method="post")[^>]*action="([^"]+)"', 'IgnoreCase')
    Assert-Value "form-post-action" $Action ([Net.WebUtility]::HtmlDecode($form.Groups[1].Value))
    $values = @{}
    foreach ($input in [regex]::Matches($html, '<input type="hidden" name="([^"]+)" value="([^"]*)"')) {
        $values[[Net.WebUtility]::HtmlDecode($input.Groups[1].Value)] = [Net.WebUtility]::HtmlDecode($input.Groups[2].Value)
    }
    return $values
}

function Read-Query([string]$Url) {
    $values = @{}
    foreach ($pair in ([Uri]$Url).Query.TrimStart('?').Split('&', [StringSplitOptions]::RemoveEmptyEntries)) {
        $parts = $pair.Split('=', 2)
        $values[[Uri]::UnescapeDataString($parts[0])] = [Uri]::UnescapeDataString($parts[1].Replace('+', ' '))
    }
    return $values
}

function Login([string]$Username = "admin", [string]$Tenant, [string]$Password = $adminPassword, [string]$Service = "idp") {
    $session = New-HttpSession -Cookies
    $headers = if ($Tenant) { @{ "X-Tenant" = $Tenant } } else { @{} }
    Assert-Http "session-login-$Username" 200 ($urls[$Service] + "/api/v1/auth/session-login") -Method POST `
        -Body @{ usernameOrEmail = $Username; password = $Password } -Headers $headers -Session $session | Out-Null
    return $session
}

function Get-CodeToken([Net.Http.HttpClient]$Session, [string]$Scope, [string]$ClientId = "orders-web") {
    # 每个 SPA 只申请自己的 API；下游范围由调用方的交换权限授予。
    $api = switch ($ClientId) { "billing-web" { "billing-api" }; "idp-web" { "e2e-idp-api" }; default { "orders-api" } }
    $Scope = (($Scope.Split(' ') | Where-Object { $_ -notin @("orders-api", "billing-api", "e2e-idp-api") }) + $api) -join ' '

    $verifier = [Convert]::ToBase64String([Security.Cryptography.RandomNumberGenerator]::GetBytes(48)).TrimEnd('=').Replace('+', '-').Replace('/', '_')
    $challenge = [Convert]::ToBase64String([Security.Cryptography.SHA256]::HashData([Text.Encoding]::UTF8.GetBytes($verifier))).TrimEnd('=').Replace('+', '-').Replace('/', '_')
    $state = New-RandomValue "state"
    $parameters = @{ response_type = "code"; client_id = $ClientId; redirect_uri = $redirect; scope = $Scope
        state = $state; code_challenge = $challenge; code_challenge_method = "S256" }
    $authorizeUrl = $urls.idp + "/connect/authorize?" + (ConvertTo-Form $parameters)
    $response = Assert-CachedHttp "authorize-code-pkce" 302 $authorizeUrl -Session $Session
    $query = Read-Query $response.Location
    Assert-Value "authorize-state" $state $query.state
    $form = @{ grant_type = "authorization_code"; client_id = $ClientId; redirect_uri = $redirect
        code = $query.code; code_verifier = (New-RandomValue "wrong") }
    Assert-Http "pkce-wrong-verifier" 400 ($urls.idp + "/connect/token") -Method POST -Body (ConvertTo-Form $form) | Out-Null
    # 错误交换可能消费授权码，成功路径重新申请，避免依赖协议实现的消费顺序。
    $response = Assert-CachedHttp "authorize-fresh-code" 302 $authorizeUrl -Session $Session
    $form.code = (Read-Query $response.Location).code
    $form.code_verifier = $verifier
    $data = (Assert-Http "code-exchange" 200 ($urls.idp + "/connect/token") -Method POST -Body (ConvertTo-Form $form)).Data
    Assert-Value "spa-no-refresh-token" $false $data.ContainsKey("refresh_token")
    $lifetime = if ($quickAccessTokens) { $quickAccessTokenSeconds } else { $defaultAccessTokenSeconds }
    Assert-Value "access-token-lifetime" $true ($data.expires_in -ge ($lifetime - 1) -and $data.expires_in -le $lifetime)
    $claims = Read-TokenClaims $data.access_token
    Assert-Value "jwt-exp-minus-iat" $lifetime ($claims.exp - $claims.iat)
    return $data.access_token
}

function Get-MachineToken([hashtable]$Client, [string]$Scope) {
    $form = @{ grant_type = "client_credentials"; client_id = $Client.clientId; client_secret = $Client.clientSecret; scope = $Scope }
    $data = (Assert-Http "client-credentials-$($Client.clientId)" 200 ($urls.idp + "/connect/token") `
        -Method POST -Body (ConvertTo-Form $form)).Data
    Assert-Value "machine-no-refresh-token" $false $data.ContainsKey("refresh_token")
    return $data.access_token
}

function Get-TenantToken([hashtable]$Tenant) {
    $session = Login -Tenant $Tenant.name -Password $tenantPassword
    $token = Get-CodeToken $session "openid profile email roles e2e-idp-api orders-api billing-api"
    Assert-Value "tenant-claim-$($Tenant.name)" $Tenant.id (Read-TokenClaims $token).e2e_tenant_id
    return @{ Session = $session; Token = $token }
}

function New-Application([string]$ClientId, [string[]]$Scopes, [switch]$Public, [switch]$Exchange, [string]$BrowserService) {
    $permissions = if ($Public) { @("ept:token", "gt:authorization_code", "ept:authorization", "ept:end_session", "rst:code") }
        else { @("ept:token", "gt:client_credentials") }
    if ($BrowserService) { $permissions = @("ept:token", "gt:authorization_code", "gt:refresh_token", "ept:authorization", "ept:end_session", "rst:code") }
    if ($Exchange) { $permissions += @("gt:urn:ietf:params:oauth:grant-type:token-exchange", "aud:billing-api") }
    $permissions += @($Scopes | ForEach-Object { "scp:$_" })
    # 只有服务端会话（BFF）跟随 Identity 登录会话收敛；SPA 公共客户端与机器客户端不绑定
    $body = @{ clientId = $ClientId; applicationType = $(if ($Public -or $BrowserService) { "web" } else { "service" })
        sessionBound = [bool]$BrowserService
        clientType = $(if ($Public) { "public" } else { "confidential" })
        redirectUris = @(); postLogoutRedirectUris = @(); permissions = $permissions; requirements = @() }
    if ($Public) { $body.redirectUris = @($redirect); $body.requirements = @("ft:pkce") }
    if ($BrowserService) {
        $body.redirectUris = @($urls[$BrowserService] + "/api/v1/auth/signin")
        $body.postLogoutRedirectUris = @($urls[$BrowserService] + "/api/v1/auth/signout")
        $body.requirements = @("ft:pkce")
    }
    return (Assert-Http "create-client-$ClientId" 200 ($urls.idp + "/api/v1/open-applications") `
        -Method POST -Body $body -Session $admin).Data
}

function New-Tenant([string]$Name, [string]$Connection) {
    $body = @{ name = $Name; displayName = $Name; adminEmail = "$Name@example.test"; adminPassword = $tenantPassword }
    if ($Connection) { $body.connections = @(@{ name = "default"; connectionString = $Connection }) }
    return (Assert-Http "create-tenant-$Name" 200 ($urls.idp + "/api/v1/tenants") -Method POST -Body $body -Session $admin).Data
}

function Add-FixtureRegistration([string]$Name, [string]$Role, [string]$ApiDirectory) {
    $title = [Globalization.CultureInfo]::InvariantCulture.TextInfo.ToTitleCase($Name)
    $registration = if ($Role -eq "Standalone") { "" } else {
        'services.AddAuthorization(o => o.AddPolicy("E2E.Machine", p => p.RequireAuthenticatedUser().RequireAssertion(c => c.User.FindFirst("sub")?.Value.StartsWith("client:") == true)));'
    }
    $registration += ' services.Configure<Leistd.Security.Claims.ClaimTypeOptions>(o => { o.UserIds = ["e2e_user_id"]; o.TenantId = "e2e_tenant_id"; });'
    $extraUsings = ""
    if ($Name -eq "idp") {
        $extraUsings = "using Microsoft.EntityFrameworkCore;`nusing E2E.Idp.Infrastructure.Persistence;"
        $registration += "`n" + 'services.AddScoped<PruningCommands>();'
        $registration += "`n" + 'services.AddDbContext<OpenIddictDbContext>((provider, options) => options.AddInterceptors(provider.GetRequiredService<PruningCommands>()));'
        [IO.File]::WriteAllText((Join-Path $ApiDirectory "PruningProbeController.cs"), $pruningSource, $utf8)
    }
    if ($Name -eq "orders") {
        $extraUsings = "using Leistd.ServiceClient.Refit;`nusing Leistd.ServiceClient.OAuth;"
        $registration += "`n" + 'services.AddServiceAuthentication();
services.AddRefitServiceClient<IBillingProbe, BillingOptions>("Billing").AddTokenExchange();'
        # 用 XML API 添加本轮探针的依赖，避免文本替换 csproj 结构。
        $projectPath = Join-Path $ApiDirectory "E2E.Orders.Api.csproj"
        $project = [xml][IO.File]::ReadAllText($projectPath)
        if ($project.DocumentElement.Name -ne "Project") { throw "生成的 API 项目没有 Project 根节点。" }
        $group = $project.CreateElement("ItemGroup")
        foreach ($package in @("Leistd.ServiceClient.Refit", "Refit")) {
            $reference = $project.CreateElement("PackageReference")
            $reference.SetAttribute("Include", $package)
            $group.AppendChild($reference) | Out-Null
        }
        $project.DocumentElement.AppendChild($group) | Out-Null
        $project.Save($projectPath)
        [IO.File]::WriteAllText((Join-Path $ApiDirectory "BillingProbe.cs"), $callerSource, $utf8)
    }
    $fixtureRegistration = @"
$extraUsings
namespace E2E.$title.Api;
internal static class E2EFixtureRegistration
{
    internal static void AddE2EFixtures(IServiceCollection services, IConfiguration configuration)
    {
        $registration
    }
}
"@
    [IO.File]::WriteAllText((Join-Path $ApiDirectory "E2EFixtureRegistration.cs"), $fixtureRegistration, $utf8)
    [IO.File]::WriteAllText((Join-Path $ApiDirectory "E2EProbeController.cs"), $probeSource.Replace("__NAME__", $title), $utf8)
    if ($Name -eq "idp") { [IO.File]::WriteAllText((Join-Path $ApiDirectory "FixtureTokenController.cs"), $signerSource, $utf8) }
    if ($IncludeBrowserScenarios) { Add-BrowserFixture $Name $ApiDirectory }
    # 唯一模板文本锚点；重复或缺失都失败，不能静默漏装探针管道。
    $programPath = Join-Path $ApiDirectory "Program.cs"
    $content = [IO.File]::ReadAllText($programPath)
    $anchor = "var app = builder.Build();"
    if ([regex]::Matches($content, [regex]::Escape($anchor)).Count -ne 1) { throw "$Name 的 Program.cs 组合根锚点必须恰好出现一次。" }
    $browserRegistration = if ($IncludeBrowserScenarios) { "E2E.$title.Api.BrowserFixtureRegistration.Add(builder.Services, builder.Configuration);`n    " } else { "" }
    $injection = "E2E.$title.Api.E2EFixtureRegistration.AddE2EFixtures(builder.Services, builder.Configuration);`n    $browserRegistration$anchor"
    [IO.File]::WriteAllText($programPath, $content.Replace($anchor, $injection), $utf8)
}

# 测试控制器仅写入 dotnet new 的生成目录，不进入模板或框架载荷。
$probeSource = @'
using Leistd.Security.Users;
using Leistd.Security.Clients;
using Leistd.MultiTenancy.Context;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
namespace E2E.__NAME__.Api;
[ApiController]
[Route("api/e2e")]
public sealed class E2EProbeController : ControllerBase
{
    [Authorize]
    [HttpGet("natural")]
    public object Natural([FromServices] ICurrentUser user, [FromServices] ICurrentTenant tenant,
        [FromServices] ICurrentClient client) => new { userId = user.Id, tenantId = tenant.Id, clientId = client.ClientId };
    [Authorize(Policy = "E2E.Machine")]
    [HttpGet("machine")]
    public object Machine([FromServices] ICurrentClient client) => new { clientId = client.ClientId };
}
'@

$callerSource = @'
using Leistd.ServiceClient.Options;
using Leistd.ServiceClient.Refit;
using Leistd.ServiceClient.OAuth;
using Refit;
namespace E2E.Orders.Api;
public sealed class BillingOptions : ServiceClientOptions;
public interface IBillingProbe
{
    [Get("/api/e2e/natural")]
    Task<BillingIdentity> NaturalAsync();
}
public sealed record BillingIdentity(Guid? UserId, Guid? TenantId, string? ClientId);
[Microsoft.AspNetCore.Mvc.ApiController]
[Microsoft.AspNetCore.Mvc.Route("api/e2e/billing")]
[Microsoft.AspNetCore.Authorization.Authorize]
public sealed class BillingProbeController(IBillingProbe billing) : Microsoft.AspNetCore.Mvc.ControllerBase
{
    [Microsoft.AspNetCore.Mvc.HttpGet]
    public Task<BillingIdentity> Get() => billing.NaturalAsync();
}
'@

$signerSource = @'
using System.Security.Cryptography.X509Certificates;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Leistd.Security.Users;
using Leistd.Security.Claims;
using Microsoft.Extensions.Options;
namespace E2E.Idp.Api;
[ApiController]
[Route("api/e2e/fixture-token")]
public sealed class FixtureTokenController(IConfiguration config, IOptions<ClaimTypeOptions> types) : ControllerBase
{
    [Authorize]
    [HttpGet]
    public object Get(string kind, [FromServices] ICurrentUser user, string? tenant = null)
    {
        using var cert = X509CertificateLoader.LoadPkcs12FromFile(config["OAuth:SigningCertificates:0:Path"]!, "");
        using var rsa = cert.GetRSAPrivateKey()!;
        var key = new RsaSecurityKey(rsa) { KeyId = cert.Thumbprint,
            CryptoProviderFactory = new CryptoProviderFactory { CacheSignatureProviders = false } };
        var now = DateTime.UtcNow;
        var claims = new Dictionary<string, object> { ["sub"] = user.Id!.Value.ToString(),
            ["aud"] = kind == "audience" ? new[] { "other-api" } : new[] { "orders-api", "billing-api" } };
        if (kind == "machine-tenant")
        {
            claims["sub"] = "client:orders-machine";
            claims["client_id"] = "orders-machine";
            claims["scope"] = "orders-api billing-api";
            claims[types.Value.TenantId] = Guid.NewGuid().ToString();
        }
        claims[types.Value.UserIds[0]] = claims["sub"];
        if (tenant is not null) claims[types.Value.TenantId] = tenant;
        if (kind == "chain") claims["act"] = new Dictionary<string, object> { ["sub"] = "client:previous-api" };
        if (kind == "source") claims["aud"] = new[] { "billing-api" };
        var descriptor = new SecurityTokenDescriptor {
            Issuer = kind == "issuer" ? "https://wrong.example.test/" : config["OAuth:Issuer"],
            Claims = claims, TokenType = "at+jwt", IssuedAt = now.AddMinutes(-20), NotBefore = now.AddMinutes(-20),
            Expires = kind == "expired" ? now.AddMinutes(-10) : kind == "short" ? now.AddSeconds(25) : now.AddMinutes(10),
            SigningCredentials = new SigningCredentials(key, SecurityAlgorithms.RsaSha256) };
        return new { access_token = new JsonWebTokenHandler().CreateToken(descriptor) };
    }
}
'@

$pruningSource = @'
using System.Data.Common;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;
using Leistd.BackgroundJobs.Recurring;
using E2E.Idp.Infrastructure.Persistence;
using OpenIddict.Abstractions;
using OpenIddict.EntityFrameworkCore;
using static OpenIddict.Abstractions.OpenIddictConstants;
namespace E2E.Idp.Api;
public sealed class PruningCommands : DbCommandInterceptor
{
    internal int TokenDeletes;
    internal int AuthorizationDeletes;
    public override ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(DbCommand command,
        CommandEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default)
    {
        if (command.CommandText.TrimStart().StartsWith("DELETE FROM", StringComparison.OrdinalIgnoreCase))
        {
            if (command.CommandText.Contains("OpenIddictTokens", StringComparison.Ordinal)) TokenDeletes++;
            if (command.CommandText.Contains("OpenIddictAuthorizations", StringComparison.Ordinal)) AuthorizationDeletes++;
        }
        return ValueTask.FromResult(result);
    }
}
[ApiController]
[Authorize]
[Route("api/e2e/pruning")]
public sealed class PruningProbeController(IOpenIddictTokenManager tokens, IOpenIddictAuthorizationManager authorizations,
    OpenIddictDbContext db, IOptions<OpenIddictEntityFrameworkCoreOptions> options, PruningCommands commands,
    IServiceProvider services) : ControllerBase
{
    [HttpPost]
    public async Task<object> Run(CancellationToken cancellationToken)
    {
        var now = DateTimeOffset.UtcNow;
        async Task<string> Token(DateTimeOffset created, DateTimeOffset expiry)
        {
            var token = await tokens.CreateAsync(new OpenIddictTokenDescriptor
            { Subject = Guid.NewGuid().ToString(), Type = TokenTypeHints.AccessToken, Status = Statuses.Valid,
              CreationDate = created, ExpirationDate = expiry }, cancellationToken);
            return (await tokens.GetIdAsync(token, cancellationToken))!;
        }
        async Task<string> Authorization(DateTimeOffset created, string status, string type)
        {
            var authorization = await authorizations.CreateAsync(new OpenIddictAuthorizationDescriptor
            { Subject = Guid.NewGuid().ToString(), Type = type, Status = status, CreationDate = created }, cancellationToken);
            return (await authorizations.GetIdAsync(authorization, cancellationToken))!;
        }
        var seeded = new {
            oldExpired = await Token(now.AddDays(-15), now.AddDays(-1)),
            youngExpired = await Token(now.AddDays(-13), now.AddDays(-1)),
            oldValid = await Token(now.AddDays(-15), now.AddDays(1)),
            oldRevoked = await Authorization(now.AddDays(-15), Statuses.Revoked, AuthorizationTypes.Permanent),
            youngRevoked = await Authorization(now.AddDays(-13), Statuses.Revoked, AuthorizationTypes.Permanent),
            oldPermanent = await Authorization(now.AddDays(-15), Statuses.Valid, AuthorizationTypes.Permanent),
            oldAdHoc = await Authorization(now.AddDays(-15), Statuses.Valid, AuthorizationTypes.AdHoc)
        };
        var definition = services.GetServices<RecurringJobDefinition>().Single(job => job.Name == Leistd.Security.OpenIddict.Server.Pruning.OpenIddictPruningJob.Name);
        var job = (IRecurringJob)services.GetRequiredService(definition.JobType);
        await job.ExecuteAsync(new RecurringJobContext(definition.Name, now), cancellationToken);
        return new { seeded, provider = db.Database.ProviderName, bulkDisabled = options.Value.DisableBulkOperations,
            tokenDeletes = commands.TokenDeletes, authorizationDeletes = commands.AuthorizationDeletes };
    }
}
'@

function Test-PostgresqlPruning {
    $probe = (Assert-Http "pruning-job-postgresql" 200 ($urls.idp + "/api/e2e/pruning") -Method POST -Session $admin).Data
    Assert-Value "pruning-provider" "Npgsql.EntityFrameworkCore.PostgreSQL" $probe.provider
    Assert-Value "pruning-bulk-enabled" $false $probe.bulkDisabled
    Assert-Value "pruning-token-bulk-delete-command" $true ($probe.tokenDeletes -gt 0)
    Assert-Value "pruning-authorization-bulk-delete-command" $true ($probe.authorizationDeletes -gt 0)
    foreach ($record in @(
        @{ Name = "oldExpired"; Table = "OpenIddictTokens"; Count = "0" },
        @{ Name = "youngExpired"; Table = "OpenIddictTokens"; Count = "1" },
        @{ Name = "oldValid"; Table = "OpenIddictTokens"; Count = "1" },
        @{ Name = "oldRevoked"; Table = "OpenIddictAuthorizations"; Count = "0" },
        @{ Name = "youngRevoked"; Table = "OpenIddictAuthorizations"; Count = "1" },
        @{ Name = "oldPermanent"; Table = "OpenIddictAuthorizations"; Count = "1" },
        @{ Name = "oldAdHoc"; Table = "OpenIddictAuthorizations"; Count = "0" }
    )) {
        $id = [string]$probe.seeded[$record.Name]
        [Guid]::Parse($id) | Out-Null
        $query = 'SELECT COUNT(*) FROM "e2e-idp"."' + $record.Table + '" WHERE "Id"=' + "'$id';"
        Assert-Value "pruning-record-$($record.Name)" $record.Count (Invoke-Sql "oidc_shared" $query)
    }
}

function Initialize-Environment {
    Write-Host "Artifacts: $runRoot"
    if ($IncludeBrowserScenarios) {
        foreach ($tool in @('npm', 'agent-browser')) { Get-Command $tool -CommandType Application -ErrorAction Stop | Select-Object -First 1 | Out-Null }
    }
    $script:context = Select-DockerContext
    $script:hadImage = (Invoke-Docker @("image", "inspect", $image) -AllowFailure).ExitCode -eq 0
    if (-not $hadImage) { Invoke-DockerPullWithRetry $image -Pull { param($Name) (Invoke-Docker @("pull", $Name) -AllowFailure).ExitCode } }
    $feed = if ($SkipPack) {
        if ($LocalFeedPath) { [IO.Path]::GetFullPath($LocalFeedPath, $repoRoot) } else { Join-Path $repoRoot ".tmp/local-feed" }
    } else {
        if ($LocalFeedPath) { throw "-LocalFeedPath 必须同时使用 -SkipPack。" }
        Join-Path $runRoot "local-feed"
    }
    if ($SkipPack) {
        $relativeFeed = [IO.Path]::GetRelativePath((Join-Path $repoRoot ".tmp"), $feed)
        if ($relativeFeed -eq ".." -or $relativeFeed.StartsWith("../") -or $relativeFeed.StartsWith('..\') -or [IO.Path]::IsPathRooted($relativeFeed)) {
            throw "复用的本地包源必须位于仓库 .tmp 内。"
        }
    } else {
        Invoke-Tool "dotnet" @("pack", (Join-Path $repoRoot "framework/Leistd.Framework.slnx"), "-c", $Configuration, "-o", $feed,
            "-p:UseSharedCompilation=false", "-nodeReuse:false") "pack" | Out-Null
    }
    if (-not (Get-ChildItem -LiteralPath $feed -Filter "Leistd.*.nupkg")) { throw "本地包源为空：$feed。" }
    $hive = Join-Path $runRoot "template-hive"
    $packages = Join-Path $runRoot "nuget-cache"
    $nugetConfig = Join-Path $runRoot "NuGet.Config"
    $escapedFeed = [Security.SecurityElement]::Escape($feed)
    $escapedPackages = [Security.SecurityElement]::Escape($packages)
    [IO.File]::WriteAllText($nugetConfig, @"
<configuration><config><add key="globalPackagesFolder" value="$escapedPackages" /></config>
<packageSources><clear/><add key="local" value="$escapedFeed" />
<add key="nuget.org" value="https://api.nuget.org/v3/index.json" /></packageSources></configuration>
"@, $utf8)
    Invoke-Tool "dotnet" @("new", "--debug:custom-hive", $hive, "install", (Join-Path $repoRoot "template"), "--force") "install" | Out-Null
    foreach ($purpose in @("signing", "encryption")) { New-TestCertificate $purpose }
    foreach ($name in @("postgres", "idp", "orders", "billing", "solo")) {
        $ports[$name] = Get-FreeTcpPort
        $scheme = if ($IncludeBrowserScenarios -and $name -ne "postgres") { "https" } else { "http" }
        $urls[$name] = "${scheme}://127.0.0.1:$($ports[$name])"
    }
    $script:issuer = $urls.idp + "/"
    # HTTP 用例不启动前端，但公共应用仍登记真实源格式的回调。
    $callbackPort = Get-FreeTcpPort
    $script:redirect = "http://127.0.0.1:$callbackPort/auth/callback"
    foreach ($definition in @(@("idp", "Identity"), @("orders", "Resource"), @("billing", "Resource"), @("solo", "Standalone"))) {
        $name, $role = $definition
        $title = [Globalization.CultureInfo]::InvariantCulture.TextInfo.ToTitleCase($name)
        $generated = Join-Path $runRoot "generated/$name"
        $scenario = switch ($name) {
            "idp" { if ($IncludeBrowserScenarios) { "identity-external-login" } else { "identity-notifications" } }
            "solo" { "standalone" }
            default { "resource" }
        }
        $arguments = @("new", "--debug:custom-hive", $hive, "fullstack-app", "-n", "E2E.$title", "-o", $generated, "--force") + $scenarioMap[$scenario].Arguments
        if ($IncludeBrowserScenarios -and $name -eq 'orders') {
            # 同一资源服务实际认证通知与业务实时共用 Hub/连接，并验证列表刷新。
            $arguments += @('--include-real-time', 'true', '--include-notifications', 'true')
        }
        if (-not $IncludeBrowserScenarios -and $name -eq 'billing') {
            $arguments += @('--include-frontend', 'false', '--include-real-time', 'true')
        }
        Invoke-Tool "dotnet" $arguments "generate-$name" | Out-Null
        $apiDirectory = Join-Path $generated "backend/src/E2E.$title.Api"
        Add-FixtureRegistration $name $role $apiDirectory
        $solution = Join-Path $generated "backend/E2E.$title.sln"
        Invoke-Tool "dotnet" @("restore", $solution, "--configfile", $nugetConfig, "--force") "restore-$name" | Out-Null
        Invoke-Tool "dotnet" @("build", $solution, "-c", $Configuration, "--no-restore", "-p:UseSharedCompilation=false", "-nodeReuse:false") "build-$name" | Out-Null
        $environment = @{
            ASPNETCORE_ENVIRONMENT = $serviceEnvironment; ASPNETCORE_URLS = $urls[$name]
            ConnectionStrings__Redis = ""; DataProtection__KeysPath = (Join-Path $runRoot "keys-$name")
            DefaultAdmin__Username = "admin"; DefaultAdmin__Password = $adminPassword
            Leistd__Security__VerificationCodes__Key = [Convert]::ToBase64String([Security.Cryptography.RandomNumberGenerator]::GetBytes(32))
        }
        if ($IncludeBrowserScenarios) {
            $environment.ASPNETCORE_Kestrel__Certificates__Default__Path = Join-Path $runRoot "browser-tls.pfx"
            $environment.E2E__CertificateThumbprint = $browserCertificateThumbprint
            $environment.E2E__BrowserSecret = $browserFixtureSecret
            $environment.E2E__EvidenceDirectory = $runRoot
            if ($name -eq "idp") {
                $environment.E2E__ProviderOrigin = $urls.idp
                foreach ($provider in @("Google", "Github")) {
                    # 开关默认关闭（部署配置给基线，系统设置可运行期覆盖）：协议闭环在部署配置里打开
                    $environment["ExternalAuth__${provider}__Enabled"] = "true"
                    $environment["ExternalAuth__${provider}__ClientId"] = "browser-fixture-$provider"
                    $environment["ExternalAuth__${provider}__ClientSecret"] = $browserFixtureSecret
                }
            }
        }
        if ($name -eq "idp" -and ($BrowserOnly -or $IncludeExpiryWait)) { Use-QuickAccessTokens $environment }
        if ($role -eq "Identity") {
            $environment.OAuth__Issuer = $issuer
            $environment.OAuth__DisableHttpsRequirement = "true"
            $environment.OAuth__UseDevelopmentCertificates = "false"
            $environment.OAuth__SigningCertificates__0__Path = Join-Path $runRoot "signing.pfx"
            $environment.OAuth__EncryptionCertificates__0__Path = Join-Path $runRoot "encryption.pfx"
            $environment.OAuth__ApiResources__0__Name = "orders-api"
            $environment.OAuth__ApiResources__1__Name = "billing-api"
        }
        if ($role -eq "Resource") {
            $environment.Authentication__Issuer = $issuer
            $environment.Authentication__Audience = "$name-api"
            $environment.Leistd__ServiceClients__Identity__BaseAddress = $urls.idp
            $environment.Leistd__ServiceClients__Identity__Scope = "tenant-routing.read"
            $environment.Leistd__ServiceAuth__Authority = $urls.idp
            $environment.Leistd__MultiTenancy__Routing__CacheLifetime = "00:00:01"
        }
        $frontends = if ($IncludeMultiTenantScenarios) { @("idp", "orders", "billing") } else { @("idp", "orders") }
        if ($IncludeBrowserScenarios -and $name -in $frontends) { Build-BrowserFrontend $name $generated $apiDirectory }
        $services[$name] = @{
            Root = $generated; Environment = $environment
            Api = Join-Path $apiDirectory "bin/$Configuration/net10.0/E2E.$title.Api.dll"
            Migrator = Join-Path $generated "backend/src/E2E.$title.DbMigrator/bin/$Configuration/net10.0/E2E.$title.DbMigrator.dll"
        }
    }
    # 在可能失败的创建操作前登记清理责任；只清理本轮命名的资源。
    $script:createdVolume = $true
    Invoke-Docker @("volume", "create", $volume) | Out-Null
    $script:createdContainer = $true
    Invoke-Docker @("run", "-d", "--name", $container, "-e", "POSTGRES_PASSWORD", "-p", "127.0.0.1:$($ports.postgres):5432",
        "-v", "${volume}:/var/lib/postgresql", $image) -Variables @{ POSTGRES_PASSWORD = $postgresPassword } | Out-Null
    $deadline = [DateTimeOffset]::UtcNow.AddSeconds(60)
    do {
        $ready = Invoke-Docker @("exec", $container, "pg_isready", "-h", "127.0.0.1", "-U", "postgres") -AllowFailure
        if ($ready.ExitCode -eq 0) { break }
        Start-Sleep -Milliseconds 500
    } while ([DateTimeOffset]::UtcNow -lt $deadline)
    if ($ready.ExitCode -ne 0) { throw "PostgreSQL 启动超时。" }
    foreach ($database in @("oidc_shared", "oidc_dedicated")) { Invoke-Docker @("exec", $container, "createdb", "-U", "postgres", $database) | Out-Null }
    $script:sharedConnection = "Host=127.0.0.1;Port=$($ports.postgres);Database=oidc_shared;Username=postgres;Password=$postgresPassword"
    $script:dedicatedConnection = $sharedConnection.Replace("oidc_shared", "oidc_dedicated")
    foreach ($name in @("idp", "orders", "billing", "solo")) {
        $service = $services[$name]
        $service.Environment.ConnectionStrings__Default = $sharedConnection
        foreach ($target in @(@("shared", $sharedConnection), @("dedicated", $dedicatedConnection))) {
            $migrationEnvironment = $service.Environment.Clone()
            $migrationEnvironment.ConnectionStrings__MigrationTarget = $target[1]
            Invoke-Tool "dotnet" @($service.Migrator, "--apply") "migrate-$name-$($target[0])" -Variables $migrationEnvironment | Out-Null
        }
    }
    foreach ($name in @("idp", "solo")) {
        Invoke-Tool "dotnet" @($services[$name].Migrator, "--apply") "migrate-control-$name" -Variables $services[$name].Environment | Out-Null
    }
    Start-Api "idp"
    Start-Api "solo"
    $script:admin = Login
    New-Application "orders-web" @("openid", "profile", "email", "roles", "orders-api") -Public | Out-Null
    New-Application "billing-web" @("openid", "profile", "email", "roles", "billing-api") -Public | Out-Null
    New-Application "idp-web" @("openid", "profile", "email", "roles", "e2e-idp-api") -Public | Out-Null
    $script:exchangeClient = New-Application "orders-api" @("billing-api", "tenant-routing.read") -Exchange
    $script:machine = New-Application "orders-machine" @("orders-api", "billing-api", "tenant-routing.read")
    $script:plainClient = New-Application "orders-only" @("orders-api")
    $script:noDelegateClient = New-Application "no-delegate" @("orders-api", "billing-api")
    foreach ($name in @("orders", "billing")) {
        $environment = $services[$name].Environment
        $browser = New-Application "$name-browser" @("openid", "profile", "email", "roles", "offline_access", "$name-api") -BrowserService $name
        $environment.Authentication__ClientId = $browser.clientId
        $environment.Authentication__ClientSecret = $browser.clientSecret
        $environment.Leistd__ServiceAuth__ClientId = $exchangeClient.clientId
        $environment.Leistd__ServiceAuth__ClientSecret = $exchangeClient.clientSecret
        $environment.Leistd__ServiceClients__Billing__BaseAddress = $urls.billing
        $environment.Leistd__ServiceClients__Billing__TokenExchange__Audience = "billing-api"
        $environment.Leistd__ServiceClients__Billing__TokenExchange__Scope = "billing-api"
        Start-Api $name
        Wait-Http $name "/api/health/ready"
    }
    $script:sharedTenant = New-Tenant "shared-e2e"
    $script:dedicatedTenant = New-Tenant "dedicated-e2e" $dedicatedConnection
    Write-JsonFile (Join-Path $runRoot "topology.json") @{
        ports = $ports; issuer = $issuer; redirectUri = $redirect; dockerContext = $context
        container = $container; volume = $volume; includeExpiryWait = $IncludeExpiryWait.IsPresent
    }
}

function Exchange-Token([string]$Subject, [hashtable]$Overrides = @{}, [int]$Status = 200) {
    $form = @{ grant_type = "urn:ietf:params:oauth:grant-type:token-exchange"; client_id = $exchangeClient.clientId
        client_secret = $exchangeClient.clientSecret; subject_token = $Subject
        subject_token_type = "urn:ietf:params:oauth:token-type:access_token"
        requested_token_type = "urn:ietf:params:oauth:token-type:access_token"; audience = "billing-api"; scope = "billing-api" }
    foreach ($key in $Overrides.Keys) { $form[$key] = $Overrides[$key] }
    $response = Assert-Http "exchange-protocol-$Status" $Status ($urls.idp + "/connect/token") -Method POST -Body (ConvertTo-Form $form)
    if ($Status -eq 200) {
        Assert-Value "exchange-issued-type" "urn:ietf:params:oauth:token-type:access_token" $response.Data.issued_token_type
        Assert-Value "exchange-no-refresh" $false $response.Data.ContainsKey("refresh_token")
        return $response.Data.access_token
    }
    return $response.Data
}

function Get-ServiceToken([string]$Subject, [string]$Name) {
    if ($Name -ne "billing") { return $Subject }
    if (-not $serviceTokens.ContainsKey($Subject)) { $serviceTokens[$Subject] = Exchange-Token $Subject }
    return $serviceTokens[$Subject]
}

function Test-Exchange([string]$Subject, [hashtable]$SourceClaims) {
    $output = Get-ServiceToken $Subject "billing"
    $claims = Read-TokenClaims $output
    Assert-Value "exchange-user" $SourceClaims.sub $claims.sub
    Assert-Value "exchange-custom-user" $SourceClaims.e2e_user_id $claims.e2e_user_id
    Assert-Value "exchange-custom-tenant" $SourceClaims.e2e_tenant_id $claims.e2e_tenant_id
    Assert-Value "exchange-audience-only-target" @("billing-api") @($claims.aud)
    Assert-Value "exchange-scope-only-target" "billing-api" $claims.scope
    Assert-Value "exchange-actor" "client:orders-api" $claims.act.sub
    # 交换令牌取 120 秒上限与源令牌到期时刻中较早的一个；默认档源令牌更长，落在 120 秒上限
    Assert-Value "exchange-lifetime" ([Math]::Min($claims.iat + 120, $SourceClaims.exp)) $claims.exp
    Assert-Value "exchange-bounded-by-subject" $true ($claims.exp -le $SourceClaims.exp)
    Assert-Value "exchange-no-roles" $false $claims.ContainsKey("role")
    Assert-Value "exchange-no-super-admin" $false $claims.ContainsKey("is_super_admin")
    foreach ($field in @("preferred_username", "email", "name")) { Assert-Value "exchange-authority-$field" $SourceClaims[$field] $claims[$field] }
    Assert-Http "source-token-cannot-call-billing" 401 ($urls.billing + "/api/e2e/natural") -Token $Subject | Out-Null
    Assert-Http "target-token-cannot-call-orders" 401 ($urls.orders + "/api/e2e/natural") -Token $output | Out-Null
    foreach ($overrides in @(@{ audience = "unknown-api" }, @{ audience = "orders-api"; scope = "orders-api" },
        @{ scope = "orders-api" }, @{ requested_token_type = "urn:ietf:params:oauth:token-type:refresh_token" },
        @{ requested_token_type = "urn:ietf:params:oauth:token-type:id_token" }, @{ subject_token_type = "urn:ietf:params:oauth:token-type:id_token" },
        @{ actor_token = $Subject; actor_token_type = "urn:ietf:params:oauth:token-type:access_token" },
        @{ client_id = $plainClient.clientId; client_secret = $plainClient.clientSecret })) {
        Exchange-Token $Subject $overrides 400 | Out-Null
    }
    $resourceError = Exchange-Token $Subject @{ resource = "https://billing.example.com/" } 400
    Assert-Value "exchange-unregistered-resource" "invalid_target" $resourceError.error
    foreach ($kind in @("source", "chain", "machine-tenant", "expired")) {
        $fixture = (Assert-Http "exchange-negative-fixture-$kind" 200 ($urls.idp + "/api/e2e/fixture-token?kind=$kind") -Session $admin).Data.access_token
        Exchange-Token $fixture @{} 400 | Out-Null
    }
    $fixture = (Assert-Http "membership-fixture" 200 ($urls.idp + "/api/e2e/fixture-token?kind=valid&tenant=" + $sharedTenant.id) -Session $admin).Data.access_token
    Exchange-Token $fixture @{} 400 | Out-Null
    $short = (Assert-Http "short-subject-fixture" 200 ($urls.idp + "/api/e2e/fixture-token?kind=short") -Session $admin).Data.access_token
    $shortOutput = Exchange-Token $short
    Assert-Value "final-jwt-subject-exp-cap" (Read-TokenClaims $short).exp (Read-TokenClaims $shortOutput).exp
    $script:delegatedExpiryToken = $shortOutput
    # 同一用户交替通过 Billing SPA 和交换令牌投影，权威资料保持不变。
    $spa = Get-CodeToken $sharedSession "openid profile email billing-api" "billing-web"
    foreach ($token in @($spa, $output, $spa, $output)) {
        Assert-Http "alternating-profile" 200 ($urls.billing + "/api/e2e/natural") -Token $token | Out-Null
        $query = 'SELECT "Username"||''|''||"Email"||''|''||"DisplayName" FROM "e2e-billing"."Users" WHERE "Id"=''{0}'' AND "TenantId"=''{1}'';' -f $SourceClaims.sub, $SourceClaims.e2e_tenant_id
        Assert-Value "profile-stable-in-database" ($SourceClaims.preferred_username + "|" + $SourceClaims.email + "|" + $SourceClaims.name) (Invoke-Sql "oidc_shared" $query)
    }
}

function Test-BrowserSession {
    $challenge = Assert-Http "resource-browser-challenge" 200 ($urls.orders + "/api/v1/auth/login?returnUrl=/workspace")
    $parameters = Read-FormPost $challenge ($urls.idp + "/connect/authorize")
    Assert-Value "browser-code-flow" "code" $parameters.response_type
    Assert-Value "browser-pkce" "S256" $parameters.code_challenge_method
    $authorize = Assert-CachedHttp "browser-authorize" 200 ($urls.idp + "/connect/authorize") -Method POST `
        -Body (ConvertTo-Form $parameters) -Session $sharedSession
    $form = @{}
    foreach ($input in [regex]::Matches([string]$authorize.Data, '<input[^>]*name="([^"]+)"[^>]*value="([^"]*)"')) {
        $form[$input.Groups[1].Value] = [Net.WebUtility]::HtmlDecode($input.Groups[2].Value)
    }
    if (-not $form.ContainsKey("code") -or -not $form.ContainsKey("state")) { throw "OIDC form_post response contains no code/state." }
    $correlation = ($challenge.Cookies | ForEach-Object { $_.Split(';')[0] }) -join "; "
    $callback = Assert-Http "resource-browser-callback" 302 ($urls.orders + "/api/v1/auth/signin") -Method POST `
        -Body (ConvertTo-Form $form) -Headers @{ Cookie = $correlation }
    Assert-Value "browser-return-url" "/workspace" $callback.Location
    $cookie = ($callback.Cookies | Where-Object { $_.StartsWith((Get-SessionCookieName "Orders") + "=") } | ForEach-Object { $_.Split(';')[0] }) -join "; "
    if (-not $cookie) { throw "OIDC callback created no reference cookie." }
    $me = (Assert-Http "resource-browser-me" 200 ($urls.orders + "/api/v1/auth/me") -Headers @{ Cookie = $cookie }).Data
    $claims = Read-TokenClaims $sharedToken
    Assert-Value "browser-session-user" $claims.sub $me.id
    Assert-Value "browser-session-tenant" $sharedTenant.id $me.tenantId
    Assert-Http "browser-cookie-delegation" 200 ($urls.orders + "/api/e2e/billing") -Headers @{ Cookie = $cookie } | Out-Null
    Assert-Http "browser-no-bearer-fallback" 401 ($urls.orders + "/api/v1/auth/me") -Token "invalid" -Headers @{ Cookie = $cookie } | Out-Null
    $logout = Assert-Http "resource-browser-logout" 200 ($urls.orders + "/api/v1/auth/logout") -Method POST -Body @{} -Headers @{ Cookie = $cookie }
    $logoutParameters = Read-FormPost $logout ($urls.idp + "/connect/logout")
    # hint 只在表单正文里：Identity 据其中的会话标识免确认退出（浏览器闭环见 S11 与 MT4）
    Assert-Value "logout-hint-in-form-body" $true (-not [string]::IsNullOrEmpty($logoutParameters["id_token_hint"]))
    Assert-Value "logout-hint-session" $true ((Read-TokenClaims $logoutParameters["id_token_hint"]).ContainsKey("sid"))
    Assert-Http "browser-logout-invalidates-copied-cookie" 401 ($urls.orders + "/api/v1/auth/me") -Headers @{ Cookie = $cookie } | Out-Null
}

function Test-S2 {
    $script:hostToken = Get-CodeToken $admin "openid profile email roles e2e-idp-api orders-api billing-api"
    $claims = Read-TokenClaims $hostToken
    Assert-Value "scope-derived-audiences" @("orders-api") @($claims.aud | Sort-Object)
    foreach ($name in @("orders", "billing")) {
        $data = (Assert-Http "human-token-$name" 200 ($urls[$name] + "/api/e2e/natural") -Token (Get-ServiceToken $hostToken $name)).Data
        Assert-Value "human-sub-$name" $claims.sub $data.userId
    }
    $ordersToken = Get-CodeToken $admin "openid profile orders-api"
    Assert-Http "orders-audience-positive" 200 ($urls.orders + "/api/e2e/natural") -Token $ordersToken | Out-Null
    Assert-Http "orders-audience-negative-billing" 401 ($urls.billing + "/api/e2e/natural") -Token $ordersToken | Out-Null
    $parameters = @{ response_type = "code"; client_id = "orders-web"; redirect_uri = $redirect; scope = "openid orders-api" }
    Assert-Http "pkce-missing-challenge" 400 ($urls.idp + "/connect/authorize?" + (ConvertTo-Form $parameters)) -Session $admin | Out-Null
}

function Test-PureApiRealtime {
    Assert-Value 'pure-api-no-frontend' $false (Test-Path (Join-Path $runRoot 'generated/billing/frontend'))
    Assert-Http 'pure-api-no-spa-fallback' 404 ($urls.billing + '/') | Out-Null
    $token = Get-CodeToken $admin 'openid profile billing-api' 'billing-web'
    $subject = (Read-TokenClaims $token).sub
    Invoke-Tool 'dotnet' @($services.billing.Migrator, '--grant-admin', $subject, '--apply') 'pure-api-realtime-admin' -Variables $services.billing.Environment | Out-Null
    $probe = Join-Path $runRoot 'realtime-client'
    New-Item -ItemType Directory -Path $probe -Force | Out-Null
    $versions = [xml](Get-Content -LiteralPath (Join-Path $runRoot 'generated/billing/backend/Directory.Packages.props') -Raw)
    $clientVersion = [string]($versions.Project.ItemGroup.PackageVersion | Where-Object { $_.Include -eq 'Microsoft.AspNetCore.SignalR.Client' }).Version
    if (-not $clientVersion) { throw 'SignalR client version is absent from the generated dependency contract.' }
    $clientProject = @'
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup><OutputType>Exe</OutputType><TargetFramework>net10.0</TargetFramework><ImplicitUsings>enable</ImplicitUsings><Nullable>enable</Nullable></PropertyGroup>
  <ItemGroup><PackageReference Include="Microsoft.AspNetCore.SignalR.Client" Version="CLIENT_VERSION" /></ItemGroup>
</Project>
'@
    [IO.File]::WriteAllText((Join-Path $probe 'Realtime.Client.csproj'), $clientProject.Replace('CLIENT_VERSION', $clientVersion), $utf8)
    [IO.File]::WriteAllText((Join-Path $probe 'Program.cs'), @'
using System.Net.Http.Headers;
using System.Net.Http.Json;
using Microsoft.AspNetCore.SignalR.Client;

var root = Environment.GetEnvironmentVariable("E2E_REALTIME_URL")!;
var token = Environment.GetEnvironmentVariable("E2E_REALTIME_TOKEN")!;
await using var connection = new HubConnectionBuilder().WithUrl(root + "/hubs/realtime", options =>
    options.AccessTokenProvider = () => Task.FromResult<string?>(token)).Build();
var changed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
connection.On<object>("Roles.Changed", _ => changed.TrySetResult());
await connection.StartAsync();
await connection.InvokeAsync("Subscribe", "host:roles");
foreach (var forbidden in new[] { Guid.NewGuid().ToString("N") + ":roles", "host:secrets", "roles" })
{
    try
    {
        await connection.InvokeAsync("Subscribe", forbidden);
        throw new InvalidOperationException("An unauthorized resource subscription was accepted.");
    }
    catch (Microsoft.AspNetCore.SignalR.HubException exception) when (exception.Message.Contains("Subscription forbidden", StringComparison.OrdinalIgnoreCase)) { }
}
using var client = new HttpClient();
client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
var name = "rt_" + Guid.NewGuid().ToString("N")[..12];
using var created = await client.PostAsJsonAsync(root + "/api/v1/roles", new { name, displayName = name });
created.EnsureSuccessStatusCode();
await changed.Task.WaitAsync(TimeSpan.FromSeconds(15));
await connection.StopAsync();
Console.WriteLine("PASS: pure API authenticated external client, scope rejection and committed role-list event.");
'@, $utf8)
    $project = Join-Path $probe 'Realtime.Client.csproj'
    Invoke-Tool 'dotnet' @('restore', $project, '--configfile', (Join-Path $runRoot 'NuGet.Config')) 'pure-api-client-restore' | Out-Null
    Invoke-Tool 'dotnet' @('build', $project, '-c', $Configuration, '--no-restore', '-nodeReuse:false') 'pure-api-client-build' | Out-Null
    $proof = Invoke-Tool 'dotnet' @((Join-Path $probe "bin/$Configuration/net10.0/Realtime.Client.dll")) 'pure-api-client-runtime' `
        -Variables @{ E2E_REALTIME_URL = $urls.billing; E2E_REALTIME_TOKEN = $token }
    Assert-Value 'pure-api-external-client-proof' $true ($proof.Output.Contains('PASS: pure API authenticated external client'))
}

function Test-S3 {
    $token = Get-MachineToken $plainClient "orders-api"
    $data = (Assert-Http "machine-endpoint-positive" 200 ($urls.orders + "/api/e2e/machine") -Token $token).Data
    Assert-Value "machine-client-id" "orders-only" $data.clientId
    Assert-Http "machine-natural-negative" 403 ($urls.orders + "/api/e2e/natural") -Token $token | Out-Null
    Assert-Http "machine-orders-only-billing" 401 ($urls.billing + "/api/e2e/machine") -Token $token | Out-Null
}

function Test-S4 {
    if (-not $hostToken) { $script:hostToken = Get-CodeToken $admin "openid profile e2e-idp-api orders-api billing-api" }
    $tenantToken = Get-TenantToken $sharedTenant
    $script:sharedSession = $tenantToken.Session
    $script:sharedToken = $tenantToken.Token
    Test-BrowserSession
    $claims = Read-TokenClaims $sharedToken
    $data = (Assert-Http "serviceclient-billing-delegation" 200 ($urls.orders + "/api/e2e/billing") -Token $sharedToken).Data
    Assert-Value "delegation-user" $claims.sub $data.userId
    Assert-Value "delegation-tenant" $sharedTenant.id $data.tenantId
    Assert-Value "delegation-client" "orders-api" $data.clientId
    # 另建真实用户，使用户头覆盖和租户头覆盖两个维度都能被证伪。
    $otherUser = (Assert-Http "create-header-forgery-user" 200 ($urls.idp + "/api/v1/users") -Method POST -Session $admin `
        -Body @{ username = "header_forgery_user"; email = "header@example.test"; password = (New-RandomValue "Usr!1")
            isActive = $true; isEmailVerified = $true }).Data
    Assert-Value "forged-user-differs-from-token" $true ($otherUser.id -ne $claims.sub)
    $spoof = @{ "X-User-Id" = $otherUser.id; "X-Tenant-Id" = $dedicatedTenant.id }
    $untrusted = Get-MachineToken $noDelegateClient "orders-api billing-api"
    foreach ($name in @("orders", "billing")) {
        $endpoint = $urls[$name] + "/api/e2e/natural"
        Assert-Http "no-delegate-$name" 403 $endpoint -Token $untrusted -Headers $spoof | Out-Null
        Assert-Http "anonymous-forged-header-$name" 401 $endpoint -Headers $spoof | Out-Null
        $data = (Assert-Http "human-forged-header-$name" 200 $endpoint -Token (Get-ServiceToken $sharedToken $name) -Headers $spoof).Data
        Assert-Value "human-header-cannot-change-user-$name" $claims.sub $data.userId
        Assert-Value "human-header-cannot-change-tenant-$name" $sharedTenant.id $data.tenantId
    }
    $fixture = (Assert-Http "signed-machine-tenant-fixture" 200 ($urls.idp + "/api/e2e/fixture-token?kind=machine-tenant") -Session $admin).Data
    Assert-Http "machine-header-cannot-create-user" 403 ($urls.billing + "/api/e2e/natural") -Token $fixture.access_token `
        -Headers @{ "X-User-Id" = (Read-TokenClaims $hostToken).sub } | Out-Null
    Test-Exchange $sharedToken $claims
}

function Test-S5 {
    if (-not $sharedToken) { $script:sharedToken = (Get-TenantToken $sharedTenant).Token }
    $dedicatedToken = (Get-TenantToken $dedicatedTenant).Token
    foreach ($target in @(@($sharedTenant, $sharedToken, "oidc_shared", "oidc_dedicated"),
            @($dedicatedTenant, $dedicatedToken, "oidc_dedicated", "oidc_shared"))) {
        $tenant, $token, $database, $other = $target
        $subject = (Read-TokenClaims $token).sub
        foreach ($name in @("orders", "billing")) {
            Assert-Http "project-$($tenant.name)-$name" 200 ($urls[$name] + "/api/e2e/natural") -Token (Get-ServiceToken $token $name) | Out-Null
            $query = 'SELECT count(*) FROM "e2e-{0}"."Users" WHERE "Id"=''{1}'' AND "TenantId"=''{2}'';' -f $name, $subject, $tenant.id
            Assert-Value "projection-target-$($tenant.name)-$name" "1" (Invoke-Sql $database $query)
            Assert-Value "projection-no-leak-$($tenant.name)-$name" "0" (Invoke-Sql $other $query)
        }
    }
    $update = @{}
    foreach ($key in @("displayName", "applicationType", "clientType", "redirectUris", "postLogoutRedirectUris", "permissions", "requirements", "sessionBound")) {
        $update[$key] = $exchangeClient[$key]
    }
    $update.permissions = @($exchangeClient.permissions | Where-Object { $_ -ne "scp:tenant-routing.read" })
    Assert-Http "remove-routing-scope" 200 ($urls.idp + "/api/v1/open-applications/" + $exchangeClient.id) -Method PUT -Body $update -Session $admin | Out-Null
    Stop-Api "orders"
    $offsets = Get-LogOffsets "orders"
    try {
        Start-Api "orders"
        Assert-Http "routing-denied-cold-cache" 502 ($urls.orders + "/api/v1/users") -Token $dedicatedToken | Out-Null
        $deadline = [DateTimeOffset]::UtcNow.AddSeconds(10)
        do {
            $diagnostic = (Read-ApiLog "orders" $offsets) -match '(?s)invalid_request.*ID2051'
            if ($diagnostic) { break }
            Start-Sleep -Milliseconds 200
        } while ([DateTimeOffset]::UtcNow -lt $deadline)
        Assert-Value "routing-error-diagnostic" $true $diagnostic
    }
    finally {
        $update.permissions = $exchangeClient.permissions
        Assert-Http "restore-routing-scope" 200 ($urls.idp + "/api/v1/open-applications/" + $exchangeClient.id) -Method PUT -Body $update -Session $admin | Out-Null
        Stop-Api "orders"
        Start-Api "orders"
    }
    Assert-Http "routing-restored" 200 ($urls.orders + "/api/e2e/natural") -Token $dedicatedToken | Out-Null
    Assert-Http "shared-tenant-unaffected" 200 ($urls.orders + "/api/e2e/natural") -Token $sharedToken | Out-Null
}

function Test-S6 {
    $valid = (Assert-Http "signed-control-fixture" 200 ($urls.idp + "/api/e2e/fixture-token?kind=valid") -Session $admin).Data.access_token
    $parts = $valid.Split('.')
    $signatureStart = if ($parts[2][0] -eq 'A') { 'B' } else { 'A' }
    $noneHeader = [Convert]::ToBase64String([Text.Encoding]::UTF8.GetBytes('{"alg":"none","typ":"at+jwt"}')).TrimEnd('=').Replace('+', '-').Replace('/', '_')
    $tokens = @{ signature = $parts[0] + "." + $parts[1] + "." + $signatureStart + $parts[2].Substring(1)
        none = $noneHeader + "." + $parts[1] + "." }
    foreach ($kind in @("issuer", "audience", "expired")) {
        $tokens[$kind] = (Assert-Http "signed-negative-$kind" 200 ($urls.idp + "/api/e2e/fixture-token?kind=$kind") -Session $admin).Data.access_token
    }
    foreach ($name in @("orders", "billing")) {
        Assert-Http "signed-control-accepted-$name" 200 ($urls[$name] + "/api/e2e/natural") -Token $valid | Out-Null
        foreach ($kind in $tokens.Keys) { Assert-Http "reject-$kind-$name" 401 ($urls[$name] + "/api/e2e/natural") -Token $tokens[$kind] | Out-Null }
    }
}

function Test-S7 {
    $tenantToken = (Get-TenantToken $sharedTenant).Token
    $password = New-RandomValue "Usr!1"
    $user = (Assert-Http "create-revocation-user" 200 ($urls.idp + "/api/v1/users") -Method POST -Session $admin `
        -Body @{ username = "revocation_user"; email = "revocation@example.test"; password = $password; isActive = $true; isEmailVerified = $true }).Data
    $session = Login "revocation_user" -Password $password
    $token = Get-CodeToken $session "openid profile e2e-idp-api orders-api billing-api"
    $idpToken = Get-CodeToken $session "openid profile e2e-idp-api" "idp-web"
    $tenantIdpSession = Login -Tenant $sharedTenant.name -Password $tenantPassword
    $tenantIdpToken = Get-CodeToken $tenantIdpSession "openid profile e2e-idp-api" "idp-web"
    Get-ServiceToken $token "billing" | Out-Null
    Get-ServiceToken $tenantToken "billing" | Out-Null
    $otherSession = Login "revocation_user" -Password $password
    Assert-Http "revoke-other-sessions" 200 ($urls.idp + "/api/v1/auth/me/sessions/revoke-others") -Method POST -Body @{} -Session $otherSession | Out-Null
    Assert-Http "revoked-cookie-immediate" 401 ($urls.idp + "/api/v1/auth/me") -Session $session | Out-Null
    Assert-Http "session-revocation-token-idp" 200 ($urls.idp + "/api/e2e/natural") -Token $idpToken | Out-Null
    Assert-Http "disable-user" 200 ($urls.idp + "/api/v1/users/" + $user.id + "/disable") -Method PATCH -Session $admin | Out-Null
    Assert-Http "disabled-user-idp-immediate" 401 ($urls.idp + "/api/e2e/natural") -Token $idpToken | Out-Null
    foreach ($name in @("orders", "billing")) {
        Assert-Http "disabled-user-resource-window-$name" 200 ($urls[$name] + "/api/e2e/natural") -Token (Get-ServiceToken $token $name) | Out-Null
    }
    try {
        Assert-Http "disable-shared-tenant" 200 ($urls.idp + "/api/v1/tenants/" + $sharedTenant.id + "/activation") -Method PUT -Body @{ isActive = $false } -Session $admin | Out-Null
        Assert-Http "disabled-tenant-idp-immediate" 401 ($urls.idp + "/api/e2e/natural") -Token $tenantIdpToken | Out-Null
        foreach ($name in @("orders", "billing")) {
            Assert-Http "disabled-tenant-resource-window-$name" 200 ($urls[$name] + "/api/e2e/natural") -Token (Get-ServiceToken $tenantToken $name) | Out-Null
        }
        if ($IncludeExpiryWait) {
            $expiry = [Math]::Max((Read-TokenClaims $token).exp, (Read-TokenClaims $tenantToken).exp) + 2
            Write-Host "S7 等待快速档访问令牌真实到期（OpenIddict ClockSkew=0）。"
            while ([DateTimeOffset]::UtcNow.ToUnixTimeSeconds() -lt $expiry) { Start-Sleep -Seconds 3 }
            foreach ($name in @("orders", "billing")) {
                Assert-Http "revoked-user-expired-$name" 401 ($urls[$name] + "/api/e2e/natural") -Token (Get-ServiceToken $token $name) | Out-Null
                Assert-Http "revoked-tenant-expired-$name" 401 ($urls[$name] + "/api/e2e/natural") -Token (Get-ServiceToken $tenantToken $name) | Out-Null
            }
        } else { Write-Skipped "S7-expiry-wait" "默认不等待令牌真实到期；使用 -IncludeExpiryWait 以快速档验证到期边界。" }
    }
    finally {
        Assert-Http "restore-shared-tenant" 200 ($urls.idp + "/api/v1/tenants/" + $sharedTenant.id + "/activation") -Method PUT -Body @{ isActive = $true } -Session $admin | Out-Null
    }
}

function Test-S8 {
    # 门禁取到元数据不等于 OpenIddict 验证器已经缓存公钥，先逐实例验一次真实令牌。
    $token = Get-CodeToken $admin "openid profile orders-api billing-api"
    foreach ($name in @("orders", "billing")) {
        Assert-Http "warm-validator-primed-$name" 200 ($urls[$name] + "/api/e2e/natural") -Token (Get-ServiceToken $token $name) | Out-Null
    }
    Stop-Api "idp"
    try {
        foreach ($name in @("orders", "billing")) {
            Assert-Http "warm-token-during-idp-outage-$name" 200 ($urls[$name] + "/api/e2e/natural") -Token (Get-ServiceToken $token $name) | Out-Null
            Assert-Http "warm-readiness-latched-$name" 200 ($urls[$name] + "/api/health/ready") | Out-Null
            Stop-Api $name
            $offsets = Get-LogOffsets $name
            Start-Api $name
            Wait-Http $name "/api/health/ready" 503
            Assert-Http "cold-readiness-unavailable-$name" 503 ($urls[$name] + "/api/health/ready") | Out-Null
            Assert-Http "cold-liveness-independent-$name" 200 ($urls[$name] + "/api/health/live") | Out-Null
            # 仅看冷重启后的警告段；启动成功的 INF 不能满足此断言。
            $deadline = [DateTimeOffset]::UtcNow.AddSeconds(10)
            do {
                $diagnostic = (Read-ApiLog $name $offsets) -match "is not usable yet"
                if ($diagnostic) { break }
                Start-Sleep -Milliseconds 200
            } while ([DateTimeOffset]::UtcNow -lt $deadline)
            Assert-Value "readiness-restart-warning-$name" $true $diagnostic @{ stdoutOffset = $offsets.Stdout; stderrOffset = $offsets.Stderr }
        }
    }
    finally { Start-Api "idp"; $script:admin = Login }
    foreach ($name in @("orders", "billing")) {
        Wait-Http $name "/api/health/ready"
        Assert-Http "readiness-recovered-$name" 200 ($urls[$name] + "/api/health/ready") | Out-Null
    }
}

function Test-S10 {
    $session = Login -Service "solo"
    Assert-Http "solo-cookie-positive" 200 ($urls.solo + "/api/e2e/natural") -Session $session | Out-Null
    $token = Get-CodeToken $admin "openid profile orders-api"
    Assert-Http "solo-bearer-negative" 401 ($urls.solo + "/api/e2e/natural") -Token $token | Out-Null
    foreach ($endpoint in @(@("/connect/authorize", "GET"), @("/connect/token", "POST"), @("/connect/userinfo", "GET"),
            @("/connect/logout", "GET"), @("/.well-known/openid-configuration", "GET"))) {
        $response = Invoke-Http ($urls.solo + $endpoint[0]) -Method $endpoint[1]
        Assert-Value "solo-no-$($endpoint[0])" $true ($response.Status -in @(404, 405)) @{ actualStatus = $response.Status }
    }
}

function Invoke-Scenario([string]$Name, [scriptblock]$Action) {
    Write-Host "Running $Name"
    try {
        & $Action
        $results.Add(@{ scenario = $Name; status = "pass"; evidence = $assertionPath })
    }
    catch {
        $results.Add(@{ scenario = $Name; status = "fail"; error = $_.Exception.Message; evidence = $assertionPath })
        Write-Host "${Name}：$($_.Exception.Message)" -ForegroundColor Red
    }
    Write-JsonFile (Join-Path $runRoot "results.json") $results.ToArray()
}

# 共用矩阵事实；浏览器辅助文件只被本入口加载，不另建场景生成清单。
. (Join-Path $PSScriptRoot "template-matrix-scenarios.ps1")
. (Join-Path $PSScriptRoot "docker-image.ps1")
if ($IncludeBrowserScenarios) { . (Join-Path $PSScriptRoot "test-template-oidc-browser.ps1") }
if ($IncludeMultiTenantScenarios) { . (Join-Path $PSScriptRoot "test-template-oidc-multitenant.ps1") }
$postgresPassword = New-RandomValue "Pg!1"
$adminPassword = New-RandomValue "Adm!1"
$tenantPassword = New-RandomValue "Tnt!1"
if ($IncludeBrowserScenarios) { Initialize-BrowserCertificate }
$http = New-HttpSession
try {
    Initialize-Environment
    if (-not $BrowserOnly) {
    Invoke-Scenario "postgresql-pruning" { Test-PostgresqlPruning }
    Invoke-Scenario "S2" { Test-S2 }
    if (-not $IncludeBrowserScenarios) { Invoke-Scenario 'pure-api-realtime' { Test-PureApiRealtime } }
    Invoke-Scenario "S3" { Test-S3 }
    Invoke-Scenario "S4" { Test-S4 }
    Invoke-Scenario "S5" { Test-S5 }
    Invoke-Scenario "S6" { Test-S6 }
    Invoke-Scenario "S8" { Test-S8 }
    Invoke-Scenario "S10" { Test-S10 }
    if ($IncludeExpiryWait) {
        Invoke-Scenario "exchange-expiry" {
            $expires = (Read-TokenClaims $delegatedExpiryToken).exp + 1
            while ([DateTimeOffset]::UtcNow.ToUnixTimeSeconds() -lt $expires) { Start-Sleep -Milliseconds 500 }
            Assert-Http "exchanged-jwt-expired" 401 ($urls.billing + "/api/e2e/natural") -Token $delegatedExpiryToken | Out-Null
        }
    } else { Write-Skipped "exchange-expiry" "默认不等待令牌真实到期；使用 -IncludeExpiryWait 验证交换令牌到期。" }
    Invoke-Scenario "S7" { Test-S7 }
    }
    if ($IncludeBrowserScenarios) { Invoke-BrowserScenarios }
    if ($IncludeMultiTenantScenarios) { Invoke-MultiTenantScenarios }
}
catch {
    $results.Add(@{ scenario = "setup"; status = "fail"; error = $_.Exception.Message })
    Write-Host $_.Exception.Message -ForegroundColor Red
}
finally {
    if ($IncludeBrowserScenarios) { Close-BrowserFixtures }
    foreach ($name in @($processes.Keys)) {
        try { Stop-Api $name }
        catch { $cleanupErrors.Add("进程 $name 清理失败：$($_.Exception.Message)") }
    }
    foreach ($client in $httpClients) { $client.Dispose() }
    if ($context) {
        if ($createdContainer -and (Invoke-Docker @("rm", "-fv", $container) -AllowFailure).ExitCode -ne 0) { $cleanupErrors.Add("容器清理失败：$container") }
        if ($createdVolume -and (Invoke-Docker @("volume", "rm", $volume) -AllowFailure).ExitCode -ne 0) { $cleanupErrors.Add("卷清理失败：$volume") }
        if ($context -eq "orbstack" -and -not $hadImage -and (Invoke-Docker @("image", "rm", $image) -AllowFailure).ExitCode -ne 0) { $cleanupErrors.Add("本轮新镜像清理失败：$image") }
    }
    Write-JsonFile (Join-Path $runRoot "cleanup.json") @{ errors = $cleanupErrors.ToArray(); processesStopped = ($processes.Count -eq 0)
        container = $container; volume = $volume; imagePreexisting = $hadImage }
    Write-JsonFile (Join-Path $runRoot "results.json") $results.ToArray()
}
if ($cleanupErrors.Count -gt 0 -or @($results | Where-Object { $_.status -eq "fail" }).Count -gt 0) {
    throw "OIDC E2E 失败，见 $runRoot/results.json 和 cleanup.json。"
}
Write-Host "OIDC E2E passed: $(@($results | ForEach-Object { $_.scenario }) -join ", ")." -ForegroundColor Green
