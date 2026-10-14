# 模板场景与 CI 归属的唯一来源；生成入口、汇总入口与场景覆盖闸门共同使用。
# Slices 记各档位里场景所属的分片：full 为全集（合入后、夜间、发布），pr 为 PR 档子集。
# $MatrixSlices 维护逻辑分组；候选计划明确绑定各组实际所选成员。
# check-template-scenario-coverage.py 保证每个条件行都由某个 PR 档场景生成（行覆盖）；
# 同时检查有效能力的可达两两组合；关键高阶交互由登记场景和真实端到端验证。
$scenarioMap = [ordered]@{
    "identity" = @{
        Slices = @{ full = "identity-role"; pr = "identity-default-and-all-features" }
        Arguments = @(); Frontend = $true; Lint = $true
        Present = @(
            ".github/workflows/ci.yml",
            "backend/src/{name}.Api/Controllers/AuthController.cs",
            "backend/src/{name}.Api/Controllers/TenantController.cs",
            "backend/src/{name}.Infrastructure/Persistence/IdentityControlDbContext.cs",
            "backend/src/{name}.Infrastructure/Persistence/Migrations/Control",
            "backend/src/{name}.DbMigrator"
        )
        Absent = @(
            ".gitlab-ci.yml",
            "backend/src/{name}.Infrastructure/TenantConnections/IdentityTenantConnectionStore.cs",
            "backend/src/{name}.Infrastructure/Persistence/Migrations/Resource",
            "backend/src/{name}.Api/Notifications",
            "backend/tests/{name}.UnitTests/Api/ResourceReadinessGateTests.cs"
        )
        ReadmeContains = @()
        ReadmeExcludes = @()
        # Identity 侧自己是租户连接的来源，映射机器端点而不是消费远端存储
        RequiredTokens = @{
            "backend/src/{name}.Api/Hosting/ComponentEndpoints.cs" = @("MapTenantManagement<", "MapTenantConnections(")
        }
        # 外部登录关闭：Mock 路由、客户端方法与 DTO 都不得留下。
        # 这类残留编译、lint、单测全都放得过——Mock 会对一个后端返回 404 的端点回成功。
        ForbiddenTokens = @("external-auth", "ExternalLoginUrlOutputDto", "ExternalLoginCallbackInputDto")
    }
    "resource" = @{
        Slices = @{ full = "resource-role" }
        Arguments = @("--service-role","Resource"); Frontend = $true; Lint = $true
        Present = @(
            "backend/src/{name}.Infrastructure/Persistence/Migrations/Resource",
            "backend/src/{name}.DbMigrator",
            "backend/tests/{name}.UnitTests/Api/ResourceReadinessGateTests.cs",
            "backend/src/{name}.Client/Dtos/WhoAmIDto.cs"
        )
        Absent = @(
            "backend/src/{name}.Api/Controllers/AuthController.cs",
            "backend/src/{name}.Api/Controllers/TenantController.cs",
            "backend/src/{name}.Infrastructure/Persistence/IdentityControlDbContext.cs",
            "backend/src/{name}.Infrastructure/Persistence/Migrations/Control",
            "frontend/src/app/features/account",
            "backend/src/{name}.Infrastructure/TenantConnections/IdentityTenantConnectionStore.cs"
        )
        ReadmeContains = @()
        ReadmeExcludes = @()
        # 租户连接由框架的远端存储包回源 Identity，模板不再手写客户端。
        # 资源服务是 Token Exchange 的接收方，调用诊断端点与 Client 方法同样生成
        RequiredTokens = @{
            "backend/src/{name}.Infrastructure/{name}.Infrastructure.csproj" = @("Leistd.MultiTenancy.ServiceClient")
            "backend/src/{name}.Infrastructure/DependencyInjection.cs" = @("AddRemoteTenantConnectionStore(")
            "backend/src/{name}.Api/Controllers/ServiceInfoController.cs" = @("WhoAmI(")
        }
        # Resource 固定使用普通路径路由，后端完成认证后返回站内路径。
        # 产物不携带哈希路由配置或本地口令登录契约。
        ForbiddenTokens = @("App.Tenants", "useHash", "withHashLocation", "LoginInputDto", "usernameOrEmail")
    }
    "standalone" = @{
        Slices = @{ full = "standalone-role" }
        # Cookie 会话形态：有本地用户与租户控制面，但不签发 OIDC 令牌。
        # 目的是不让内部系统带着用不到的授权服务器上线——未使用的 /connect/* 端点
        # 与 OpenIddict 存储不是"多余代码"，是需要防护、打补丁、审计的攻击面
        Arguments = @("--service-role","Standalone"); Frontend = $true; Lint = $true
        Present = @(
            "backend/src/{name}.Api/Controllers/AuthController.cs",
            "backend/src/{name}.Api/Controllers/UserController.cs",
            "backend/src/{name}.Api/Controllers/TenantController.cs",
            "backend/src/{name}.Infrastructure/Persistence/IdentityControlDbContext.cs"
        )
        Absent = @(
            "backend/src/{name}.Api/Controllers/ConnectController.cs",
            "backend/src/{name}.Api/Controllers/OpenApplicationController.cs",
            "backend/src/{name}.Application/OpenApplications",
            "backend/src/{name}.Api/Options/OAuthServerOptions.cs", "backend/src/{name}.Application/Auth/Options/OAuthResourceOptions.cs",
            "frontend/src/app/features/platform/components/open-applications"
        )
        ReadmeContains = @()
        ReadmeExcludes = @()
        # OIDC 契约不得残留：端点路径与 OpenIddict 类型都不该出现在产物里
        ForbiddenTokens = @(
            "connect/token", "connect/authorize", "OpenIddict", "App.OpenApplications",
            # 外部登录同样关闭（见 identity 场景的同组断言）
            "external-auth", "ExternalLoginUrlOutputDto", "ExternalLoginCallbackInputDto"
        )
    }
    "identity-notifications" = @{
        Slices = @{ full = "identity-role"; pr = "identity-notifications-and-resource-localization" }
        Arguments = @("--include-notifications"); Frontend = $true; Lint = $true
        Present = @(
            "backend/src/{name}.Api/Notifications/NotificationSecurityAlertPublisher.cs",
            "frontend/src/app/core/services/notification-service.ts"
        )
        Absent = @("backend/src/{name}.Api/Controllers/ExternalAuthController.cs")
        ReadmeContains = @()
        ReadmeExcludes = @()
        RequiredTokens = @{
            "backend/src/{name}.Api/Hosting/ComponentEndpoints.cs" = @("MapNotifications(")
        }
    }
    "resource-notifications" = @{
        Slices = @{ full = "resource-role"; pr = "resource-notifications-and-standalone" }
        Arguments = @("--service-role","Resource","--include-notifications"); Frontend = $true; Lint = $true
        Present = @(
            "backend/src/{name}.Application/Notifications/Constants/AppNotificationTypes.cs",
            "frontend/src/app/core/services/notification-service.ts"
        )
        Absent = @("backend/src/{name}.Api/Controllers/AuthController.cs", "frontend/src/app/features/account")
        ReadmeContains = @()
        ReadmeExcludes = @()
        RequiredTokens = @{
            "backend/src/{name}.Api/Hosting/ComponentEndpoints.cs" = @("MapNotifications(")
        }
        # 同 resource：不带本地口令登录契约。
        ForbiddenTokens = @("useHash", "withHashLocation", "LoginInputDto", "usernameOrEmail")
    }
    "identity-external-login" = @{
        Slices = @{ full = "identity-role" }
        Arguments = @("--include-external-login"); Frontend = $true; Lint = $true
        Present = @("backend/src/{name}.Api/Controllers/ExternalAuthController.cs", "frontend/src/app/features/account/components/external-auth-callback")
        Absent = @()
        ReadmeContains = @()
        ReadmeExcludes = @()
    }
    "standalone-external-login" = @{
        Slices = @{ full = "standalone-role"; pr = "resource-notifications-and-standalone" }
        Arguments = @("--service-role", "Standalone", "--include-external-login"); Frontend = $true; Lint = $true
        Present = @("backend/src/{name}.Api/Controllers/ExternalAuthController.cs", "frontend/src/app/features/account/components/external-auth-callback")
        Absent = @("backend/src/{name}.Api/Controllers/ConnectController.cs", "backend/src/{name}.Api/Options/OAuthServerOptions.cs", "backend/src/{name}.Application/Auth/Options/OAuthResourceOptions.cs", "backend/src/{name}.Api/Auth/Sessions/DistributedTicketStore.cs")
        ReadmeContains = @(); ReadmeExcludes = @()
        RequiredTokens = @{
            "backend/src/{name}.Api/Auth/Authentication/ExternalAuthenticationExtensions.cs" = @("AddGoogle", "AddGitHub", "UsePkce = true", "UserEmailsEndpoint = string.Empty", "AddDistributedTicketStore")
        }
        ForbiddenTokens = @("OpenIddict", "IOAuthProvider", "OAuthTokenInfo", "angular-auth-oidc-client")
    }
    "identity-localization" = @{
        Slices = @{ full = "identity-role" }
        Arguments = @("--include-localization"); Frontend = $true; Lint = $true
        Present = @("backend/src/{name}.Api/Resources/en.json", "frontend/public/i18n/en.json", "frontend/src/app/core/services/language-service.ts")
        Absent = @()
        ReadmeContains = @()
        ReadmeExcludes = @()
    }
    # 全开组合：三个可选特性两两之间的条件块会互相影响，而单开场景各自都能编过。
    # 曾漏过的实例：ExternalAuthController 的 InvalidState 工厂在「外部登录 + 本地化」
    # 同时开启时才编译失败（只开外部登录时 WithCode 那行被裁掉，只开本地化时整个文件被裁掉）。
    "identity-all-features" = @{
        Slices = @{ full = "identity-role"; pr = "identity-default-and-all-features" }
        Arguments = @("--include-notifications","--include-real-time","--include-external-login","--include-localization")
        Frontend = $true; Lint = $true; Verify = $true
        Present = @(
            "backend/src/{name}.Api/Notifications/NotificationSecurityAlertPublisher.cs",
            "backend/src/{name}.Api/Controllers/ExternalAuthController.cs",
            "backend/src/{name}.Api/Resources/en.json",
            "frontend/public/i18n/en.json",
            "frontend/src/app/features/account/components/external-auth-callback",
            "frontend/src/app/core/services/notification-service.ts"
        )
        Absent = @()
        ReadmeContains = @()
        ReadmeExcludes = @()
    }
    "resource-localization" = @{
        Slices = @{ full = "resource-role"; pr = "identity-notifications-and-resource-localization" }
        Arguments = @("--service-role","Resource","--include-localization"); Frontend = $true; Lint = $true
        Present = @("backend/src/{name}.Api/Resources/en.json", "frontend/public/i18n/en.json", "frontend/src/app/core/services/language-service.ts")
        Absent = @("backend/src/{name}.Api/Controllers/AuthController.cs", "frontend/src/app/features/account")
        ReadmeContains = @()
        ReadmeExcludes = @()
        # 同 resource：不带本地口令登录契约。
        ForbiddenTokens = @("useHash", "withHashLocation", "LoginInputDto", "usernameOrEmail")
    }
    "identity-capabilities-01" = @{
        Slices = @{ full = "identity-role"; pr = "identity-default-and-all-features" }
        Arguments = @("--service-role", "Identity", "--include-frontend", "true", "--include-multi-tenancy", "false", "--include-real-time", "false", "--include-email", "false", "--include-operation-records", "false", "--include-notifications", "false", "--include-external-login", "false", "--include-localization", "false")
        Frontend = $true; Lint = $true
        Present = @("backend/src/{name}.Api/Controllers/AuthController.cs")
        Absent = @("backend/src/{name}.Api/Controllers/TenantController.cs", "backend/src/{name}.Infrastructure/Persistence/IdentityControlDbContext.cs", "backend/src/{name}.Infrastructure/Persistence/Migrations/Control", "frontend/src/app/core/services/tenant-context-service.ts", "frontend/src/app/features/platform/components/operation-records", "frontend/_mock/data/operation-record.ts", "backend/src/{name}.Api/Controllers/SettingController.cs", "backend/src/{name}.Application/Auth/AppServices/EmailVerificationAppService.cs")
        ReadmeContains = @(); ReadmeExcludes = @()
    }
    "standalone-capabilities-02" = @{
        Slices = @{ full = "standalone-role"; pr = "resource-notifications-and-standalone" }
        Arguments = @("--service-role", "Standalone", "--include-frontend", "true", "--include-multi-tenancy", "false", "--include-real-time", "false", "--include-email", "false", "--include-operation-records", "false", "--include-notifications", "false", "--include-external-login", "false", "--include-localization", "false")
        Frontend = $true; Lint = $true
        Present = @("backend/src/{name}.Api/Controllers/AuthController.cs")
        Absent = @("backend/src/{name}.Api/Controllers/TenantController.cs", "backend/src/{name}.Infrastructure/Persistence/IdentityControlDbContext.cs", "backend/src/{name}.Infrastructure/Persistence/Migrations/Control", "frontend/src/app/core/services/tenant-context-service.ts", "frontend/src/app/features/platform/components/operation-records", "frontend/_mock/data/operation-record.ts", "backend/src/{name}.Api/Controllers/SettingController.cs", "backend/src/{name}.Application/Auth/AppServices/EmailVerificationAppService.cs")
        ReadmeContains = @(); ReadmeExcludes = @()
    }
    "resource-capabilities-03" = @{
        Slices = @{ full = "resource-role"; pr = "resource-notifications-and-standalone" }
        Arguments = @("--service-role", "Resource", "--include-frontend", "false", "--include-multi-tenancy", "false", "--include-real-time", "false", "--include-email", "false", "--include-operation-records", "false", "--include-notifications", "false", "--include-external-login", "false", "--include-localization", "false")
        Frontend = $false; Lint = $true
        Present = @("backend/src/{name}.DbMigrator/Runners/ResourceAdminBootstrapRunner.cs")
        Absent = @("frontend", "backend/src/{name}.Api/Controllers/TenantController.cs", "backend/src/{name}.Infrastructure/Persistence/IdentityControlDbContext.cs", "backend/src/{name}.Infrastructure/Persistence/Migrations/Control", "frontend/src/app/core/services/tenant-context-service.ts", "frontend/src/app/features/platform/components/operation-records", "frontend/_mock/data/operation-record.ts", "backend/src/{name}.Api/Controllers/SettingController.cs", "backend/src/{name}.Application/Auth/AppServices/EmailVerificationAppService.cs")
        ReadmeContains = @(); ReadmeExcludes = @()
    }
    "resource-capabilities-04" = @{
        Slices = @{ full = "resource-role"; pr = "resource-notifications-and-standalone" }
        Arguments = @("--service-role", "Resource", "--include-frontend", "false", "--include-multi-tenancy", "true", "--include-real-time", "true", "--include-email", "false", "--include-operation-records", "true", "--include-notifications", "true", "--include-external-login", "false", "--include-localization", "true")
        Frontend = $false; Lint = $true
        Present = @("backend/src/{name}.DbMigrator/Runners/ResourceAdminBootstrapRunner.cs")
        Absent = @("frontend", "backend/src/{name}.Api/Controllers/TenantController.cs", "backend/src/{name}.Api/Controllers/SettingController.cs", "backend/src/{name}.Application/Auth/AppServices/EmailVerificationAppService.cs")
        ReadmeContains = @(); ReadmeExcludes = @()
    }
    "identity-capabilities-05" = @{
        Slices = @{ full = "identity-role"; pr = "identity-default-and-all-features" }
        Arguments = @("--service-role", "Identity", "--include-frontend", "true", "--include-multi-tenancy", "true", "--include-real-time", "false", "--include-email", "false", "--include-operation-records", "false", "--include-notifications", "true", "--include-external-login", "false", "--include-localization", "false")
        Frontend = $true; Lint = $true
        Present = @("backend/src/{name}.Api/Controllers/AuthController.cs")
        Absent = @("backend/src/{name}.Api/Controllers/TenantController.cs", "frontend/src/app/features/platform/components/operation-records", "frontend/_mock/data/operation-record.ts", "backend/src/{name}.Api/Controllers/SettingController.cs", "backend/src/{name}.Application/Auth/AppServices/EmailVerificationAppService.cs")
        ReadmeContains = @(); ReadmeExcludes = @()
    }
    "identity-tenant-management-without-history" = @{
        Slices = @{ full = "identity-role"; pr = "identity-default-and-all-features" }
        Arguments = @("--service-role", "Identity", "--include-multi-tenancy", "true", "--include-operation-records", "false", "--include-email", "false", "--include-notifications", "true", "--include-localization", "true")
        Frontend = $true; Lint = $true
        Present = @("backend/src/{name}.Infrastructure/Persistence/IdentityControlDbContext.cs", "frontend/src/app/features/platform/components/tenants/tenants.ts")
        Absent = @("backend/src/{name}.Api/Controllers/TenantController.cs", "frontend/src/app/core/services/impersonation-service.ts", "frontend/src/app/features/platform/components/operation-records")
        ReadmeContains = @(); ReadmeExcludes = @()
        ForbiddenTokens = @("ImpersonationService", "App.Tenants.Impersonation", "canImpersonate", "onImpersonate")
    }
    "standalone-capabilities-06" = @{
        Slices = @{ full = "standalone-role"; pr = "resource-notifications-and-standalone" }
        Arguments = @("--service-role", "Standalone", "--include-frontend", "true", "--include-multi-tenancy", "true", "--include-real-time", "true", "--include-email", "true", "--include-operation-records", "false", "--include-notifications", "false", "--include-external-login", "true", "--include-localization", "false")
        Frontend = $true; Lint = $true
        Present = @("backend/src/{name}.Api/Controllers/AuthController.cs")
        Absent = @("backend/src/{name}.Api/Controllers/TenantController.cs", "frontend/src/app/features/platform/components/operation-records", "frontend/_mock/data/operation-record.ts")
        ReadmeContains = @(); ReadmeExcludes = @()
    }
    "standalone-capabilities-07" = @{
        Slices = @{ full = "standalone-role"; pr = "resource-notifications-and-standalone" }
        Arguments = @("--service-role", "Standalone", "--include-frontend", "true", "--include-multi-tenancy", "false", "--include-real-time", "true", "--include-email", "false", "--include-operation-records", "false", "--include-notifications", "true", "--include-external-login", "true", "--include-localization", "true")
        Frontend = $true; Lint = $true
        Present = @("backend/src/{name}.Api/Controllers/AuthController.cs")
        Absent = @("backend/src/{name}.Api/Controllers/TenantController.cs", "backend/src/{name}.Infrastructure/Persistence/IdentityControlDbContext.cs", "backend/src/{name}.Infrastructure/Persistence/Migrations/Control", "frontend/src/app/core/services/tenant-context-service.ts", "frontend/src/app/features/platform/components/operation-records", "frontend/_mock/data/operation-record.ts", "backend/src/{name}.Api/Controllers/SettingController.cs", "backend/src/{name}.Application/Auth/AppServices/EmailVerificationAppService.cs")
        ReadmeContains = @(); ReadmeExcludes = @()
    }
    "identity-capabilities-08" = @{
        Slices = @{ full = "identity-role"; pr = "identity-default-and-all-features" }
        Arguments = @("--service-role", "Identity", "--include-frontend", "true", "--include-multi-tenancy", "false", "--include-real-time", "true", "--include-email", "true", "--include-operation-records", "true", "--include-notifications", "false", "--include-external-login", "false", "--include-localization", "false", "--ci", "gitlab")
        Frontend = $true; Lint = $true; Verify = $true
        Present = @("backend/src/{name}.Api/Controllers/AuthController.cs", ".gitlab-ci.yml")
        Absent = @(".github", "backend/src/{name}.Api/Controllers/TenantController.cs", "backend/src/{name}.Infrastructure/Persistence/IdentityControlDbContext.cs", "backend/src/{name}.Infrastructure/Persistence/Migrations/Control", "frontend/src/app/core/services/tenant-context-service.ts")
        ReadmeContains = @(); ReadmeExcludes = @()
    }
    "resource-capabilities-09" = @{
        Slices = @{ full = "resource-role"; pr = "resource-notifications-and-standalone" }
        Arguments = @("--service-role", "Resource", "--include-frontend", "true", "--include-multi-tenancy", "false", "--include-real-time", "false", "--include-email", "false", "--include-operation-records", "false", "--include-notifications", "false", "--include-external-login", "false", "--include-localization", "false")
        Frontend = $true; Lint = $true
        Present = @("backend/src/{name}.DbMigrator/Runners/ResourceAdminBootstrapRunner.cs")
        Absent = @("backend/src/{name}.Api/Controllers/TenantController.cs", "backend/src/{name}.Infrastructure/Persistence/IdentityControlDbContext.cs", "backend/src/{name}.Infrastructure/Persistence/Migrations/Control", "frontend/src/app/core/services/tenant-context-service.ts", "frontend/src/app/features/platform/components/operation-records", "frontend/_mock/data/operation-record.ts", "backend/src/{name}.Api/Controllers/SettingController.cs", "backend/src/{name}.Application/Auth/AppServices/EmailVerificationAppService.cs")
        ReadmeContains = @(); ReadmeExcludes = @()
    }
    "resource-host-api-realtime" = @{
        Slices = @{ full = "resource-role"; pr = "resource-notifications-and-standalone" }
        Arguments = @("--service-role", "Resource", "--include-frontend", "false", "--include-multi-tenancy", "false", "--include-real-time", "true", "--include-operation-records", "false", "--ci", "none")
        Frontend = $false; Lint = $true; Verify = $true
        Present = @("backend/tests/{name}.IntegrationTests/ResourceHostPrincipalTests.cs", "backend/tests/{name}.IntegrationTests/RealTimeSubscriptionTests.cs")
        Absent = @(".github", ".gitlab-ci.yml", "frontend", "backend/src/{name}.Api/Notifications", "backend/src/{name}.Infrastructure/Persistence/IdentityControlDbContext.cs")
        ReadmeContains = @(); ReadmeExcludes = @()
    }
    "resource-host-browser-notifications" = @{
        Slices = @{ full = "resource-role"; pr = "resource-notifications-and-standalone" }
        Arguments = @("--service-role", "Resource", "--include-multi-tenancy", "false", "--include-notifications", "true", "--include-operation-records", "false", "--include-localization", "true")
        Frontend = $true; Lint = $true
        Present = @("backend/tests/{name}.IntegrationTests/ResourceHostPrincipalTests.cs", "backend/tests/{name}.IntegrationTests/ResourceBrowserSessionTests.cs", "frontend/src/app/core/services/notification-service.ts")
        Absent = @("backend/src/{name}.Application/RealTime", "backend/src/{name}.Infrastructure/Persistence/IdentityControlDbContext.cs")
        ReadmeContains = @(); ReadmeExcludes = @()
    }
}

