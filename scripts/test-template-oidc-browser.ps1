# 仅由 test-template-oidc-e2e.ps1 加载。夹具仅写入 .tmp 的生成宿主。
$browserFixtureSecret = New-RandomValue 'Browser!1'
$browserCertificateThumbprint = ''
$browserSession = $null
$browserStarted = $false
$browserVault = $null
$browserVaultCreated = $false
$browserCommand = 0
$expectedBrowserAuthorizes = @{}
$browserEvidence = Join-Path $runRoot 'browser'
New-Item -ItemType Directory -Force $browserEvidence | Out-Null

function Initialize-BrowserCertificate {
    $rsa = [Security.Cryptography.RSA]::Create(2048)
    try {
        $request = [Security.Cryptography.X509Certificates.CertificateRequest]::new('CN=localhost', $rsa,
            [Security.Cryptography.HashAlgorithmName]::SHA256, [Security.Cryptography.RSASignaturePadding]::Pkcs1)
        $san = [Security.Cryptography.X509Certificates.SubjectAlternativeNameBuilder]::new()
        $san.AddDnsName('localhost'); $san.AddIpAddress([Net.IPAddress]::Loopback)
        $request.CertificateExtensions.Add($san.Build())
        $certificate = $request.CreateSelfSigned([DateTimeOffset]::UtcNow.AddMinutes(-5), [DateTimeOffset]::UtcNow.AddDays(1))
        try {
            $script:browserCertificateThumbprint = $certificate.Thumbprint
            [IO.File]::WriteAllBytes((Join-Path $runRoot 'browser-tls.pfx'), $certificate.Export([Security.Cryptography.X509Certificates.X509ContentType]::Pfx, ''))
        } finally { $certificate.Dispose() }
    } finally { $rsa.Dispose() }
}

# .NET 回调而非 PowerShell 回调：后台 HttpClient 线程没有 PowerShell runspace。
Add-Type -TypeDefinition @'
using System.Net.Http;
public static class OidcBrowserTls
{
    public static HttpClientHandler CreateHandler(string thumbprint) => new HttpClientHandler {
        ServerCertificateCustomValidationCallback = (request, certificate, chain, errors) => certificate?.Thumbprint == thumbprint
    };
}
'@

function Build-BrowserFrontend([string]$Name, [string]$Generated, [string]$ApiDirectory) {
    $frontend = Join-Path $Generated 'frontend'
    Invoke-Tool 'npm' @('ci', '--no-audit', '--no-fund') "browser-npm-$Name" -WorkingDirectory $frontend | Out-Null
    $output = Join-Path $runRoot "frontend-$Name"
    Invoke-Tool 'npm' @('exec', '--', 'ng', 'build', '--configuration', 'production', '--output-path', $output) "browser-build-$Name" -WorkingDirectory $frontend | Out-Null
    $webroot = Join-Path $ApiDirectory "bin/$Configuration/net10.0/wwwroot"
    New-Item -ItemType Directory -Force $webroot | Out-Null
    Copy-Item (Join-Path $output 'browser/*') $webroot -Recurse -Force
}

function Add-BrowserFixture([string]$Name, [string]$ApiDirectory) {
    $title = [Globalization.CultureInfo]::InvariantCulture.TextInfo.ToTitleCase($Name)
    $registration = @'
using System.Net.Http;
using Microsoft.Extensions.Http;
using Microsoft.Extensions.Options;
namespace E2E.__NAME__.Api;
internal static class BrowserFixtureRegistration
{
    internal static HttpClientHandler Tls(IConfiguration config) => new() {
        ServerCertificateCustomValidationCallback = (request, cert, chain, errors) => cert?.Thumbprint == config["E2E:CertificateThumbprint"]
    };
    public static void Add(IServiceCollection services, IConfiguration config)
    {
        services.PostConfigureAll<HttpClientFactoryOptions>(options => options.HttpMessageHandlerBuilderActions.Add(builder => builder.PrimaryHandler = Tls(config)));
        services.Configure<Microsoft.AspNetCore.Authentication.Cookies.CookieAuthenticationOptions>(E2E.__NAME__.Application.Shared.AuthenticationSchemeNames.SessionCookie, options => {
            var validate = options.Events.OnValidatePrincipal;
            options.Events.OnValidatePrincipal = context => context.Request.Path == "/api/e2e/browser-ticket" && context.Request.Headers["X-E2E-Secret"] == config["E2E:BrowserSecret"]
                ? Task.CompletedTask : validate(context);
        });
        __REGISTER__
    }
}
'@
    $extra = ''
    if ($Name -in @('orders','billing')) {
        $extra = 'services.Configure<Microsoft.AspNetCore.Authentication.OpenIdConnect.OpenIdConnectOptions>(E2E.__NAME__.Application.Shared.AuthenticationSchemeNames.OpenIdConnect, options => options.BackchannelHttpHandler = Tls(config));'
    }
    if ($Name -eq 'idp') {
        $extra = @'
        services.Configure<Microsoft.AspNetCore.Authentication.Google.GoogleOptions>(E2E.Idp.Application.Shared.AuthenticationSchemeNames.ExternalProviderPrefix + "google", options => Configure(options, "google"));
        services.Configure<AspNet.Security.OAuth.GitHub.GitHubAuthenticationOptions>(E2E.Idp.Application.Shared.AuthenticationSchemeNames.ExternalProviderPrefix + "github", options => Configure(options, "github"));
        void Configure(Microsoft.AspNetCore.Authentication.OAuth.OAuthOptions options, string provider)
        {
            var origin = config["E2E:ProviderOrigin"]!;
            options.AuthorizationEndpoint = origin + "/api/e2e/provider/" + provider + "/authorize";
            options.TokenEndpoint = origin + "/api/e2e/provider/" + provider + "/token";
            options.UserInformationEndpoint = origin + "/api/e2e/provider/" + provider + "/user";
            options.BackchannelHttpHandler = new ProviderBackchannel(config, Tls(config));
        }
'@
        [IO.File]::WriteAllText((Join-Path $ApiDirectory 'BrowserProviderController.cs'), $browserProviderSource, $utf8)
    }
    $registration = $registration.Replace('__REGISTER__',$extra).Replace('__NAME__',$title)
    [IO.File]::WriteAllText((Join-Path $ApiDirectory 'BrowserFixtureRegistration.cs'), $registration, $utf8)
    [IO.File]::WriteAllText((Join-Path $ApiDirectory 'BrowserTicketController.cs'), $browserTicketSource.Replace('__NAME__',$title), $utf8)
    if ($Name -in @('orders', 'billing')) {
        # 正式 CLI 同样会经机器认证访问测试 Identity；只信任本轮证书，不修改生产 TLS 配置。
        $migratorDirectory = Join-Path (Split-Path $ApiDirectory) "E2E.$title.DbMigrator"
        $tlsFixture = @'
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Http;
using Microsoft.Extensions.Options;
internal static class BootstrapTlsFixture
{
    internal static void Add(IServiceCollection services, IConfiguration config) =>
        services.PostConfigureAll<HttpClientFactoryOptions>(options => options.HttpMessageHandlerBuilderActions.Add(builder =>
            builder.PrimaryHandler = new System.Net.Http.HttpClientHandler {
                ServerCertificateCustomValidationCallback = (request, cert, chain, errors) =>
                    cert?.Thumbprint == config["E2E:CertificateThumbprint"]
            }));
}
'@
        [IO.File]::WriteAllText((Join-Path $migratorDirectory 'BootstrapTlsFixture.cs'), $tlsFixture, $utf8)
        $programPath = Join-Path $migratorDirectory 'Program.cs'
        $program = [IO.File]::ReadAllText($programPath)
        $needle = 'builder.Services.AddResourceAdminBootstrapServices(builder.Configuration);'
        if (-not $program.Contains($needle)) { throw '正式管理员引导入口缺失，不能注入测试证书绑定' }
        [IO.File]::WriteAllText($programPath, $program.Replace($needle, "$needle`n    BootstrapTlsFixture.Add(builder.Services, builder.Configuration);"), $utf8)
    }
}

