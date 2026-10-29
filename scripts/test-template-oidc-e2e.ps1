#!/usr/bin/env pwsh

<#
.SYNOPSIS
    从当前模板生成四个服务，以真实 PostgreSQL 和 OpenIddict 验证跨进程认证。
.DESCRIPTION
    运行 S2–S8、S10、S11；生成项目、隔离包源、日志和临时凭据位于 .tmp/oidc-e2e/<run>。
    依赖 PowerShell 7、.NET 10 SDK 和 Docker，不依赖 Python、OpenSSL 或宿主 psql。
    默认不等待令牌真实到期；-IncludeExpiryWait 以快速档（访问令牌 90 秒）执行撤销到期与交换令牌到期边界，CI 在 full 档传入。
    只放跨进程才能证明的链路，多个 Resource 共用的框架实现只验一个服务；协议规则的变体在集成测试里，
    浏览器登录、两步验证与确认身份由生成项目的 Playwright E2E 承担。
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
    [string]$CandidateSha
)

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

# 签发方是 http 地址，只能以 Development 运行（不要求 HTTPS 元数据、会话 Cookie 不带 __Host- 前缀）。
$serviceEnvironment = "Development"

function New-HttpSession([switch]$Cookies, [int]$TimeoutSeconds = 10) {
    $handler = [Net.Http.HttpClientHandler]::new()
    $handler.AllowAutoRedirect = $false
    $handler.UseCookies = $Cookies.IsPresent
    $client = [Net.Http.HttpClient]::new($handler)
    $client.Timeout = [TimeSpan]::FromSeconds($TimeoutSeconds)
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
    if ($Exchange) { $permissions += @("gt:urn:ietf:params:oauth:grant-type:token-exchange", "aud:billing-api", "aud:e2e-idp-api") }
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
    if ($Name -eq "orders") {
        $extraUsings = "using Leistd.ServiceClient.Refit;`nusing Leistd.ServiceClient.OAuth;"
        $registration += "`n" + 'services.AddServiceAuthentication();
services.AddRefitServiceClient<IBillingProbe, BillingOptions>("Billing").AddTokenExchange();
services.AddRefitServiceClient<IBillingMachineProbe, BillingMachineOptions>("BillingMachine").AddClientCredentials();'
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
    $sessionSettings = if ($Name -eq "orders") {
        ", refreshGraceSeconds = HttpContext.RequestServices.GetRequiredService<Microsoft.Extensions.Options.IOptions<E2E.$title.Api.Options.SessionCookieOptions>>().Value.RefreshGracePeriod.TotalSeconds, routeCacheSeconds = HttpContext.RequestServices.GetRequiredService<Microsoft.Extensions.Options.IOptions<Leistd.MultiTenancy.ConnectionStrings.TenantRouteCacheOptions>>().Value.CacheLifetime.TotalSeconds"
    } else { "" }
    $probe = $probeSource.Replace("__NAME__", $title).Replace("__MACHINE_PROBE__", $(if ($Role -ne "Standalone") { $machineProbeSource } else { "" })).Replace("__SESSION_SETTINGS__", $sessionSettings)
    [IO.File]::WriteAllText((Join-Path $ApiDirectory "E2EProbeController.cs"), $probe, $utf8)
    if ($Name -eq "idp") { [IO.File]::WriteAllText((Join-Path $ApiDirectory "FixtureTokenController.cs"), $signerSource, $utf8) }
    # 唯一模板文本锚点；重复或缺失都失败，不能静默漏装探针管道。
    $programPath = Join-Path $ApiDirectory "Program.cs"
    $content = [IO.File]::ReadAllText($programPath)
    $anchor = "var app = builder.Build();"
    if ([regex]::Matches($content, [regex]::Escape($anchor)).Count -ne 1) { throw "$Name 的 Program.cs 组合根锚点必须恰好出现一次。" }
    $injection = "E2E.$title.Api.E2EFixtureRegistration.AddE2EFixtures(builder.Services, builder.Configuration);`n    $anchor"
    [IO.File]::WriteAllText($programPath, $content.Replace($anchor, $injection), $utf8)
}

# 测试控制器仅写入 dotnet new 的生成目录，不进入模板或框架载荷。
$probeSource = @'
using Microsoft.AspNetCore.Authentication;
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
    public async Task<object> Natural([FromServices] ICurrentUser user, [FromServices] ICurrentTenant tenant,
        [FromServices] ICurrentClient client)
    {
        var authentication = await HttpContext.AuthenticateAsync();
        var expiresAt = DateTimeOffset.TryParse(authentication.Properties?.GetTokenValue("expires_at"), out var expires)
            ? expires.ToUnixTimeSeconds() : (long?)null;
        return new { userId = user.Id, tenantId = tenant.Id, clientId = client.ClientId, expiresAt __SESSION_SETTINGS__ };
    }
__MACHINE_PROBE__
}
'@