# 全量清单显式排序：定义用哈希表（无序），执行顺序要稳定才便于比对历史日志
$AllScenarios = @(
    "identity", "resource", "standalone",
    "identity-notifications", "resource-notifications",
    "identity-external-login", "standalone-external-login",
    "identity-localization", "resource-localization",
    "identity-all-features",
    "identity-capabilities-01", "standalone-capabilities-02", "resource-capabilities-03", "resource-capabilities-04", "identity-capabilities-05", "standalone-capabilities-06", "standalone-capabilities-07", "identity-capabilities-08", "resource-capabilities-09",
    "resource-host-api-realtime", "resource-host-browser-notifications",
    "identity-tenant-management-without-history"
)

# 定义与全量清单必须一一对应。只加定义不加清单，新场景会静默不跑——
# 那比没加更糟：CI 绿着，而它本该覆盖的东西一直没被覆盖。
$definedOnly = @($scenarioMap.Keys | Where-Object { $_ -notin $AllScenarios })
$listedOnly = @($AllScenarios | Where-Object { -not $scenarioMap.Contains($_) })
if ($definedOnly.Count -gt 0) {
    throw "These scenarios are defined but absent from `$AllScenarios, so they would never run: $($definedOnly -join ', ')"
}
if ($listedOnly.Count -gt 0) {
    throw "These scenarios are listed in `$AllScenarios but have no definition: $($listedOnly -join ', ')"
}