$browserTicketSource = @'
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;
using E2E.__NAME__.Application.Shared;
using System.Globalization;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
namespace E2E.__NAME__.Api;
[ApiController, AllowAnonymous, Route("api/e2e/browser-ticket")]
public sealed class BrowserTicketController(IConfiguration config, IOptionsMonitor<CookieAuthenticationOptions> options) : ControllerBase
{
    // 会话 Cookie 实际使用的票据存储（官方 SessionStore），不依赖具体实现类型是否另行注册
    private ITicketStore Store => options.Get(AuthenticationSchemeNames.SessionCookie).SessionStore!;
    [HttpPost("delete")]
    public async Task<IActionResult> Delete(string key)
    {
        if (Request.Headers["X-E2E-Secret"] != config["E2E:BrowserSecret"]) return Unauthorized();
        await Store.RemoveAsync(key);
        return Ok(new { deleted = true });
    }
    // 把会话的认证时间（auth_time）往前推，让"会话不够新"的确认身份分支在浏览器里可达，不必真实等满 15 分钟。
    // 只改夹具宿主里这一张票据；判定、对话框与重试仍走生产实现。
    [HttpPost("age")]
    public async Task<IActionResult> Age(string key, int minutes)
    {
        if (Request.Headers["X-E2E-Secret"] != config["E2E:BrowserSecret"]) return Unauthorized();
        var store = Store;
        var ticket = await store.RetrieveAsync(key);
        if (ticket is null) return NotFound();
        var principal = ticket.Principal.Clone();
        var identity = (ClaimsIdentity)principal.Identity!;
        var current = identity.FindFirst("auth_time");
        if (current is null) return Conflict();
        identity.RemoveClaim(current);
        var aged = DateTimeOffset.UtcNow.AddMinutes(-minutes).ToUnixTimeSeconds();
        identity.AddClaim(new Claim("auth_time", aged.ToString(CultureInfo.InvariantCulture), ClaimValueTypes.Integer64));
        await store.RenewAsync(key, new AuthenticationTicket(principal, ticket.Properties, ticket.AuthenticationScheme));
        return Ok(new { authTime = aged });
    }
    [HttpGet]
    public async Task<IActionResult> Get(string? key = null)
    {
        if (Request.Headers["X-E2E-Secret"] != config["E2E:BrowserSecret"]) return Unauthorized();
        var cookie = options.Get(AuthenticationSchemeNames.SessionCookie);
        // 跳过 Cookie Authenticate，观测不能提前触发 OnValidatePrincipal 续期。
        var reference = cookie.TicketDataFormat.Unprotect(cookie.CookieManager.GetRequestCookie(HttpContext, cookie.Cookie.Name!) ?? "");
        key ??= reference?.Principal.FindFirst("Microsoft.AspNetCore.Authentication.Cookies-SessionId")?.Value;
        var ticket = key is null ? null : await Store.RetrieveAsync(key);
        var access = ticket?.Properties.GetTokenValue("access_token");
        return Ok(new {
            exists = ticket is not null, key,
            referenceTokenCount = reference?.Properties.GetTokens().Count() ?? 0,
            referenceClaims = reference?.Principal.Claims.Select(c => c.Type).ToArray() ?? [],
            tokenNames = ticket?.Properties.GetTokens().Select(t => t.Name).ToArray() ?? [],
            expiresAt = ticket?.Properties.GetTokenValue("expires_at"),
            accessHash = access is null ? null : Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(access))),
            // 签发寿命取自访问令牌本身（exp - iat），只回数值，令牌不离开服务端
            accessLifetime = access is null ? (long?)null : Lifetime(access),
            userId = ticket?.Principal.FindFirst("e2e_user_id")?.Value ?? ticket?.Principal.FindFirst("sub")?.Value
        });
    }
    private static long Lifetime(string jwt)
    {
        using var payload = System.Text.Json.JsonDocument.Parse(System.Buffers.Text.Base64Url.DecodeFromChars(jwt.Split('.')[1]));
        return payload.RootElement.GetProperty("exp").GetInt64() - payload.RootElement.GetProperty("iat").GetInt64();
    }
}
'@

$browserProviderSource = @'
using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.WebUtilities;
using OpenIddict.Abstractions;
namespace E2E.Idp.Api;
internal sealed class ProviderBackchannel(IConfiguration config, HttpMessageHandler inner) : DelegatingHandler(inner)
{
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        // GitHub 的官方资料映射固定访问 /user/emails，只重定向传输目标，保留生产 OnCreatingTicket。
        if (request.RequestUri?.AbsoluteUri == "https://api.github.com/user/emails") request.RequestUri = new Uri(config["E2E:ProviderOrigin"] + "/api/e2e/provider/github/emails");
        return base.SendAsync(request, cancellationToken);
    }
}
[ApiController, AllowAnonymous, Route("api/e2e/provider")]
public sealed class BrowserProviderController(IConfiguration config, IOpenIddictTokenManager tokens) : ControllerBase
{
    private static readonly ConcurrentDictionary<string, string> Codes = new();
    [HttpGet("{provider}/authorize")]
    public IActionResult AuthorizeProvider(string provider, string redirect_uri, string state, string code_challenge, string code_challenge_method)
    {
        if (code_challenge_method != "S256" || !redirect_uri.StartsWith(config["E2E:ProviderOrigin"] + "/api/v1/external-auth/" + provider + "/signin", StringComparison.Ordinal)) return BadRequest();
        var code = Guid.NewGuid().ToString("N"); Codes[code] = code_challenge;
        var callback = QueryHelpers.AddQueryString(redirect_uri, new Dictionary<string,string?> { ["code"] = code, ["state"] = state });
        var html = "<!doctype html><html><title>Local " + provider + " provider</title><h1>Local " + provider + " protocol fixture</h1><a id='approve' href='" + System.Net.WebUtility.HtmlEncode(callback) + "'>Approve sign in</a></html>";
        return Content(html, "text/html");
    }
    [HttpPost("{provider}/token")]
    public async Task<IActionResult> Token(string provider)
    {
        var form = await Request.ReadFormAsync();
        var actual = WebEncoders.Base64UrlEncode(SHA256.HashData(Encoding.ASCII.GetBytes(form["code_verifier"].ToString())));
        if (form["client_secret"] != config["E2E:BrowserSecret"] || !Codes.TryRemove(form["code"].ToString(), out var expected) || expected != actual) return BadRequest(new { error = "invalid_grant" });
        // 留协议元数据；不记录 code、verifier 或令牌。
        System.IO.File.AppendAllText(Path.Combine(config["E2E:EvidenceDirectory"]!, "provider-events.jsonl"), JsonSerializer.Serialize(new { provider, pkce = true, operation = "token" }) + "\n");
        return Ok(new { access_token = "local-provider-access-" + provider, token_type = "Bearer", expires_in = 300 });
    }
    [HttpGet("{provider}/user")]
    public IActionResult UserInfo(string provider) => Ok(new {
        id = provider == "github" ? "900123" : "google-e2e-user", sub = "google-e2e-user", login = "github_e2e",
        email = provider + "@oidc-e2e.example.test", email_verified = true,
        name = provider + " browser e2e", avatar_url = (string?)null, picture = (string?)null
    });
    [HttpGet("github/emails")]
    public IActionResult Emails() => Ok(new[] { new { email = "github@oidc-e2e.example.test", primary = true, verified = true } });
    [HttpPost("revoke")]
    public async Task<IActionResult> Revoke(string subject)
    {
        if (Request.Headers["X-E2E-Secret"] != config["E2E:BrowserSecret"]) return Unauthorized();
        var count = 0;
        await foreach (var token in tokens.FindBySubjectAsync(subject))
            if (await tokens.GetTypeAsync(token) == OpenIddictConstants.TokenTypeIdentifiers.RefreshToken && await tokens.TryRevokeAsync(token)) count++;
        return Ok(new { count });
    }
}
'@

function Invoke-Browser([string[]]$Arguments, [string]$InputText, [switch]$AllowFailure) {
    $script:browserCommand++
    # 一次性验收不持久化浏览器状态；自动保存会用临时页面访问已见过的源，干扰真实到期与 UI 导航。
    $browserArgs = @('--session', $browserSession, '--restore', '--restore-save', 'never', '--headed', '--ignore-https-errors', '--args', '--disable-gpu', '--json') + $Arguments
    $response = Invoke-Tool 'agent-browser' $browserArgs ('browser/command-{0:d4}' -f $browserCommand) -StandardInput $InputText -AllowFailure -TimeoutSeconds 45
    [IO.File]::WriteAllText((Join-Path $runRoot ('browser/command-{0:d4}.log' -f $browserCommand)), ('command={0}; exit={1}' -f ($Arguments[0]), $response.ExitCode), $utf8)
    if (-not $response.Output) { if ($AllowFailure) { return $null }; throw "浏览器操作 $($Arguments[0]) 超时或没有响应。" }
    $parsed = ConvertFrom-Json -InputObject $response.Output -AsHashtable
    if (-not $parsed.success) { if ($AllowFailure) { return $null }; throw "浏览器操作失败：$($parsed.error)" }
    return $parsed.data
}