$machineProbeSource = @'
    [Authorize(Policy = "E2E.Machine")]
    [HttpGet("machine")]
    public object Machine([FromServices] ICurrentClient client) => new
    {
        clientId = client.ClientId,
        issuedAt = OpenIddict.Abstractions.OpenIddictExtensions.GetCreationDate(User)!.Value.ToUnixTimeSeconds(),
        expiresAt = OpenIddict.Abstractions.OpenIddictExtensions.GetExpirationDate(User)!.Value.ToUnixTimeSeconds()
    };
'@

$callerSource = @'
using Leistd.ServiceClient.Options;
using Leistd.ServiceClient.Refit;
using Leistd.ServiceClient.OAuth;
using Refit;
namespace E2E.Orders.Api;
public sealed class BillingOptions : ServiceClientOptions;
public sealed class BillingMachineOptions : ServiceClientOptions;
public interface IBillingProbe
{
    [Get("/api/e2e/natural")]
    Task<BillingIdentity> NaturalAsync();
}
public sealed record BillingIdentity(Guid? UserId, Guid? TenantId, string? ClientId);
public interface IBillingMachineProbe
{
    [Get("/api/e2e/machine")]
    Task<MachineIdentity> GetAsync();
}
public sealed record MachineIdentity(string ClientId, long IssuedAt, long ExpiresAt);
[Microsoft.AspNetCore.Mvc.ApiController]
[Microsoft.AspNetCore.Mvc.Route("api/e2e/billing-machine")]
[Microsoft.AspNetCore.Authorization.Authorize]
public sealed class BillingMachineProbeController(IBillingMachineProbe billing) : Microsoft.AspNetCore.Mvc.ControllerBase
{
    [Microsoft.AspNetCore.Mvc.HttpGet]
    public Task<MachineIdentity> Get() => billing.GetAsync();
}
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
    public object Get(string kind, [FromServices] ICurrentUser user)
    {
        using var cert = X509CertificateLoader.LoadPkcs12FromFile(config["OAuth:SigningCertificates:0:Path"]!, "");
        using var rsa = cert.GetRSAPrivateKey()!;
        var key = new RsaSecurityKey(rsa) { KeyId = cert.Thumbprint,
            CryptoProviderFactory = new CryptoProviderFactory { CacheSignatureProviders = false } };
        var now = DateTime.UtcNow;
        var claims = new Dictionary<string, object> { ["sub"] = user.Id!.Value.ToString(),
            ["aud"] = new[] { "orders-api", "billing-api" } };
        if (kind == "machine-tenant")
        {
            claims["sub"] = "client:orders-machine";
            claims["client_id"] = "orders-machine";
            claims["scope"] = "orders-api billing-api";
            claims[types.Value.TenantId] = Guid.NewGuid().ToString();
        }
        claims[types.Value.UserIds[0]] = claims["sub"];
        var descriptor = new SecurityTokenDescriptor {
            Issuer = config["OAuth:Issuer"],
            Claims = claims, TokenType = "at+jwt", IssuedAt = now.AddMinutes(-20), NotBefore = now.AddMinutes(-20),
            Expires = kind == "short" ? now.AddSeconds(25) : now.AddMinutes(10),
            SigningCredentials = new SigningCredentials(key, SecurityAlgorithms.RsaSha256) };
        return new { access_token = new JsonWebTokenHandler().CreateToken(descriptor) };
    }
}
'@