$MatrixTiers = @("pr", "full")

# 每档的人工逻辑分组；组数量同时给出 CI 默认分片上限（PR 三片、full 三片）。
$MatrixSlices = [ordered]@{
    pr = [ordered]@{
        "identity-default-and-all-features" = "Identity 默认产物与可选特性全开"
        "resource-notifications-and-standalone"               = "Resource 通知与 Standalone 外部登录（含容器）"
        "identity-notifications-and-resource-localization"    = "Identity 通知与 Resource 本地化"
    }
    full = [ordered]@{
        "identity-role"   = "Identity 形态全部组合"
        "resource-role"   = "Resource 形态全部组合（含容器）"
        "standalone-role" = "Standalone 形态全部组合（含容器）"
    }
}

# 同时认证含前端镜像与纯资源 API 镜像；后者还实际迁移并启动容器。
# Verify = $true 的场景在完整阶段经生成项目的 scripts/verify.ps1 执行构建与测试（有/无前端、开/关本地化、三种 Ci 取值）；
# 其余场景由矩阵逐阶段执行，并核对 verify -List 与矩阵阶段一致。
$ContainerScenarios = @("standalone-external-login", "resource-capabilities-03")

# 本文件被各入口 dot-source：变量名不得与调用方参数同名（PowerShell 变量名不分大小写，
# 循环变量写成 $tier、$slice 会覆盖调用方的 -Tier、-Slice），故统一加 registered 前缀。
# 每个登记场景必须归属全集的一个分片；进 PR 档的场景也须归属 PR 档的一个分片；每个分片至少一个场景。
foreach ($registeredScenario in $AllScenarios) {
    $registeredSlices = $scenarioMap[$registeredScenario].Slices
    if (-not $MatrixSlices.full.Contains([string]$registeredSlices.full)) {
        throw "Scenario '$registeredScenario' must belong to a full-tier slice."
    }
    if ($registeredSlices.Contains("pr") -and -not $MatrixSlices.pr.Contains([string]$registeredSlices.pr)) {
        throw "Scenario '$registeredScenario' names an unknown PR-tier slice '$($registeredSlices.pr)'."
    }
}
foreach ($registeredTier in $MatrixTiers) {
    foreach ($registeredSlice in $MatrixSlices[$registeredTier].Keys) {
        $registeredMembers = @($AllScenarios | Where-Object { $scenarioMap[$_].Slices[$registeredTier] -eq $registeredSlice })
        if ($registeredMembers.Count -eq 0) { throw "The $registeredTier-tier slice '$registeredSlice' has no scenarios." }
    }
    foreach ($registeredContainer in $ContainerScenarios) {
        if (-not $scenarioMap[$registeredContainer].Slices.Contains($registeredTier)) {
            throw "Container scenario '$registeredContainer' must belong to the $registeredTier tier."
        }
    }
}

function Get-TierScenarios([string]$TierName, [string]$SliceName = "") {
    @($AllScenarios | Where-Object {
        $tierSlices = $scenarioMap[$_].Slices
        $tierSlices.Contains($TierName) -and (-not $SliceName -or $tierSlices[$TierName] -eq $SliceName)
    })
}