function Invoke-BrowserJs([string]$Code) { (Invoke-Browser @('eval', '--stdin') -InputText $Code).result }
function Get-BrowserTicket([string]$Name = 'orders', [string]$Key) {
    $cookies = Invoke-Browser @('cookies','get')
    $title = [Globalization.CultureInfo]::InvariantCulture.TextInfo.ToTitleCase($Name)
    $reference = @($cookies.cookies | Where-Object { $_.name -eq (Get-SessionCookieName $title) })
    $headers = @{ 'X-E2E-Secret' = $browserFixtureSecret }
    if ($reference.Count) { $headers.Cookie = $reference[0].name + '=' + $reference[0].value }
    $url = $urls[$Name] + '/api/e2e/browser-ticket'
    if ($Key) { $url += '?key=' + [Uri]::EscapeDataString($Key) }
    return (Assert-Http 'ticket-probe-status' 200 $url -Headers $headers).Data
}
function Wait-BrowserUrl([string]$Pattern) {
    $prefix = $Pattern.EndsWith('*')
    $target = $Pattern.TrimEnd('*')
    $deadline = [DateTimeOffset]::UtcNow.AddSeconds(30)
    do {
        $actual = (Invoke-Browser @('get','url')).url
        if (($prefix -and $actual.StartsWith($target, [StringComparison]::Ordinal)) -or $actual -ceq $target) { return }
        Start-Sleep -Milliseconds 200
    } while ([DateTimeOffset]::UtcNow -lt $deadline)
    # 失败现场：截图、按钮状态与控制台，供定位"操作已执行但页面未跳转"。
    # 每项单独容错：页面上下文已销毁或浏览器失联时，抛出的仍是原始的导航超时。
    $label = 'url-timeout-{0:d4}' -f $browserCommand
    $captures = [ordered]@{
        screenshot = { Invoke-Browser @('screenshot', (Join-Path $browserEvidence "$label.png")) | Out-Null }
        buttons = { Write-JsonFile (Join-Path $browserEvidence "$label-buttons.json") (Invoke-BrowserJs '[...document.querySelectorAll("button")].map(b=>{const r=b.getBoundingClientRect();return {text:b.textContent.trim(),disabled:b.disabled,x:r.x,y:r.y,w:r.width,h:r.height};})') }
        console = { Write-JsonFile (Join-Path $browserEvidence "$label-console.json") (Invoke-Browser @('console')) }
    }
    $captureErrors = @()
    foreach ($capture in $captures.GetEnumerator()) {
        try { & $capture.Value } catch { $captureErrors += "$($capture.Key)：$($_.Exception.Message)" }
    }
    if ($captureErrors) { [IO.File]::WriteAllText((Join-Path $browserEvidence "$label-capture-errors.txt"), ($captureErrors -join "`n"), $utf8) }
    throw "浏览器未到达 $target（实际路由 $(([Uri]$actual).GetLeftPart([UriPartial]::Path))），现场见 browser/$label*。"
}
function Save-BrowserEvidence([string]$Label) {
    $current = (Invoke-Browser @('get','url')).url
    if ($current.StartsWith($urls.orders + '/workspace', [StringComparison]::Ordinal)) { Wait-BrowserElement 'app-user-menu button' } else { Wait-BrowserElement '#usernameOrEmail' }
    $picture = Join-Path $browserEvidence "$Label.png"
    Invoke-Browser @('screenshot', $picture) | Out-Null
    Assert-Value "$Label-screenshot-saved" $true ((Test-Path $picture) -and (Get-Item $picture).Length -gt 0)
    $state = Invoke-BrowserJs '({url:location.href,local:{...localStorage},session:{...sessionStorage},visibleCookies:document.cookie})'
    Write-JsonFile (Join-Path $browserEvidence "$Label-storage.json") $state
    Assert-Value "$Label-no-oauth-storage" $false ([bool](($state.local,$state.session,$state.visibleCookies | ConvertTo-Json -Depth 10 -Compress) -match '(access_token|refresh_token|id_token|local-provider-access|eyJ[a-zA-Z0-9_-]+\.)'))
    $errors = Invoke-Browser @('errors')
    Write-JsonFile (Join-Path $browserEvidence "$Label-errors.json") $errors
    Assert-Value "$Label-no-page-errors" 0 @($errors.errors).Count
    $console = Invoke-Browser @('console')
    Write-JsonFile (Join-Path $browserEvidence "$Label-console.json") $console
    Assert-Value "$Label-no-console-errors" 0 @($console.messages | Where-Object { $_.type -eq 'error' }).Count
}
function Wait-BrowserElement([string]$Selector) {
    $literal = ConvertTo-Json $Selector -Compress
    $deadline = [DateTimeOffset]::UtcNow.AddSeconds(15)
    do {
        $ready = Invoke-BrowserJs "(() => { const e=document.querySelector($literal); return !!e && e.getBoundingClientRect().width>0; })()"
        if ($ready) { return }
        Start-Sleep -Milliseconds 200
    } while ([DateTimeOffset]::UtcNow -lt $deadline)
    $label = 'element-timeout-{0:d4}' -f $browserCommand
    try {
        Write-JsonFile (Join-Path $browserEvidence "$label-page.json") (Invoke-BrowserJs '({path:location.pathname,text:document.body.innerText})')
        Write-JsonFile (Join-Path $browserEvidence "$label-errors.json") (Invoke-Browser @('errors'))
        Write-JsonFile (Join-Path $browserEvidence "$label-console.json") (Invoke-Browser @('console'))
        Invoke-Browser @('screenshot', (Join-Path $browserEvidence "$label.png")) | Out-Null
    } catch { [IO.File]::WriteAllText((Join-Path $browserEvidence "$label-capture-error.txt"), $_.Exception.Message, $utf8) }
    throw "浏览器元素未就绪：$Selector。"
}
# 页面刚启动时 Angular 可能仍在替换节点，点击落到被换下的元素上会被静默丢弃（实测偶发）。
# 点击后确认地址确实离开当前页；没离开才重试，不对真实断言放宽。
function Invoke-BrowserNavigationClick([string]$Role, [string]$Name) {
    $from = (Invoke-Browser @('get','url')).url
    for ($attempt = 1; $attempt -le 3; $attempt++) {
        Invoke-Browser @('find','role',$Role,'click','--name',$Name) | Out-Null
        $deadline = [DateTimeOffset]::UtcNow.AddSeconds(5)
        do {
            if ((Invoke-Browser @('get','url')).url -ne $from) {
                if ($attempt -gt 1) { [IO.File]::AppendAllText((Join-Path $browserEvidence 'click-retries.log'), "$Role $Name attempt=$attempt`n", $utf8) }
                return
            }
            Start-Sleep -Milliseconds 200
        } while ([DateTimeOffset]::UtcNow -lt $deadline)
    }
}

# 场景隔离：需要匿名起步的场景先清掉浏览器里的登录 Cookie，不继承上一场景（可能中途失败）留下的会话。
# 清完核对两侧会话 Cookie 都不在，否则直接失败。
function Reset-BrowserSession {
    Invoke-Browser @('cookies','clear') | Out-Null
    $names = @((Get-SessionCookieName 'Orders'), (Get-SessionCookieName 'Idp'))
    $left = @((Invoke-Browser @('cookies','get')).cookies | Where-Object { $names -contains $_.name })
    Assert-Value 'browser-session-reset' 0 $left.Count
}
function Start-BrowserResource {
    Invoke-Browser @('open', $urls.orders) | Out-Null
    Wait-BrowserElement 'a[href="/workspace"]'
    Invoke-BrowserNavigationClick 'link' 'Sign In'
    # Resource 首页的 Sign In 指向受保护工作台，authGuard 直接发起后端 challenge。
    Wait-BrowserUrl ($urls.idp + '/auth/login*')
}
function Wait-BrowserButton([string]$Name) {
    $label = ConvertTo-Json $Name -Compress
    $deadline = [DateTimeOffset]::UtcNow.AddSeconds(15)
    do {
        $ready = Invoke-BrowserJs "[...document.querySelectorAll('button')].some(b => b.textContent.trim() === $label && !b.disabled)"
        if ($ready) { return }
        Start-Sleep -Milliseconds 200
    } while ([DateTimeOffset]::UtcNow -lt $deadline)
    throw "浏览器按钮未就绪：$Name。"
}
function Login-BrowserPassword([string]$Username = 'admin', [string]$Password = $adminPassword, [string]$Landing = ($urls.orders + '/workspace*')) {
    $current = Invoke-Browser @('get','url')
    Invoke-Browser @('auth','save',$browserVault,'--url',$current.url,'--username',$Username,'--password-stdin',
        '--username-selector','#usernameOrEmail','--password-selector','#password','--submit-selector','button[type="submit"]') -InputText $Password | Out-Null
    $script:browserVaultCreated = $true
    Invoke-Browser @('auth','login',$browserVault) | Out-Null
    # 填表后立即删除凭据：中断的运行跑不到 finally，口令不能留在保管库；再次登录会重新保存。
    Invoke-Tool 'agent-browser' @('auth','delete',$browserVault) 'browser-vault-delete' | Out-Null
    $script:browserVaultCreated = $false
    # 凭据由 vault 输入；补发 input 事件使 Signal Forms 接收原生自动填充，再由真实按钮提交。
    $form = Invoke-BrowserJs '(() => { const fields=[document.getElementById("usernameOrEmail"),document.getElementById("password")]; if(fields.some(e=>!e)) return false; for(const e of fields) e.dispatchEvent(new InputEvent("input",{bubbles:true,inputType:"insertText"})); return true; })()'
    if ($form) { Wait-BrowserButton 'Sign In'; Invoke-Browser @('find','role','button','click','--name','Sign In','--exact') | Out-Null }
    Wait-BrowserUrl $Landing
}
function Assert-BrowserProtected([string]$Label) {
    Wait-BrowserElement 'app-user-menu button'
    $response = Invoke-BrowserJs '(async () => { const r = await fetch("/api/e2e/natural"); return {status:r.status,body:await r.json()}; })()'
    Assert-Value "$Label-api-status" 200 $response.status
    Assert-Value "$Label-user-present" $true ([bool]$response.body.userId)
    Write-JsonFile (Join-Path $browserEvidence "$Label-api.json") $response
    $row = Invoke-Sql 'oidc_shared' ('SELECT "Id"||''|''||"Username"||''|''||coalesce("Email",'''')||''|''||coalesce(nullif("DisplayName",''''),"Username") FROM "e2e-orders"."Users" WHERE "Id"=''{0}'';' -f $response.body.userId)
    Assert-Value "$Label-user-projected" $true ([bool]$row)
    [IO.File]::WriteAllText((Join-Path $browserEvidence "$Label-sql.txt"), $row, $utf8)
    $username = $row.Split('|')[1]
    $name = $row.Split('|')[3]
    # 顶栏用户菜单按本地投影的显示名（没有时为用户名）标注
    $displayed = Invoke-BrowserJs 'document.querySelector("[data-testid=\"user-menu-trigger\"]")?.getAttribute("aria-label")'
    Assert-Value "$Label-ui-displays-projected-user" $name $displayed
    Write-JsonFile (Join-Path $browserEvidence "$Label-ui.json") @{ displayed = $displayed; username = $username; name = $name }
    $ticket = Get-BrowserTicket
    Assert-Value "$Label-server-ticket" $true $ticket.exists
    Assert-Value "$Label-reference-no-tokens" 0 $ticket.referenceTokenCount
    Assert-Value "$Label-reference-only-session-id" @('Microsoft.AspNetCore.Authentication.Cookies-SessionId') @($ticket.referenceClaims)
    Assert-Value "$Label-server-has-access" $true ($ticket.tokenNames -contains 'access_token')
    $cookies = Invoke-Browser @('cookies','get')
    $auth = @($cookies.cookies | Where-Object { $_.name -eq (Get-SessionCookieName 'Orders') })
    Assert-Value "$Label-reference-cookie-present" 1 $auth.Count
    Assert-Value "$Label-cookie-httpOnly" $true $auth[0].httpOnly
    Assert-Value "$Label-cookie-secure" $true $auth[0].secure
    $identityTicket = Get-BrowserTicket 'idp'
    Assert-Value "$Label-identity-reference-no-tokens" 0 $identityTicket.referenceTokenCount
    Assert-Value "$Label-identity-local-ticket-no-oauth" 0 @($identityTicket.tokenNames).Count
    Assert-Value "$Label-no-external-ticket-left" 0 @($cookies.cookies | Where-Object { $_.name -match '\.External$' }).Count
    Write-JsonFile (Join-Path $browserEvidence "$Label-ticket.json") $ticket
    Save-BrowserEvidence $Label
    return $ticket
}
function Get-BrowserExpiration($Value) {
    if ($Value -is [DateTimeOffset]) { return $Value }
    if ($Value -is [DateTime]) { return [DateTimeOffset]::new($Value) }
    return [DateTimeOffset]::Parse([string]$Value, [Globalization.CultureInfo]::InvariantCulture, [Globalization.DateTimeStyles]::RoundtripKind)
}
# S9 等的是真实到期：S1 没证实快速档时直接失败，不进入默认寿命的长等待
$browserQuickLifetimeVerified = $false
function Assert-BrowserQuickLifetime {
    if (-not $browserQuickLifetimeVerified) { throw "S1 未证实访问令牌为 $quickAccessTokenSeconds 秒的快速档，跳过真实到期等待。" }
}
function Wait-RealTicketExpiration($Ticket, [string]$Label) {
    $expiration = Get-BrowserExpiration $Ticket.expiresAt
    Write-Host "$Label：真实等待服务端 access_token 边界 $($expiration.ToString('o'))（浏览器不持有令牌）。"
    while ([DateTimeOffset]::UtcNow -le $expiration.AddSeconds(1)) { Start-Sleep -Seconds 2 }
    Assert-Value "$Label-real-expired" $true ([DateTimeOffset]::UtcNow -gt $expiration)
}
function Click-BrowserLogout([string]$Landing = ($urls.orders + '/')) {
    Invoke-BrowserClickUntil 'app-user-menu button' '[role="menuitem"]'
    Invoke-Browser @('find','role','menuitem','click','--name','Sign out') | Out-Null
    Wait-BrowserUrl $Landing
}