function Initialize-Environment {
    Write-Host "Artifacts: $runRoot"
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
        Invoke-Tool "pwsh" @("-NoProfile", "-File", (Join-Path $repoRoot "framework/build/pack-local-feed.ps1"), "-Configuration", $Configuration, "-OutputPath", $feed) "pack" | Out-Null
    }
    if (-not (Get-ChildItem -LiteralPath $feed -Filter "Leistd.*.nupkg")) { throw "本地包源为空：$feed。" }
    $script:candidateFeed = Read-CandidateManifest $feed
    Set-CandidateVersionOverride (Join-Path $runRoot "generated") $candidateFeed.CandidateVersion
    $hive = Join-Path $runRoot "template-hive"
    $packages = Join-Path $runRoot "nuget-cache"
    # 环境变量优先于 NuGet.Config：执行器的共享解包目录不能覆盖本轮候选包隔离。
    $baseEnvironment.NUGET_PACKAGES = $packages
    $nugetConfig = Join-Path $runRoot "NuGet.Config"
    $escapedFeed = [Security.SecurityElement]::Escape($feed)
    $escapedPackages = [Security.SecurityElement]::Escape($packages)
    [IO.File]::WriteAllText($nugetConfig, @"
<configuration><config><add key="globalPackagesFolder" value="$escapedPackages" /></config>
<packageSources><clear/><add key="local" value="$escapedFeed" />
<add key="nuget.org" value="https://api.nuget.org/v3/index.json" /></packageSources>
<packageSourceMapping><packageSource key="local"><package pattern="Leistd.*" /></packageSource>
<packageSource key="nuget.org"><package pattern="*" /></packageSource></packageSourceMapping></configuration>
"@, $utf8)
    Invoke-Tool "dotnet" @("new", "--debug:custom-hive", $hive, "install", (Join-Path $repoRoot "template"), "--force") "install" | Out-Null
    foreach ($purpose in @("signing", "encryption")) { New-TestCertificate $purpose }
    foreach ($name in @("postgres", "idp", "orders", "billing", "solo")) {
        $ports[$name] = Get-FreeTcpPort
        $urls[$name] = "http://127.0.0.1:$($ports[$name])"
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
            "idp" { "identity-notifications" }
            "solo" { "standalone" }
            default { "resource" }
        }
        $arguments = @("new", "--debug:custom-hive", $hive, "fullstack-app", "-n", "E2E.$title", "-o", $generated, "--force") + $scenarioMap[$scenario].Arguments
        if ($name -eq 'billing') {
            $arguments += @('--include-frontend', 'false', '--include-real-time', 'true')
        }
        Invoke-Tool "dotnet" $arguments "generate-$name" | Out-Null
        $apiDirectory = Join-Path $generated "backend/src/E2E.$title.Api"
        Add-FixtureRegistration $name $role $apiDirectory
        $solution = Join-Path $generated "backend/E2E.$title.sln"
        Invoke-Tool "dotnet" @("restore", $solution, "--configfile", $nugetConfig, "--force") "restore-$name" | Out-Null
        Assert-CandidatePackages $packages $candidateFeed
        Invoke-Tool "dotnet" @("build", $solution, "-c", $Configuration, "--no-restore", "-p:UseSharedCompilation=false", "-nodeReuse:false") "build-$name" | Out-Null
        $environment = @{
            ASPNETCORE_ENVIRONMENT = $serviceEnvironment; ASPNETCORE_URLS = $urls[$name]
            ConnectionStrings__Redis = ""; DataProtection__KeysPath = (Join-Path $runRoot "keys-$name")
            DefaultAdmin__Username = "admin"; DefaultAdmin__Password = $adminPassword
            Leistd__Security__VerificationCodes__Key = [Convert]::ToBase64String([Security.Cryptography.RandomNumberGenerator]::GetBytes(32))
        }
        if ($name -eq "idp" -and $IncludeExpiryWait) {
            Use-QuickAccessTokens $environment
            $environment.OAuth__ClientCredentialsAccessTokenLifetime = "00:06:00"
        }
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
            # 机器身份回源租户路由与按用户读账户偏好；代表本人写偏好经 Token Exchange 换 Identity 自身 API 的令牌
            $environment.Leistd__ServiceClients__Identity__Scope = "tenant-routing.read account-preferences.read"
            $environment.Leistd__ServiceClients__Identity__TokenExchange__Audience = "e2e-idp-api"
            $environment.Leistd__ServiceClients__Identity__TokenExchange__Scope = "e2e-idp-api"
            $environment.Leistd__ServiceAuth__Authority = $urls.idp
            $environment.Leistd__MultiTenancy__Routing__CacheLifetime = "00:00:01"
            if ($IncludeExpiryWait) { $environment.SessionCookie__RefreshGracePeriod = "00:01:00" }
        }
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
    $script:exchangeClient = New-Application "orders-api" @("billing-api", "e2e-idp-api", "tenant-routing.read", "account-preferences.read") -Exchange
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
        $environment.Leistd__ServiceClients__BillingMachine__BaseAddress = $urls.billing
        $environment.Leistd__ServiceClients__BillingMachine__Scope = "billing-api"
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
    Assert-Value "exchange-actor" "client:orders-api" $claims.act.sub
    Assert-Value "exchange-audience-only-target" @("billing-api") @($claims.aud)
    Assert-Http "target-token-cannot-call-orders" 401 ($urls.orders + "/api/e2e/natural") -Token $output | Out-Null
    # 拒绝路径：没有交换权限的机器客户端拿同一源令牌换不到目标令牌
    Exchange-Token $Subject @{ client_id = $plainClient.clientId; client_secret = $plainClient.clientSecret } 400 | Out-Null
    # 源令牌比 120 秒上限先到期：交换令牌取源令牌的到期时刻（full 档的 exchange-expiry 等它真实到期）
    $short = (Assert-Http "short-subject-fixture" 200 ($urls.idp + "/api/e2e/fixture-token?kind=short") -Session $admin).Data.access_token
    $shortOutput = Exchange-Token $short
    Assert-Value "final-jwt-subject-exp-cap" (Read-TokenClaims $short).exp (Read-TokenClaims $shortOutput).exp
    $script:delegatedExpiryToken = $shortOutput
}