function Test-BrowserRoleListRealtime {
    Reset-BrowserSession
    Start-BrowserResource
    Login-BrowserPassword
    $identity = Invoke-BrowserJs '(async () => (await (await fetch("/api/e2e/natural")).json()).userId)()'
    $environment = $services.orders.Environment.Clone()
    Invoke-Tool 'dotnet' @($services.orders.Migrator, '--grant-admin', $identity, '--apply') 'realtime-host-admin' -Variables $environment | Out-Null
    # 完整导航重新读取本地权限；随后只用 SPA 链接进入列表，保留已经建立的通知连接。
    Invoke-Browser @('open', ($urls.orders + '/workspace')) | Out-Null
    # 刚完整加载、/workspace 仍在重定向时，点击偶发落空（菜单没打开）：没打开才重点
    Invoke-BrowserClickUntil 'app-user-menu button' '[role="menuitem"]'
    Invoke-Browser @('find', 'role', 'menuitem', 'click', '--name', 'Admin platform', '--exact') | Out-Null
    Wait-BrowserUrl ($urls.orders + '/platform*')
    Wait-BrowserElement 'a[href="/platform/roles"]'
    Invoke-BrowserNavigationClick 'link' 'Role Management'
    Wait-BrowserUrl ($urls.orders + '/platform/roles*')
    Wait-BrowserElement 'app-role-table table'
    Invoke-Browser @('wait', '--fn', 'window.__e2eHubs?.some(h => h.open && h.sent.some(m => m.target === "Subscribe" && m.resource === "host:roles"))') | Out-Null
    $name = 'rt_' + [Guid]::NewGuid().ToString('N').Substring(0, 12)
    $literal = ConvertTo-Json $name -Compress
    Assert-Value 'realtime-new-role-not-visible-before-mutation' $false (Invoke-BrowserJs "document.querySelector('app-role-table').textContent.includes($literal)")
    # HTTP 变更不调用 Angular 的保存/刷新方法；DOM 只能由真实 Hub 事件更新。
    $created = Invoke-BrowserJs "(async () => { const r=await fetch('/api/v1/roles',{method:'POST',headers:{'Content-Type':'application/json'},body:JSON.stringify({name:$literal,displayName:$literal})}); return {status:r.status,body:await r.json()}; })()"
    Assert-Value 'realtime-role-created' 200 $created.status
    Invoke-Browser @('wait', '--fn', "document.querySelector('app-role-table')?.textContent.includes($literal)") | Out-Null
    $hubs = @(Invoke-BrowserJs 'window.__e2eHubs')
    $live = @($hubs | Where-Object { $_.open })
    Assert-Value 'realtime-notifications-share-one-connection' 1 $live.Count
    Assert-Value 'realtime-shared-hub-path' '/hubs/realtime' $live[0].path
    Assert-Value 'realtime-role-event-received' $true (@($live[0].received | Where-Object { $_ -eq 'Roles.Changed' }).Count -gt 0)
    Write-JsonFile (Join-Path $browserEvidence 'realtime-shared-connection.json') @{ hubs = $hubs; role = $name; visible = $true }
    $picture = Join-Path $browserEvidence 'realtime-role-list.png'
    Invoke-Browser @('screenshot', $picture) | Out-Null
    Assert-Value 'realtime-role-list-screenshot' $true ((Test-Path $picture) -and (Get-Item $picture).Length -gt 0)

    # 恢复补查：推送不持久化，断线期间的变更只能靠"订阅确认后补查一次"拿到。
    # 断开传输触发自动重连，扣住重连后的 Subscribe，在新连接尚未加入分组时变更；
    # 放行后列表必须出现新角色，且新连接从未收到 Roles.Changed——证明来自补查而非推送。
    Invoke-BrowserJs 'window.__e2eHold = "host:roles"; window.__e2eHubSockets.find(s => s.readyState === 1).close(4000, "e2e drop"); true' | Out-Null
    Invoke-Browser @('wait', '--fn', 'window.__e2eHeld.length > 0 && window.__e2eHubs.at(-1).open') | Out-Null
    $missed = 'rt_' + [Guid]::NewGuid().ToString('N').Substring(0, 12)
    $missedLiteral = ConvertTo-Json $missed -Compress
    $offline = Invoke-BrowserJs "(async () => (await fetch('/api/v1/roles',{method:'POST',headers:{'Content-Type':'application/json'},body:JSON.stringify({name:$missedLiteral,displayName:$missedLiteral})})).status)()"
    Assert-Value 'realtime-missed-role-created' 200 $offline
    Assert-Value 'realtime-missed-role-not-visible-before-resubscribe' $false (Invoke-BrowserJs "document.querySelector('app-role-table').textContent.includes($missedLiteral)")
    Invoke-BrowserJs 'window.__e2eReleaseHeld(); true' | Out-Null
    Invoke-Browser @('wait', '--fn', "document.querySelector('app-role-table')?.textContent.includes($missedLiteral)") | Out-Null
    $resumed = @(Invoke-BrowserJs 'window.__e2eHubs') | Select-Object -Last 1
    Assert-Value 'realtime-resubscribed-on-new-connection' $true (@($resumed.sent | Where-Object { $_.target -eq 'Subscribe' -and $_.resource -eq 'host:roles' }).Count -gt 0)
    Assert-Value 'realtime-missed-change-came-from-resync' 0 @($resumed.received | Where-Object { $_ -eq 'Roles.Changed' }).Count
    Write-JsonFile (Join-Path $browserEvidence 'realtime-resync.json') @{ hubs = @(Invoke-BrowserJs 'window.__e2eHubs'); role = $missed; visible = $true }
    Click-BrowserLogout
}

# ---- 身份链路：组织要求两步验证、确认身份对话框、纯外部账号的第二步 ----
# 输入经 stdin 的脚本写入并补发 input 事件（Signal Forms 与 OTP 组件都按它取值）：口令与验证码不进命令行参数。
function Set-BrowserInput([string]$Selector, [string]$Value) {
    $selectorLiteral = ConvertTo-Json $Selector -Compress
    $valueLiteral = ConvertTo-Json $Value -Compress
    $done = Invoke-BrowserJs "(() => { const e=document.querySelector($selectorLiteral); if(!e) return false; e.focus(); e.value=$valueLiteral; e.dispatchEvent(new InputEvent('input',{bubbles:true,inputType:'insertText'})); return true; })()"
    if (-not $done) { throw "浏览器输入框不存在：$Selector。" }
}
function Wait-BrowserCondition([string]$Expression, [string]$Description) {
    $deadline = [DateTimeOffset]::UtcNow.AddSeconds(15)
    do {
        if (Invoke-BrowserJs $Expression) { return }
        Start-Sleep -Milliseconds 200
    } while ([DateTimeOffset]::UtcNow -lt $deadline)
    $label = 'condition-timeout-{0:d4}' -f $browserCommand
    try {
        Write-JsonFile (Join-Path $browserEvidence "$label-page.json") (Invoke-BrowserJs '({path:location.pathname,text:document.body.innerText})')
        Invoke-Browser @('screenshot', (Join-Path $browserEvidence "$label.png")) | Out-Null
    } catch { [IO.File]::WriteAllText((Join-Path $browserEvidence "$label-capture-error.txt"), $_.Exception.Message, $utf8) }
    throw "浏览器条件未满足：$Description。"
}
# 页面刚回来时区块可能还在按取回的状态重绘，点击落到被换下的按钮上会被丢弃：点了没出现预期元素才重点
function Invoke-BrowserClickUntil([string]$Selector, [string]$Expected) {
    $literal = ConvertTo-Json $Expected -Compress
    for ($attempt = 1; $attempt -le 3; $attempt++) {
        Wait-BrowserElement $Selector
        Invoke-Browser @('click', $Selector) | Out-Null
        $deadline = [DateTimeOffset]::UtcNow.AddSeconds(5)
        do {
            if (Invoke-BrowserJs "!!document.querySelector($literal)") {
                if ($attempt -gt 1) { [IO.File]::AppendAllText((Join-Path $browserEvidence 'click-retries.log'), "$Selector attempt=$attempt`n", $utf8) }
                return
            }
            Start-Sleep -Milliseconds 200
        } while ([DateTimeOffset]::UtcNow -lt $deadline)
    }
    Wait-BrowserElement $Expected
}
function Save-BrowserScreenshot([string]$Label) {
    $picture = Join-Path $browserEvidence "$Label.png"
    Invoke-Browser @('screenshot', $picture) | Out-Null
    Assert-Value "$Label-screenshot-saved" $true ((Test-Path $picture) -and (Get-Item $picture).Length -gt 0)
}
function Invoke-BrowserFetch([string]$Path, [string]$Method = 'GET', $Body = $null) {
    $pathLiteral = ConvertTo-Json $Path -Compress
    $init = if ($null -eq $Body) { "{method:'$Method'}" } else {
        "{method:'$Method',headers:{'Content-Type':'application/json'},body:JSON.stringify($(ConvertTo-Json $Body -Compress -Depth 5))}"
    }
    return Invoke-BrowserJs "(async () => { const r=await fetch($pathLiteral,$init); const t=await r.text(); let b=null; try { b=JSON.parse(t); } catch {} return {status:r.status,body:b}; })()"
}
# RFC 6238（HMAC-SHA1、30 秒、6 位）：与身份验证器应用同一算法，由页面上显示的密钥算出验证码。
function Get-TotpCode([string]$Secret, [int]$StepOffset = 0) {
    $alphabet = 'ABCDEFGHIJKLMNOPQRSTUVWXYZ234567'
    $key = [Collections.Generic.List[byte]]::new()
    $buffer = 0; $bits = 0
    foreach ($character in ($Secret -replace '[\s=-]', '').ToUpperInvariant().ToCharArray()) {
        $index = $alphabet.IndexOf($character)
        if ($index -lt 0) { throw '密钥不是 Base32。' }
        $buffer = (($buffer -shl 5) -bor $index) -band 0xFFFF; $bits += 5
        if ($bits -ge 8) { $bits -= 8; $key.Add([byte](($buffer -shr $bits) -band 0xFF)) }
    }
    $step = [long][Math]::Floor([DateTimeOffset]::UtcNow.ToUnixTimeSeconds() / 30) + $StepOffset
    $counter = [byte[]]::new(8)
    [Buffers.Binary.BinaryPrimitives]::WriteInt64BigEndian($counter, $step)
    $hash = [Security.Cryptography.HMACSHA1]::HashData($key.ToArray(), $counter)
    $offset = $hash[19] -band 0x0F
    $value = (([int]$hash[$offset] -band 0x7F) -shl 24) -bor ([int]$hash[$offset + 1] -shl 16) -bor ([int]$hash[$offset + 2] -shl 8) -bor [int]$hash[$offset + 3]
    return ($value % 1000000).ToString('D6')
}
# 页面上的设置流程：读出密钥、输入验证码确认，确认保存恢复码后返回它们
function Complete-BrowserTwoFactorSetup([string]$Label) {
    Wait-BrowserElement '[data-testid="two-factor-secret"]'
    $secret = Invoke-BrowserJs 'document.querySelector("[data-testid=\"two-factor-secret\"]").textContent'
    Save-BrowserScreenshot "$Label-setup"
    Set-BrowserInput '#two-factor-setup-code' (Get-TotpCode $secret)
    Wait-BrowserCondition '!document.querySelector("[data-testid=\"two-factor-enable\"]").disabled' '启用按钮可点'
    Invoke-Browser @('click','[data-testid="two-factor-enable"]') | Out-Null
    Wait-BrowserElement '[data-testid="recovery-codes"]'
    $codes = @(Invoke-BrowserJs '[...document.querySelectorAll("[data-testid=\"recovery-codes\"] li")].map(e => e.textContent.trim())')
    Assert-Value "$Label-recovery-codes-shown" $true ($codes.Count -gt 0)
    Invoke-Browser @('click','label[for="recovery-codes-acknowledged"]') | Out-Null
    Wait-BrowserCondition '!document.querySelector("[data-testid=\"recovery-codes-done\"]").disabled' '已保存恢复码按钮可点'
    Invoke-Browser @('click','[data-testid="recovery-codes-done"]') | Out-Null
    return $codes
}
# 认证时间推到 16 分钟前（超出 15 分钟的"刚认证"窗口），由夹具改写服务端票据
function Set-BrowserIdentitySessionStale {
    $ticket = Get-BrowserTicket 'idp'
    Assert-Value 'stale-session-ticket-present' $true $ticket.exists
    Assert-Http 'stale-session-aged' 200 ($urls.idp + '/api/e2e/browser-ticket/age?minutes=16&key=' + [Uri]::EscapeDataString($ticket.key)) -Method POST -Headers @{ 'X-E2E-Secret' = $browserFixtureSecret } | Out-Null
    # 改写后的票据照常认证（只是不再"刚认证"）
    Assert-Value 'stale-session-still-valid' 200 (Invoke-BrowserJs "(async () => { const c=new AbortController(); setTimeout(() => c.abort(), 8000); try { return (await fetch('/api/v1/auth/me',{signal:c.signal})).status; } catch { return 'timeout'; } })()")
}
function Wait-BrowserDialogClosed {
    Wait-BrowserCondition '!document.querySelector("[data-testid=\"reauthenticate-form\"]")' '确认身份对话框已关闭'
}

$identityFlowUser = $null
# 上一场景走完、浏览器停在该账号已解除限制的 Identity 会话上时才为真；确认身份场景以此为前置
$identityFlowSessionReady = $false
function Test-BrowserRestrictedAuthorization {
    # 浏览器阶段切快速档时 Identity 重启过，之前的 HTTP 会话（服务端票据在进程内缓存）已失效，重新登录
    $admin = Login
    $roles = @((Assert-Http 'identity-flow-roles' 200 ($urls.idp + '/api/v1/roles/options') -Session $admin).Data)
    $adminRole = @($roles | Where-Object { $_.name -eq 'Admin' })
    Assert-Value 'identity-flow-admin-role' 1 $adminRole.Count
    # 本人有设置权限：完成设置、会话解除限制之后由他在浏览器里关掉要求，不依赖发起人的令牌寿命
    $password = New-RandomValue 'Mfa!1'
    $user = (Assert-Http 'identity-flow-create-user' 200 ($urls.idp + '/api/v1/users') -Method POST -Session $admin `
        -Body @{ username = 'mfa_flow_user'; email = 'mfa-flow@oidc-e2e.example.test'; password = $password; isActive = $true; isEmailVerified = $false; roleIds = @($adminRole[0].id) }).Data
    $script:identityFlowUser = @{ Id = $user.id; Username = 'mfa_flow_user'; Password = $password; Email = 'mfa-flow@oidc-e2e.example.test' }
    # 打开要求会让发起人自己的会话也受限（契约如此），用 Bearer 写入，不经受限的 Cookie
    $token = Get-CodeToken $admin "openid profile e2e-idp-api" "idp-web"
    $required = $false
    try {
        Assert-Http 'require-two-factor-on' 204 ($urls.idp + '/api/v1/settings/current-tenant') -Method PUT -Token $token `
            -Body @{ name = 'Security.RequireTwoFactor'; value = 'true' } | Out-Null
        $required = $true
        Reset-BrowserSession
        Start-BrowserResource
        $authorize = Invoke-BrowserJs '(new URL(location.href)).searchParams.get("returnUrl")'
        Assert-Value 'restricted-authorize-pending' $true ([string]$authorize).StartsWith('/connect/authorize')
        Login-BrowserPassword $identityFlowUser.Username $identityFlowUser.Password ($urls.idp + '/auth/two-factor-setup*')
        Assert-Value 'restricted-setup-keeps-authorize' $authorize (Invoke-BrowserJs '(new URL(location.href)).searchParams.get("returnUrl")')
        # 服务端同样守住：受限会话直接进授权端点被转去设置页，接口以错误码拒绝
        Invoke-Browser @('open', ($urls.idp + $authorize)) | Out-Null
        Wait-BrowserUrl ($urls.idp + '/auth/two-factor-setup*')
        $resumed = [string](Invoke-BrowserJs '(new URL(location.href)).searchParams.get("returnUrl")')
        Assert-Value 'restricted-authorize-redirected-to-setup' $true $resumed.StartsWith('/connect/authorize')
        # 必须是同一个缓存授权请求：只比路径前缀抓不到“另起了一个授权请求但最终也登录成功”
        foreach ($parameter in 'client_id', 'request_uri') {
            $pattern = "[?&]$parameter=([^&]*)"
            $original = if ($authorize -match $pattern) { $Matches[1] } else { $null }
            $returned = if ($resumed -match $pattern) { $Matches[1] } else { $null }
            Assert-Value "restricted-authorize-same-$parameter" $true ($null -ne $original -and $original -eq $returned)
        }
        $denied = Invoke-BrowserFetch '/api/v1/users'
        Assert-Value 'restricted-api-status' 403 $denied.status
        Assert-Value 'restricted-api-code' 'Auth:TwoFactorSetupRequired' $denied.body.code
        Assert-Value 'restricted-no-orders-session' 0 @((Invoke-Browser @('cookies','get')).cookies | Where-Object { $_.name -eq (Get-SessionCookieName 'Orders') }).Count
        Complete-BrowserTwoFactorSetup 'restricted' | Out-Null
        # 完成设置后回到原缓存授权请求，依赖方拿到授权码建立会话
        Wait-BrowserUrl ($urls.orders + '/workspace*')
        $ticket = Assert-BrowserProtected 'restricted-setup-resumed'
        $row = Invoke-Sql 'oidc_shared' ('SELECT "TwoFactor_Secret" IS NOT NULL FROM "e2e-idp"."Users" WHERE "Id"=''{0}'';' -f $identityFlowUser.Id)
        Assert-Value 'restricted-two-factor-enabled' 't' $row
        Write-JsonFile (Join-Path $browserEvidence 'restricted-setup-resumed-flow.json') @{ authorize = $authorize.Split('?')[0]; orderUser = $ticket.userId }
        # 回到 Identity：同一会话已解除限制（不换发），由本人关掉要求
        Invoke-Browser @('open', ($urls.idp + '/workspace/settings/profile')) | Out-Null
        Wait-BrowserElement '#profile-email'
        $off = Invoke-BrowserFetch '/api/v1/settings/current-tenant' 'PUT' @{ name = 'Security.RequireTwoFactor'; value = $null }
        Assert-Value 'require-two-factor-off' 204 $off.status
        $required = $false
        $script:identityFlowSessionReady = $true
    }
    finally {
        if ($required) {
            # 场景中途失败：尽力用发起人的令牌关掉要求，不影响后续场景（令牌可能已过期，失败只记录）
            $reset = Invoke-Http ($urls.idp + '/api/v1/settings/current-tenant') -Method PUT -Token $token -Body @{ name = 'Security.RequireTwoFactor'; value = $null }
            if ($reset.Status -ne 204) { $cleanupErrors.Add("组织两步验证要求未能关闭（状态 $($reset.Status)）") }
        }
    }
}