function New-ResourceBrowserCookie {
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
    $cookie = ($callback.Cookies | Where-Object { $_.StartsWith("E2E.Orders.Auth=") } | ForEach-Object { $_.Split(';')[0] }) -join "; "
    if (-not $cookie) { throw "OIDC callback created no reference cookie." }
    return $cookie
}

function Test-BrowserSession {
    $cookie = New-ResourceBrowserCookie
    $me = (Assert-Http "resource-browser-me" 200 ($urls.orders + "/api/v1/auth/me") -Headers @{ Cookie = $cookie }).Data
    $claims = Read-TokenClaims $sharedToken
    Assert-Value "browser-session-user" $claims.sub $me.id
    Assert-Value "browser-session-tenant" $sharedTenant.id $me.tenantId
    Assert-Http "browser-cookie-delegation" 200 ($urls.orders + "/api/e2e/billing") -Headers @{ Cookie = $cookie } | Out-Null
    Assert-Http "browser-no-bearer-fallback" 401 ($urls.orders + "/api/v1/auth/me") -Token "invalid" -Headers @{ Cookie = $cookie } | Out-Null
    $logout = Assert-Http "resource-browser-logout" 200 ($urls.orders + "/api/v1/auth/logout") -Method POST -Body @{} -Headers @{ Cookie = $cookie }
    $logoutParameters = Read-FormPost $logout ($urls.idp + "/connect/logout")
    # hint 只在表单正文里：Identity 据其中的会话标识免确认退出（判定见 OidcLogoutTests）
    Assert-Value "logout-hint-in-form-body" $true (-not [string]::IsNullOrEmpty($logoutParameters["id_token_hint"]))
    Assert-Value "logout-hint-session" $true ((Read-TokenClaims $logoutParameters["id_token_hint"]).ContainsKey("sid"))
    Assert-Http "browser-logout-invalidates-copied-cookie" 401 ($urls.orders + "/api/v1/auth/me") -Headers @{ Cookie = $cookie } | Out-Null
}