function Test-BrowserReauthenticationDialog {
    if (-not $identityFlowSessionReady) { throw '上一场景未建立 mfa_flow_user 的浏览器会话，不在其他账号的会话上执行。' }
    Invoke-Browser @('open', ($urls.idp + '/workspace/settings/profile')) | Out-Null
    Wait-BrowserElement '#profile-email'
    Assert-Value 'reauth-session-is-flow-user' $identityFlowUser.Id (Invoke-BrowserFetch '/api/v1/auth/me').body.id
    $page = $urls.idp + '/workspace/settings/profile'
    Set-BrowserIdentitySessionStale
    $changed = 'mfa-flow-changed@oidc-e2e.example.test'
    Set-BrowserInput '#profile-email' $changed
    Invoke-Browser @('click','app-profile-panel form button[type="submit"]') | Out-Null
    Wait-BrowserElement '[data-testid="reauthenticate-form"]'
    Assert-Value 'reauth-dialog-password-first' 'password' (Invoke-BrowserJs 'document.querySelector("[data-testid=\"reauthenticate-form\"]").dataset.method')
    Save-BrowserScreenshot 'reauth-dialog-open'
    # 取消：不保存，未保存的修改留在表单里
    Invoke-Browser @('click','[data-testid="reauthenticate-cancel"]') | Out-Null
    Wait-BrowserDialogClosed
    Assert-Value 'reauth-cancel-keeps-email' $identityFlowUser.Email (Invoke-BrowserFetch '/api/v1/auth/me').body.email
    Assert-Value 'reauth-cancel-keeps-edit' $changed (Invoke-BrowserJs 'document.getElementById("profile-email").value')
    Assert-Value 'reauth-cancel-stays' $page (Invoke-Browser @('get','url')).url
    # 再次保存并出示口令：同一份修改保存成功，对话框关闭，仍在原页面
    Invoke-Browser @('click','app-profile-panel form button[type="submit"]') | Out-Null
    Wait-BrowserElement '#reauthenticate-password'
    Set-BrowserInput '#reauthenticate-password' $identityFlowUser.Password
    Invoke-Browser @('click','[data-testid="reauthenticate-submit"]') | Out-Null
    Wait-BrowserDialogClosed
    Wait-BrowserCondition "(async () => (await (await fetch('/api/v1/auth/me')).json()).email === $(ConvertTo-Json $changed -Compress))()" '邮箱已保存'
    Assert-Value 'reauth-success-stays' $page (Invoke-Browser @('get','url')).url
    $row = Invoke-Sql 'oidc_shared' ('SELECT "Email" FROM "e2e-idp"."Users" WHERE "Id"=''{0}'';' -f $identityFlowUser.Id)
    Assert-Value 'reauth-email-persisted' $changed $row
    Save-BrowserScreenshot 'reauth-dialog-saved'
    # 退出依赖方，并确认 Identity 会话也已结束，为纯外部账号场景让出浏览器
    Invoke-Browser @('open', ($urls.orders + '/workspace')) | Out-Null
    Click-BrowserLogout
    Invoke-Browser @('open', ($urls.idp + '/')) | Out-Null
    Wait-BrowserUrl ($urls.idp + '/*')
    if ((Invoke-BrowserFetch '/api/v1/auth/me').status -ne 401) { Invoke-BrowserFetch '/api/v1/auth/logout' 'POST' @{} | Out-Null }
    Assert-Value 'reauth-identity-signed-out' 401 (Invoke-BrowserFetch '/api/v1/auth/me').status
}

function Test-BrowserExternalRecovery {
    Reset-BrowserSession
    # 外部登录场景建立的纯外部账号：先在 Identity 本地登录
    Invoke-Browser @('open', ($urls.idp + '/auth/login')) | Out-Null
    Wait-BrowserButton 'Google'
    Invoke-BrowserNavigationClick 'button' 'Google'
    Wait-BrowserUrl ($urls.idp + '/api/e2e/provider/google/authorize*')
    Invoke-Browser @('find','role','link','click','--name','Approve sign in','--exact') | Out-Null
    Wait-BrowserUrl ($urls.idp + '/workspace*')
    $me = (Invoke-BrowserFetch '/api/v1/auth/me').body
    Assert-Value 'external-account-no-password' $false $me.hasPassword
    Assert-Value 'external-account-two-factor-off' $false $me.isTwoFactorEnabled
    $security = $urls.idp + '/workspace/settings/security'
    Invoke-Browser @('open', $security) | Out-Null
    Wait-BrowserElement '[data-testid="two-factor-turn-on"]'
    # 会话不够新又拿不出凭证：只能重新登录，回来后落回原页面
    Set-BrowserIdentitySessionStale
    Invoke-BrowserClickUntil '[data-testid="two-factor-turn-on"]' '[data-testid="reauthenticate-sign-in-again-submit"]'
    Assert-Value 'external-reauth-sign-in-again' 'signInAgain' (Invoke-BrowserJs 'document.querySelector("[data-testid=\"reauthenticate-form\"]").dataset.method')
    Save-BrowserScreenshot 'external-sign-in-again'
    Invoke-Browser @('click','[data-testid="reauthenticate-sign-in-again-submit"]') | Out-Null
    Wait-BrowserUrl ($urls.idp + '/auth/login*')
    Assert-Value 'external-sign-in-again-return' '/workspace/settings/security' (Invoke-BrowserJs '(new URL(location.href)).searchParams.get("returnUrl")')
    Wait-BrowserButton 'Google'
    Invoke-BrowserNavigationClick 'button' 'Google'
    Wait-BrowserUrl ($urls.idp + '/api/e2e/provider/google/authorize*')
    Invoke-Browser @('find','role','link','click','--name','Approve sign in','--exact') | Out-Null
    Wait-BrowserUrl $security
    # 刚重新登录：直接开始设置
    Invoke-BrowserClickUntil '[data-testid="two-factor-turn-on"]' '[data-testid="two-factor-secret"]'
    $codes = @(Complete-BrowserTwoFactorSetup 'external')
    Wait-BrowserElement '[data-testid="two-factor-on"]'
    Click-BrowserLogout ($urls.idp + '/auth/login*')
    # 依赖方发起登录：外部登录后进入第二步，用恢复码完成，回到原缓存授权请求
    Start-BrowserResource
    $authorize = Invoke-BrowserJs '(new URL(location.href)).searchParams.get("returnUrl")'
    Wait-BrowserButton 'Google'
    Invoke-BrowserNavigationClick 'button' 'Google'
    Wait-BrowserUrl ($urls.idp + '/api/e2e/provider/google/authorize*')
    Invoke-Browser @('find','role','link','click','--name','Approve sign in','--exact') | Out-Null
    Wait-BrowserElement '[data-testid="two-factor-challenge"]'
    Invoke-BrowserClickUntil '[data-testid="two-factor-toggle-mode"]' '[data-testid="two-factor-recovery-input"]'
    Save-BrowserScreenshot 'external-recovery-code'
    Set-BrowserInput '[data-testid="two-factor-recovery-input"]' $codes[0]
    Invoke-Browser @('click','[data-testid="two-factor-submit"]') | Out-Null
    Wait-BrowserUrl ($urls.orders + '/workspace*')
    $ticket = Assert-BrowserProtected 'external-recovery'
    Assert-Value 'external-recovery-same-account' $me.id $ticket.userId
    # 恢复码用一个少一个
    $left = Invoke-Sql 'oidc_shared' ('SELECT coalesce(array_length(string_to_array("TwoFactor_RecoveryCodes", '';''), 1), 0) FROM "e2e-idp"."Users" WHERE "Id"=''{0}'';' -f $me.id)
    Assert-Value 'external-recovery-code-consumed' ($codes.Count - 1) ([int]$left)
    Write-JsonFile (Join-Path $browserEvidence 'external-recovery-flow.json') @{ authorize = ([string]$authorize).Split('?')[0]; recoveryCodesIssued = $codes.Count; recoveryCodesLeft = [int]$left }
    Click-BrowserLogout
}