function Test-S2 {
    $script:hostToken = Get-CodeToken $admin "openid profile email roles e2e-idp-api orders-api billing-api"
    $claims = Read-TokenClaims $hostToken
    Assert-Value "scope-derived-audiences" @("orders-api") @($claims.aud | Sort-Object)
    $data = (Assert-Http "human-token-orders" 200 ($urls.orders + "/api/e2e/natural") -Token $hostToken).Data
    Assert-Value "human-sub-orders" $claims.sub $data.userId
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
// Subscribe 以 bool 表示是否加入了资源组：越权是返回 false 的正常结果，技术故障仍以异常结束调用
if (!await connection.InvokeAsync<bool>("Subscribe", "host:roles"))
{
    throw new InvalidOperationException("The authorized resource subscription was rejected.");
}
foreach (var forbidden in new[] { Guid.NewGuid().ToString("N") + ":roles", "host:secrets", "roles" })
{
    if (await connection.InvokeAsync<bool>("Subscribe", forbidden))
    {
        throw new InvalidOperationException("An unauthorized resource subscription was accepted.");
    }
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
    # Resource 按令牌里的租户经 Identity 路由到对应库；orders 与 billing 共用同一份路由实现，只验 orders
    foreach ($target in @(@($sharedTenant, $sharedToken, "oidc_shared", "oidc_dedicated"),
            @($dedicatedTenant, $dedicatedToken, "oidc_dedicated", "oidc_shared"))) {
        $tenant, $token, $database, $other = $target
        $subject = (Read-TokenClaims $token).sub
        Assert-Http "project-$($tenant.name)-orders" 200 ($urls.orders + "/api/e2e/natural") -Token $token | Out-Null
        $query = 'SELECT count(*) FROM "e2e-orders"."Users" WHERE "Id"=''{0}'' AND "TenantId"=''{1}'';' -f $subject, $tenant.id
        Assert-Value "projection-target-$($tenant.name)-orders" "1" (Invoke-Sql $database $query)
        Assert-Value "projection-no-leak-$($tenant.name)-orders" "0" (Invoke-Sql $other $query)
    }
}

function Test-S6 {
    $valid = (Assert-Http "signed-control-fixture" 200 ($urls.idp + "/api/e2e/fixture-token?kind=valid") -Session $admin).Data.access_token
    $parts = $valid.Split('.')
    $signatureStart = if ($parts[2][0] -eq 'A') { 'B' } else { 'A' }
    $tampered = $parts[0] + "." + $parts[1] + "." + $signatureStart + $parts[2].Substring(1)
    Assert-Http "signed-control-accepted-orders" 200 ($urls.orders + "/api/e2e/natural") -Token $valid | Out-Null
    Assert-Http "reject-signature-orders" 401 ($urls.orders + "/api/e2e/natural") -Token $tampered | Out-Null
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
    # 门禁取到元数据不等于 OpenIddict 验证器已经缓存公钥，先验一次真实令牌。
    # 就绪门禁是框架同一份实现，只验 orders。
    $token = Get-CodeToken $admin "openid profile orders-api"
    Assert-Http "warm-validator-primed-orders" 200 ($urls.orders + "/api/e2e/natural") -Token $token | Out-Null
    Stop-Api "idp"
    try {
        Assert-Http "warm-token-during-idp-outage-orders" 200 ($urls.orders + "/api/e2e/natural") -Token $token | Out-Null
        Assert-Http "warm-readiness-latched-orders" 200 ($urls.orders + "/api/health/ready") | Out-Null
        Stop-Api "orders"
        $offsets = Get-LogOffsets "orders"
        Start-Api "orders"
        Wait-Http "orders" "/api/health/ready" 503
        Assert-Http "cold-readiness-unavailable-orders" 503 ($urls.orders + "/api/health/ready") | Out-Null
        Assert-Http "cold-liveness-independent-orders" 200 ($urls.orders + "/api/health/live") | Out-Null
        # 仅看冷重启后的警告段；启动成功的 INF 不能满足此断言。
        $deadline = [DateTimeOffset]::UtcNow.AddSeconds(10)
        do {
            $diagnostic = (Read-ApiLog "orders" $offsets) -match "is not usable yet"
            if ($diagnostic) { break }
            Start-Sleep -Milliseconds 200
        } while ([DateTimeOffset]::UtcNow -lt $deadline)
        Assert-Value "readiness-restart-warning-orders" $true $diagnostic @{ stdoutOffset = $offsets.Stdout; stderrOffset = $offsets.Stderr }
    }
    finally { Start-Api "idp"; $script:admin = Login }
    Wait-Http "orders" "/api/health/ready"
    Assert-Http "readiness-recovered-orders" 200 ($urls.orders + "/api/health/ready") | Out-Null
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

# S11 账户偏好跨进程：资源服务的 /me 以机器身份按租户回源 Identity（共享库与独立库两个租户），
# 资源服务的 PATCH 以 Token Exchange 代表本人转交 Identity，Identity 的 /me 随之变化
function Test-S11 {
    foreach ($case in @(@{ Tenant = $sharedTenant; TimeZone = "Asia/Tokyo"; Theme = "dark" },
            @{ Tenant = $dedicatedTenant; TimeZone = "Europe/Paris"; Theme = "light" })) {
        $name = $case.Tenant.name
        # 重新登录：S4 的浏览器会话用例经 /connect/logout 退出了共享租户的 Identity 会话
        $user = Get-TenantToken $case.Tenant
        Assert-Http "idp-preference-write-$name" 200 ($urls.idp + "/api/v1/auth/me/preferences") -Method PATCH `
            -Body @{ timeZone = $case.TimeZone } -Session $user.Session | Out-Null
        $me = (Assert-Http "resource-me-preferences-$name" 200 ($urls.orders + "/api/v1/auth/me") -Token $user.Token).Data
        Assert-Value "resource-me-reads-identity-$name" $case.TimeZone $me.preferences.timeZone
        $written = (Assert-Http "resource-preference-forward-$name" 200 ($urls.orders + "/api/v1/auth/me/preferences") -Method PATCH `
            -Body @{ theme = $case.Theme } -Token $user.Token).Data
        Assert-Value "resource-forward-representation-$name" $case.Theme $written.preferences.theme
        $identityMe = (Assert-Http "idp-me-after-forward-$name" 200 ($urls.idp + "/api/v1/auth/me") -Session $user.Session).Data
        Assert-Value "identity-holds-forwarded-write-$name" $case.Theme $identityMe.preferenceOverrides.theme
    }
    # 本地能判定的非法值在转交前就拒绝，错误码与 Identity 一致
    $rejected = Assert-Http "resource-preference-invalid-value" 400 ($urls.orders + "/api/v1/auth/me/preferences") -Method PATCH `
        -Body @{ timeZone = "Mars/Olympus" } -Token $user.Token
    Assert-Value "resource-preference-invalid-code" "AppSetting:TimeZoneInvalid" $rejected.Data.code
}

# S12 资源服务迁移后补齐租户 Admin：用与 compose 相同 scope 的独立迁移身份跑全目标模式，
# 按正式路由进入共享库与独立库租户；已删除租户残留的 Admin 角色计入跳过，作业照常成功
function Test-S12 {
    $doomed = New-Tenant "doomed-e2e"
    foreach ($tenant in @($sharedTenant, $dedicatedTenant, $doomed)) {
        $subject = (Read-TokenClaims (Get-TenantToken $tenant).Token).sub
        Invoke-Tool "dotnet" @($services.orders.Migrator, "--grant-admin", $subject, "--tenant", $tenant.id, "--apply") `
            "orders-tenant-admin-$($tenant.name)" -Variables $services.orders.Environment | Out-Null
    }
    Assert-Http "delete-tenant-$($doomed.name)" 204 ($urls.idp + "/api/v1/tenants/$($doomed.id)") -Method DELETE -Session $admin | Out-Null
    # scope 取自生成项目的部署配置，部署配置退回旧值时本场景随之失败；开发配置须与之一致
    $compose = Get-Content -LiteralPath (Join-Path $services.orders.Root "deploy/docker-compose.yml") -Raw
    $section = [regex]::Match($compose, '(?ms)^  db-migrator:.*?(?=^  \S)').Value
    $scope = [regex]::Match($section, 'Leistd__ServiceClients__Identity__Scope=(.+)').Groups[1].Value.Trim()
    $development = Get-Content -LiteralPath (Join-Path $services.orders.Root "backend/src/E2E.Orders.DbMigrator/appsettings.Development.json") -Raw
    $developmentScope = [regex]::Match($development, '"Scope":\s*"([^"]+)"').Groups[1].Value
    Assert-Value "migrator-scope-dev-matches-deploy" (@($scope.Split(" ") | Sort-Object) -join " ") (@($developmentScope.Split(" ") | Sort-Object) -join " ")
    $migrator = New-Application "orders-migrator" $scope.Split(" ")
    $environment = $services.orders.Environment.Clone()
    $environment.Leistd__ServiceAuth__ClientId = $migrator.clientId
    $environment.Leistd__ServiceAuth__ClientSecret = $migrator.clientSecret
    $environment.Leistd__ServiceClients__Identity__Scope = $scope
    # 输出只有租户标识、权限名与连接指纹，不含连接串；失败时原文在日志文件里
    $result = Invoke-Tool "dotnet" @($services.orders.Migrator, "--apply") "orders-migrate-topup" -Variables $environment -AllowFailure
    Assert-Value "resource-topup-exit" 0 $result.ExitCode
    Assert-Value "resource-topup-no-failure" $false ($result.Output -match "FAILED")
    Assert-Value "resource-topup-skips-deleted" $true $result.Output.Contains("Skipped 1 deleted or missing tenant(s): $($doomed.id)")
    $rerun = Invoke-Tool "dotnet" @($services.orders.Migrator, "--apply") "orders-migrate-topup-rerun" -Variables $environment
    Assert-Value "resource-topup-rerun-complete" $true $rerun.Output.Contains("already hold every available permission")
}