function Invoke-BrowserScenarios {
    if (-not $quickAccessTokens) {
        # HTTP 场景用默认寿命；浏览器阶段要观察真实续期与到期，切到快速档。
        Stop-Api 'idp'
        Use-QuickAccessTokens $services.idp.Environment
        Start-Api 'idp'
    }
    $session = Invoke-Tool 'agent-browser' @('session','id','--scope','worktree','--prefix',"oidc-$runId") 'browser-session'
    $script:browserSession = $session.Output.Trim()
    $script:browserVault = "oidc-$runId-admin"
    $script:browserStarted = $true
    $socketObservation = @'
(() => {
  window.__e2eHubs = [];
  // 恢复补查验收用：扣住指定资源的 Subscribe 帧，制造"已重连、订阅尚未恢复"的窗口
  window.__e2eHubSockets = [];
  window.__e2eHold = null;
  window.__e2eHeld = [];
  window.__e2eReleaseHeld = () => {
    const held = window.__e2eHeld;
    window.__e2eHold = null;
    window.__e2eHeld = [];
    held.forEach(([socket, data]) => socket.sendNow(data));
  };
  const Native = window.WebSocket;
  function frames(data) {
    if (typeof data !== 'string') return [];
    return data.split(String.fromCharCode(30)).filter(Boolean).flatMap(part => {
      try { return [JSON.parse(part)]; } catch { return []; }
    });
  }
  window.WebSocket = class extends Native {
    constructor(url, protocols) {
      super(url, protocols);
      const path = new URL(url, location.href).pathname;
      if (!path.startsWith('/hubs/')) return;
      this.evidence = { path, open: false, sent: [], received: [] };
      window.__e2eHubs.push(this.evidence);
      window.__e2eHubSockets.push(this);
      this.addEventListener('open', () => this.evidence.open = true);
      this.addEventListener('close', () => this.evidence.open = false);
      this.addEventListener('message', event => frames(event.data).forEach(frame => {
        if (frame.target) this.evidence.received.push(frame.target);
      }));
    }
    send(data) {
      const parsed = frames(data);
      if (this.evidence) parsed.forEach(frame => {
        if (frame.target) this.evidence.sent.push({ target: frame.target, resource: frame.arguments?.[0] });
      });
      if (this.evidence && window.__e2eHold
          && parsed.some(frame => frame.target === 'Subscribe' && frame.arguments?.[0] === window.__e2eHold)) {
        window.__e2eHeld.push([this, data]);
        return;
      }
      return super.send(data);
    }
    sendNow(data) {
      return super.send(data);
    }
  };
})();
'@
    $observationPath = Join-Path $browserEvidence 'socket-observation.js'
    [IO.File]::WriteAllText($observationPath, $socketObservation, $utf8)
    Invoke-Browser @('open', $urls.orders, '--init-script', $observationPath) | Out-Null
    $version = Invoke-Tool 'agent-browser' @('--version') 'browser-version'
    Write-JsonFile (Join-Path $browserEvidence 'runtime.json') @{ session = $browserSession; agentBrowser = $version.Output; userAgent = (Invoke-BrowserJs 'navigator.userAgent'); headed = $true }
    Invoke-Browser @('network','har','start') | Out-Null
    Invoke-Scenario 'S1-browser' {
        Start-BrowserResource
        Login-BrowserPassword
        $script:initialBrowserTicket = Assert-BrowserProtected 'S1'
        # 快速档必须已生效：没生效时 S9 会退回等满默认寿命
        Assert-Value 'S1-access-token-lifetime' $quickAccessTokenSeconds $initialBrowserTicket.accessLifetime
        $script:browserQuickLifetimeVerified = $true
    }
    Invoke-Scenario 'S9-renewal' {
        Assert-BrowserQuickLifetime
        $before = Get-BrowserTicket
        Wait-RealTicketExpiration $before 'S9-success'
        Invoke-Browser @('open', ($urls.orders + '/workspace/dashboard')) | Out-Null
        Wait-BrowserUrl ($urls.orders + '/workspace*')
        $after = Assert-BrowserProtected 'S9-renewed'
        Assert-Value 'S9-access-rotated' $true ($after.accessHash -ne $before.accessHash)
        Assert-Value 'S9-expiration-extended' $true ((Get-BrowserExpiration $after.expiresAt) -gt (Get-BrowserExpiration $before.expiresAt))
    }
    Invoke-Scenario 'S9-rejection' {
        Assert-BrowserQuickLifetime
        $before = Get-BrowserTicket
        $beforeCookies = Invoke-Browser @('cookies','get')
        $copied = @($beforeCookies.cookies | Where-Object { $_.name -eq (Get-SessionCookieName 'Orders') })[0]
        $revoked = (Assert-Http 'S9-revoke-refresh' 200 ($urls.idp + '/api/e2e/provider/revoke?subject=' + $before.userId) -Method POST -Headers @{ 'X-E2E-Secret' = $browserFixtureSecret }).Data
        Assert-Value 'S9-refresh-revoked' $true ($revoked.count -gt 0)
        # 签发方会话同样结束，保证本例回到交互登录；有效 IdP 会话可以正常 SSO 再认证。
        $identityTicket = Get-BrowserTicket 'idp'
        Assert-Value 'S9-identity-ticket-present' $true $identityTicket.exists
        Assert-Http 'S9-end-identity-session' 200 ($urls.idp + '/api/e2e/browser-ticket/delete?key=' + [Uri]::EscapeDataString($identityTicket.key)) -Method POST -Headers @{ 'X-E2E-Secret' = $browserFixtureSecret } | Out-Null
        Wait-RealTicketExpiration $before 'S9-failure'
        # UI 发出的受保护请求收到 401，由真正的 auth interceptor/guard 回到登录。
        Invoke-Browser @('open', ($urls.orders + '/workspace/dashboard')) | Out-Null
        Wait-BrowserUrl ($urls.idp + '/auth/login*')
        $ticket = Get-BrowserTicket 'orders' $before.key
        Assert-Value 'S9-rejected-ticket-deleted' $false $ticket.exists
        $cookies = Invoke-Browser @('cookies','get')
        Assert-Value 'S9-rejected-cookie-cleared' 0 @($cookies.cookies | Where-Object { $_.name -eq (Get-SessionCookieName 'Orders') }).Count
        Assert-Http 'S9-copied-cookie-rejected' 401 ($urls.orders + '/api/v1/auth/me') -Headers @{ Cookie = $copied.name + '=' + $copied.value } | Out-Null
        Save-BrowserEvidence 'S9-rejected'
    }
    Invoke-Scenario 'S11-browser' {
        # 上一场景结束了 Identity 会话；从登录 UI 重新建立会话后，从真实菜单退出。
        Start-BrowserResource
        Login-BrowserPassword
        $before = Assert-BrowserProtected 'S11-before'
        $cookies = Invoke-Browser @('cookies','get')
        $copied = @($cookies.cookies | Where-Object { $_.name -eq (Get-SessionCookieName 'Orders') })[0]
        $offsets = Get-LogOffsets 'idp'
        Click-BrowserLogout
        # 退出携带指向当前会话的 hint：不出现确认页。SPA 路由在请求日志里记成 /index.html，
        # 所以观察确认页一定会调的核对接口；S12 用同一判据做正向对照
        Assert-Value 'S11-logout-without-confirmation' $false ((Read-ApiLog 'idp' $offsets) -match 'GET /api/v1/auth/logout-confirmation')
        $ticket = Get-BrowserTicket 'orders' $before.key
        Assert-Value 'S11-ticket-deleted' $false $ticket.exists
        Assert-Http 'S11-copied-cookie-rejected' 401 ($urls.orders + '/api/v1/auth/me') -Headers @{ Cookie = $copied.name + '=' + $copied.value } | Out-Null
        Invoke-Browser @('open', ($urls.orders + '/workspace')) | Out-Null
        Wait-BrowserUrl ($urls.idp + '/auth/login*')
        Save-BrowserEvidence 'S11-login-required'
    }
    foreach ($provider in @('google','github')) {
        Invoke-Scenario "external-$provider-browser" {
            if ($provider -ne 'google') { Click-BrowserLogout; Start-BrowserResource }
            $expectedBrowserAuthorizes[$provider] = Invoke-BrowserJs '(new URL(location.href)).searchParams.get("returnUrl")'
            Assert-Value "$provider-authorize-return-present" $true ([bool]$expectedBrowserAuthorizes[$provider])
            Wait-BrowserButton $(if ($provider -eq 'google') { 'Google' } else { 'GitHub' })
            Invoke-BrowserNavigationClick 'button' $(if ($provider -eq 'google') { 'Google' } else { 'GitHub' })
            Wait-BrowserUrl ($urls.idp + "/api/e2e/provider/$provider/authorize*")
            Invoke-Browser @('find','role','link','click','--name','Approve sign in','--exact') | Out-Null
            Wait-BrowserUrl ($urls.orders + '/workspace*')
            $ticket = Assert-BrowserProtected "external-$provider"
            $events = @(Get-Content (Join-Path $runRoot 'provider-events.jsonl') | ForEach-Object { ConvertFrom-Json $_ -AsHashtable } | Where-Object { $_.provider -eq $provider })
            Assert-Value "$provider-official-handler-pkce" $true (@($events | Where-Object { $_.pkce }).Count -gt 0)
            $sql = Invoke-Sql 'oidc_shared' ('SELECT "Provider"||''|''||"ProviderUserId" FROM "e2e-idp"."ExternalLoginConnections" WHERE "UserId"=''{0}'';' -f $ticket.userId)
            [IO.File]::WriteAllText((Join-Path $browserEvidence "external-$provider-link.sql.txt"), $sql, $utf8)
            Assert-Value "$provider-external-link-persisted" $true ($sql -match $provider)
        }
    }
    Invoke-Scenario 'S12-logout-confirmation' {
        # 不带 hint 的依赖方退出：Identity 在本源确认页征得同意；取消保持会话，确认后结束会话并回到登记的退出回调
        $logout = $urls.idp + '/connect/logout?client_id=orders-browser&state=s12&post_logout_redirect_uri=' +
            [Uri]::EscapeDataString($urls.orders + '/api/v1/auth/signout')
        # 第一次从另一个 site 发起（localhost 与 127.0.0.1 不同站）：真实浏览器的跨站表单 POST 不带 Lax 会话 Cookie，
        # 请求缓存后的顶层 GET 重入才带上；能进确认页（不是直接回跳）说明会话 Cookie 在重入时恢复了
        $offsets = Get-LogOffsets 'idp'
        $crossSite = ([Uri]$urls.idp).GetLeftPart([UriPartial]::Authority).Replace('127.0.0.1', 'localhost')
        Invoke-Browser @('open', $crossSite + '/') | Out-Null
        Wait-BrowserUrl ($crossSite + '/*')
        $fields = ConvertTo-Json @{ client_id = 'orders-browser'; state = 's12'; post_logout_redirect_uri = $urls.orders + '/api/v1/auth/signout' } -Compress
        $action = ConvertTo-Json ($urls.idp + '/connect/logout') -Compress
        Invoke-BrowserJs "(() => { const f=document.createElement('form'); f.method='POST'; f.action=$action; for (const [k,v] of Object.entries($fields)) { const i=document.createElement('input'); i.type='hidden'; i.name=k; i.value=v; f.appendChild(i); } document.body.appendChild(f); f.submit(); return true; })()" | Out-Null
        Wait-BrowserUrl ($urls.idp + '/auth/logout-confirm*')
        # 按钮在核对接口返回有效之后才渲染：等到它，核对请求一定已经记进日志
        Wait-BrowserElement '[data-testid="logout-confirm-cancel"]'
        $log = Read-ApiLog 'idp' $offsets
        Assert-Value 'S12-cross-site-post-cached' $true ($log -match 'POST /connect/logout')
        Assert-Value 'S12-confirmation-check-observed' $true ($log -match 'GET /api/v1/auth/logout-confirmation')
        Assert-Value 'S12-confirm-url-has-no-hint' $false ((Invoke-Browser @('get','url')).url -match '(?i)id_token')
        Wait-BrowserElement '[data-testid="logout-confirm-cancel"]'
        Invoke-Browser @('click','[data-testid="logout-confirm-cancel"]') | Out-Null
        Wait-BrowserUrl ($urls.idp + '/*')
        Assert-Value 'S12-cancel-keeps-session' 200 (Invoke-BrowserJs '(async () => (await fetch("/api/v1/auth/me")).status)()')
        Invoke-Browser @('open', $logout) | Out-Null
        Wait-BrowserUrl ($urls.idp + '/auth/logout-confirm*')
        Wait-BrowserElement '[data-testid="logout-confirm-submit"]'
        Invoke-Browser @('click','[data-testid="logout-confirm-submit"]') | Out-Null
        Wait-BrowserUrl ($urls.orders + '/*')
        # 回到 Identity 本源再查会话（不带 returnUrl 的登录页会转回首页，不以页面路由作判据）
        Invoke-Browser @('open', $urls.idp + '/') | Out-Null
        Wait-BrowserUrl ($urls.idp + '/*')
        Assert-Value 'S12-confirm-ends-session' 401 (Invoke-BrowserJs '(async () => (await fetch("/api/v1/auth/me")).status)()')
        # 停在 Identity 首页，不是登录页或工作区：只截图存证（Save-BrowserEvidence 按这两种页面等待就绪）
        $picture = Join-Path $browserEvidence 'S12-logout-confirmed.png'
        Invoke-Browser @('screenshot', $picture) | Out-Null
        Assert-Value 'S12-screenshot-saved' $true ((Test-Path $picture) -and (Get-Item $picture).Length -gt 0)
    }
    Invoke-Scenario 'realtime-role-list-browser' { Test-BrowserRoleListRealtime }
    Invoke-Scenario 'restricted-authorization-setup-browser' { Test-BrowserRestrictedAuthorization }
    Invoke-Scenario 'reauthentication-dialog-browser' { Test-BrowserReauthenticationDialog }
    Invoke-Scenario 'external-second-step-recovery-browser' { Test-BrowserExternalRecovery }
    Complete-BrowserNetwork
}
function Close-BrowserFixtures {
    if ($browserStarted) {
        try { Invoke-Browser @('close') | Out-Null } catch { $cleanupErrors.Add("浏览器清理：$($_.Exception.Message)") }
        try { Invoke-Tool 'agent-browser' @('state','clear',$browserSession) 'browser-state-clear' | Out-Null } catch { $cleanupErrors.Add("本轮浏览器状态清理：$($_.Exception.Message)") }
    }
    if ($browserVaultCreated) {
        try { Invoke-Tool 'agent-browser' @('auth','delete',$browserVault) 'browser-vault-delete' | Out-Null } catch { $cleanupErrors.Add("本轮凭据清理：$($_.Exception.Message)") }
    }
}