# 只在 full 的真实到期快速档中等待；故障矩阵、节流和边界仍由组件/模板集成测试承担。
function Test-S13 {
    # 各请求在自己的异步操作里计时，不能用依次收集响应的时刻冒充独立耗时。
    Add-Type -TypeDefinition @'
using System.Diagnostics;
using System.Net.Http;
using System.Threading.Tasks;
public static class E2EOutageTiming
{
    public static async Task<(int Status, long Milliseconds)> SendAsync(HttpClient client, string url, string cookie)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.TryAddWithoutValidation("Cookie", cookie);
        var watch = Stopwatch.StartNew();
        using var response = await client.SendAsync(request);
        return ((int)response.StatusCode, watch.ElapsedMilliseconds);
    }
}
'@
    $client = New-HttpSession -TimeoutSeconds 90
    $script:sharedSession = Login -Tenant $sharedTenant.name -Password $tenantPassword
    $cookie = New-ResourceBrowserCookie
    $warm = (Assert-Http "outage-machine-warm" 200 ($urls.orders + "/api/e2e/billing-machine") -Headers @{ Cookie = $cookie } -Session $client).Data
    Assert-Value "outage-machine-configured-lifetime" 360 ([long]$warm.expiresAt - [long]$warm.issuedAt)
    $renewAt = [long]$warm.issuedAt + 180
    while ([DateTimeOffset]::UtcNow.ToUnixTimeSeconds() -lt $renewAt) { Start-Sleep -Milliseconds 500 }
    # 新登录的访问令牌 90 秒，宽限 60 秒；机器令牌则刚进入提前续取窗口。
    $cookie = New-ResourceBrowserCookie
    $initial = (Assert-Http "outage-route-warm" 200 ($urls.orders + "/api/e2e/natural") -Headers @{ Cookie = $cookie }).Data
    Assert-Value "outage-session-configured-grace" 60 $initial.refreshGraceSeconds
    Assert-Value "outage-route-configured-lifetime" 1 $initial.routeCacheSeconds
    $routeExpires = [DateTimeOffset]::UtcNow.AddSeconds(2)
    $offsets = Get-LogOffsets "orders"
    Stop-Api "idp"
    try {
        while ([DateTimeOffset]::UtcNow -lt $routeExpires) { Start-Sleep -Milliseconds 100 }
        foreach ($wave in @(@{ Name = "first"; Limit = 3000 }, @{ Name = "throttled"; Limit = 500 })) {
            $pending = @(1..3 | ForEach-Object { [E2EOutageTiming]::SendAsync($client, $urls.orders + "/api/e2e/natural", $cookie) })
            for ($i = 0; $i -lt $pending.Count; $i++) {
                $timing = $pending[$i].GetAwaiter().GetResult()
                Assert-Value "outage-route-$($wave.Name)-$i-status" 200 $timing.Item1
                Assert-Value "outage-route-$($wave.Name)-$i-latency" $true ($timing.Item2 -le $wave.Limit) @{
                    elapsedMs = $timing.Item2; limitMs = $wave.Limit
                }
            }
        }
        # Orders 路由已节流；Billing 接收宿主机器身份，不触发租户路由，剩一个令牌续取预算加调度余量。
        $machineWatch = [Diagnostics.Stopwatch]::StartNew()
        Assert-Http "outage-machine-fallback" 200 ($urls.orders + "/api/e2e/billing-machine") -Headers @{ Cookie = $cookie } -Session $client | Out-Null
        $machineWatch.Stop()
        Assert-Value "outage-machine-latency" $true ($machineWatch.ElapsedMilliseconds -le 3000) @{
            elapsedMs = $machineWatch.ElapsedMilliseconds; limitMs = 3000
        }
        $log = Read-ApiLog "orders" $offsets
        Assert-Value "outage-route-fallback-observed" $true ($log -match "using state stale")
        Assert-Value "outage-token-fallback-observed" $true ($log -match "using a token stale")
        $delegated = Invoke-Http ($urls.orders + "/api/e2e/billing") -Headers @{ Cookie = $cookie } -Session $client
        Assert-Value "outage-delegation-dependency-failure" $true ($delegated.Status -in @(502, 503, 504))
        while ([DateTimeOffset]::UtcNow.ToUnixTimeSeconds() -lt ([long]$initial.expiresAt + 2)) { Start-Sleep -Milliseconds 500 }
        Assert-Http "outage-delegation-keeps-session" 200 ($urls.orders + "/api/e2e/natural") -Headers @{ Cookie = $cookie } -Session $client | Out-Null
    }
    finally { Start-Api "idp"; Wait-Http "idp"; $script:admin = Login }
    # 不重新登录 Resource：原 Cookie 必须在恢复后完成续期。
    $deadline = [DateTimeOffset]::UtcNow.AddSeconds(120)
    do {
        $renewed = (Assert-Http "outage-recovery-session" 200 ($urls.orders + "/api/e2e/natural") -Headers @{ Cookie = $cookie } -Session $client).Data
        if ([long]$renewed.expiresAt -gt [long]$initial.expiresAt) { break }
        Start-Sleep -Milliseconds 500
    } while ([DateTimeOffset]::UtcNow -lt $deadline)
    Assert-Value "outage-recovery-actually-renewed" $true ([long]$renewed.expiresAt -gt [long]$initial.expiresAt)
    Assert-Http "outage-recovery-delegation" 200 ($urls.orders + "/api/e2e/billing") -Headers @{ Cookie = $cookie } -Session $client | Out-Null
    # 独立会话验证耗尽；测试 Identity 使用内存票据缓存，重启后先重建它的登录态。
    $script:sharedSession = Login -Tenant $sharedTenant.name -Password $tenantPassword
    $cookie = New-ResourceBrowserCookie
    $last = (Assert-Http "outage-expiry-session-primed" 200 ($urls.orders + "/api/e2e/natural") -Headers @{ Cookie = $cookie }).Data
    $expires = [DateTimeOffset]::FromUnixTimeSeconds([long]$last.expiresAt + 61)
    Stop-Api "idp"
    try {
        while ([DateTimeOffset]::UtcNow -lt $expires) { Start-Sleep -Milliseconds 500 }
        Assert-Http "outage-grace-exhausted" 401 ($urls.orders + "/api/e2e/natural") -Headers @{ Cookie = $cookie } -Session $client | Out-Null
    }
    finally { Start-Api "idp"; Wait-Http "idp"; $script:admin = Login }
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

# 共用矩阵事实，不另建场景生成清单。
. (Join-Path $PSScriptRoot "template-matrix-scenarios.ps1")
. (Join-Path $PSScriptRoot "docker-image.ps1")
. (Join-Path $repoRoot "framework/build/candidate-feed.ps1")
$postgresPassword = New-RandomValue "Pg!1"
$adminPassword = New-RandomValue "Adm!1"
$tenantPassword = New-RandomValue "Tnt!1"
$http = New-HttpSession
try {
    Initialize-Environment
    Invoke-Scenario "S2" { Test-S2 }
    Invoke-Scenario 'pure-api-realtime' { Test-PureApiRealtime }
    Invoke-Scenario "S3" { Test-S3 }
    Invoke-Scenario "S4" { Test-S4 }
    Invoke-Scenario "S5" { Test-S5 }
    Invoke-Scenario "S6" { Test-S6 }
    Invoke-Scenario "S8" { Test-S8 }
    Invoke-Scenario "S10" { Test-S10 }
    Invoke-Scenario "S11" { Test-S11 }
    Invoke-Scenario "S12" { Test-S12 }
    if ($IncludeExpiryWait) { Invoke-Scenario "S13" { Test-S13 } }
    else { Write-Skipped "S13" "故障恢复与截止的真实到期仅在 -IncludeExpiryWait 快速档验证。" }
    if ($IncludeExpiryWait) {
        Invoke-Scenario "exchange-expiry" {
            $expires = (Read-TokenClaims $delegatedExpiryToken).exp + 1
            while ([DateTimeOffset]::UtcNow.ToUnixTimeSeconds() -lt $expires) { Start-Sleep -Milliseconds 500 }
            Assert-Http "exchanged-jwt-expired" 401 ($urls.billing + "/api/e2e/natural") -Token $delegatedExpiryToken | Out-Null
        }
    } else { Write-Skipped "exchange-expiry" "默认不等待令牌真实到期；使用 -IncludeExpiryWait 验证交换令牌到期。" }
    Invoke-Scenario "S7" { Test-S7 }
}
catch {
    $results.Add(@{ scenario = "setup"; status = "fail"; error = $_.Exception.Message; stack = $_.ScriptStackTrace })
    Write-Host $_.Exception.Message -ForegroundColor Red
}
finally {
    foreach ($name in @($processes.Keys)) {
        try { Stop-Api $name }
        catch { $cleanupErrors.Add("进程 $name 清理失败：$($_.Exception.Message)") }
    }
    foreach ($client in $httpClients) { $client.Dispose() }
    if ($context) {
        if ($createdContainer -and (Invoke-Docker @("rm", "-f", "-v", $container) -AllowFailure).ExitCode -ne 0) { $cleanupErrors.Add("容器清理失败：$container") }
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
# 只在全部场景与清理成功后写证明；断言数取 assertions.jsonl 中通过的条目，汇总要求大于 0
$passedAssertions = @(if (Test-Path -LiteralPath $assertionPath) {
    Get-Content -LiteralPath $assertionPath | Where-Object { $_ } | ForEach-Object { $_ | ConvertFrom-Json } |
        Where-Object { $_.PSObject.Properties['passed'] -and $_.passed -eq $true }
}).Count
Write-JsonFile (Join-Path $runRoot "e2e-proof.json") ([ordered]@{
    Version = 1; CandidateSha = $(if ($CandidateSha) { $CandidateSha } else { git -C $repoRoot rev-parse HEAD }); Result = "pass"; Assertions = $passedAssertions })
Write-Host "OIDC E2E passed: $(@($results | ForEach-Object { $_.scenario }) -join ", ")." -ForegroundColor Green