function Complete-BrowserNetwork {
    $path = Join-Path $browserEvidence 'network.private.har'
    try {
        Invoke-Browser @('network','har','stop',$path) | Out-Null
        $har = ConvertFrom-Json ([IO.File]::ReadAllText($path)) -AsHashtable
        $entries = @($har.log.entries)
        $routes = @($entries | ForEach-Object { ([Uri]$_.request.url).AbsolutePath })
        $continuations = @{}
        Assert-Value 'browser-no-token-endpoint' $false ($routes -contains '/connect/token')
        foreach ($route in @('/api/v1/auth/login','/connect/authorize','/api/v1/auth/signin','/api/v1/auth/me','/api/v1/auth/logout','/connect/logout','/api/v1/auth/signout')) {
            Assert-Value "browser-traversed-$route" $true ($routes -contains $route)
        }
        foreach ($provider in @('google','github')) {
            Assert-Value "$provider-browser-no-token-endpoint" $false ($routes -contains "/api/e2e/provider/$provider/token")
            $original = $expectedBrowserAuthorizes[$provider]
            $continued = @($entries | Where-Object { ([Uri]$_.request.url).PathAndQuery -ceq $original })
            Assert-Value "$provider-original-authorize-resumed" $true ($continued.Count -ge 2)
            $continuations[$provider] = @{ originalAuthorizeObservedCount = $continued.Count; resumed = $continued.Count -ge 2 }
            foreach ($stage in @('challenge','signin','complete')) {
                Assert-Value "$provider-official-$stage" $true ($routes -contains "/api/v1/external-auth/$provider/$stage")
            }
        }
        Write-JsonFile (Join-Path $browserEvidence 'network-summary.json') @{ requests = $entries.Count; routes = $routes; continuations = $continuations }
        foreach ($entry in $entries) {
            Assert-Value 'network-url-no-oauth' $false ($entry.request.url -match '(?i)(access_token|refresh_token|id_token|id_token_hint)=')
            if ($entry.response.content.ContainsKey('text') -and $entry.response.content.mimeType -match 'json') {
                Assert-Value 'browser-json-no-oauth' $false ([string]$entry.response.content.text -match '"(?:access_token|refresh_token|id_token)"\s*:')
            }
            $entry.request.url = ([Uri]$entry.request.url).GetLeftPart([UriPartial]::Path)
            $entry.request.queryString = @(); $entry.request.cookies = @()
            foreach ($header in $entry.request.headers) {
                if ($header.name -match '(?i)^(authorization|cookie|x-e2e-secret)$') { $header.value = '[redacted]' }
                if ($header.name -match '(?i)^(referer|origin)$') { $header.value = $header.value.Split('?')[0] }
            }
            if ($entry.request.ContainsKey('postData')) { $entry.request.postData = @{ mimeType = $entry.request.postData.mimeType; text = '[redacted]' } }
            $entry.response.cookies = @()
            foreach ($header in $entry.response.headers) {
                if ($header.name -match '(?i)^set-cookie$') { $header.value = '[redacted]' }
                if ($header.name -match '(?i)^location$') { $header.value = $header.value.Split('?')[0] }
            }
            $null = $entry.response.content.Remove('text'); $null = $entry.response.content.Remove('encoding')
            if ($entry.response.redirectURL) { $entry.response.redirectURL = $entry.response.redirectURL.Split('?')[0] }
        }
        Write-JsonFile (Join-Path $browserEvidence 'network.har') $har
    } finally { if (Test-Path $path) { Remove-Item $path -Force } }
}
