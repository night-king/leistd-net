# 模板场景与 CI 归属的唯一来源；生成入口、汇总入口、PR 选择器与场景覆盖闸门共同使用。
# Slices 记各档位里场景所属的分片：full 为全集（合入后、夜间、发布），core 为 PR 的代表场景，每个场景一片。
# $MatrixSlices 维护逻辑分组；候选计划明确绑定各组实际所选成员。
# check-template-scenario-coverage.py 在 full 档上检查行覆盖（每个可达条件行都由某个登记场景生成）
# 与有效能力的可达两两组合；PR 只承担 plan-quality-checks.py 的增量保证，全局覆盖由放行前的 full 证明。
$scenarioMap = [ordered]@{
    "identity" = @{
        Slices = @{ full = "full-01"; core = "identity" }
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
        Slices = @{ full = "full-04" }
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
        Slices = @{ full = "full-03" }
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
        Slices = @{ full = "full-02" }
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
        Slices = @{ full = "full-04" }
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
        Slices = @{ full = "full-03" }
        Arguments = @("--include-external-login"); Frontend = $true; Lint = $true
        Present = @("backend/src/{name}.Api/Controllers/ExternalAuthController.cs", "frontend/src/app/features/account/components/external-auth-callback")
        Absent = @()
        ReadmeContains = @()
        ReadmeExcludes = @()
    }
    "standalone-external-login" = @{
        Slices = @{ full = "full-02"; core = "standalone-external-login" }
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
        Slices = @{ full = "full-04" }
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
        Slices = @{ full = "full-05"; core = "identity-all-features" }
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
        Slices = @{ full = "full-03" }
        Arguments = @("--service-role","Resource","--include-localization"); Frontend = $true; Lint = $true
        Present = @("backend/src/{name}.Api/Resources/en.json", "frontend/public/i18n/en.json", "frontend/src/app/core/services/language-service.ts")
        Absent = @("backend/src/{name}.Api/Controllers/AuthController.cs", "frontend/src/app/features/account")
        ReadmeContains = @()
        ReadmeExcludes = @()
        # 同 resource：不带本地口令登录契约。
        ForbiddenTokens = @("useHash", "withHashLocation", "LoginInputDto", "usernameOrEmail")
    }
    "identity-capabilities-01" = @{
        Slices = @{ full = "full-01" }
        Arguments = @("--service-role", "Identity", "--include-frontend", "true", "--include-multi-tenancy", "false", "--include-real-time", "false", "--include-email", "false", "--include-operation-records", "false", "--include-notifications", "false", "--include-external-login", "false", "--include-localization", "false")
        Frontend = $true; Lint = $true
        Present = @("backend/src/{name}.Api/Controllers/AuthController.cs")
        Absent = @("backend/src/{name}.Api/Controllers/TenantController.cs", "backend/src/{name}.Infrastructure/Persistence/IdentityControlDbContext.cs", "backend/src/{name}.Infrastructure/Persistence/Migrations/Control", "frontend/src/app/core/services/tenant-context-service.ts", "frontend/src/app/features/platform/components/operation-records", "frontend/_mock/data/operation-record.ts", "backend/src/{name}.Api/Controllers/SettingController.cs", "backend/src/{name}.Application/Auth/AppServices/EmailVerificationAppService.cs")
        ReadmeContains = @(); ReadmeExcludes = @()
    }
    "standalone-capabilities-02" = @{
        Slices = @{ full = "full-04" }
        Arguments = @("--service-role", "Standalone", "--include-frontend", "true", "--include-multi-tenancy", "false", "--include-real-time", "false", "--include-email", "false", "--include-operation-records", "false", "--include-notifications", "false", "--include-external-login", "false", "--include-localization", "false")
        Frontend = $true; Lint = $true
        Present = @("backend/src/{name}.Api/Controllers/AuthController.cs")
        Absent = @("backend/src/{name}.Api/Controllers/TenantController.cs", "backend/src/{name}.Infrastructure/Persistence/IdentityControlDbContext.cs", "backend/src/{name}.Infrastructure/Persistence/Migrations/Control", "frontend/src/app/core/services/tenant-context-service.ts", "frontend/src/app/features/platform/components/operation-records", "frontend/_mock/data/operation-record.ts", "backend/src/{name}.Api/Controllers/SettingController.cs", "backend/src/{name}.Application/Auth/AppServices/EmailVerificationAppService.cs")
        ReadmeContains = @(); ReadmeExcludes = @()
    }
    "resource-capabilities-03" = @{
        Slices = @{ full = "full-03"; core = "resource-capabilities-03" }
        Arguments = @("--service-role", "Resource", "--include-frontend", "false", "--include-multi-tenancy", "false", "--include-real-time", "false", "--include-email", "false", "--include-operation-records", "false", "--include-notifications", "false", "--include-external-login", "false", "--include-localization", "false")
        Frontend = $false; Lint = $true
        Present = @("backend/src/{name}.DbMigrator/Runners/ResourceAdminBootstrapRunner.cs")
        Absent = @("frontend", "backend/src/{name}.Api/Controllers/TenantController.cs", "backend/src/{name}.Infrastructure/Persistence/IdentityControlDbContext.cs", "backend/src/{name}.Infrastructure/Persistence/Migrations/Control", "frontend/src/app/core/services/tenant-context-service.ts", "frontend/src/app/features/platform/components/operation-records", "frontend/_mock/data/operation-record.ts", "backend/src/{name}.Api/Controllers/SettingController.cs", "backend/src/{name}.Application/Auth/AppServices/EmailVerificationAppService.cs")
        ReadmeContains = @(); ReadmeExcludes = @()
    }
    "resource-capabilities-04" = @{
        Slices = @{ full = "full-03" }
        Arguments = @("--service-role", "Resource", "--include-frontend", "false", "--include-multi-tenancy", "true", "--include-real-time", "true", "--include-email", "false", "--include-operation-records", "true", "--include-notifications", "true", "--include-external-login", "false", "--include-localization", "true")
        Frontend = $false; Lint = $true
        Present = @("backend/src/{name}.DbMigrator/Runners/ResourceAdminBootstrapRunner.cs")
        Absent = @("frontend", "backend/src/{name}.Api/Controllers/TenantController.cs", "backend/src/{name}.Api/Controllers/SettingController.cs", "backend/src/{name}.Application/Auth/AppServices/EmailVerificationAppService.cs")
        ReadmeContains = @(); ReadmeExcludes = @()
    }
    "identity-capabilities-05" = @{
        Slices = @{ full = "full-01" }
        Arguments = @("--service-role", "Identity", "--include-frontend", "true", "--include-multi-tenancy", "true", "--include-real-time", "false", "--include-email", "false", "--include-operation-records", "false", "--include-notifications", "true", "--include-external-login", "false", "--include-localization", "false")
        Frontend = $true; Lint = $true
        Present = @("backend/src/{name}.Api/Controllers/AuthController.cs")
        Absent = @("backend/src/{name}.Api/Controllers/TenantController.cs", "frontend/src/app/features/platform/components/operation-records", "frontend/_mock/data/operation-record.ts", "backend/src/{name}.Api/Controllers/SettingController.cs", "backend/src/{name}.Application/Auth/AppServices/EmailVerificationAppService.cs")
        ReadmeContains = @(); ReadmeExcludes = @()
    }
    "identity-tenant-management-without-history" = @{
        Slices = @{ full = "full-05" }
        Arguments = @("--service-role", "Identity", "--include-multi-tenancy", "true", "--include-operation-records", "false", "--include-email", "false", "--include-notifications", "true", "--include-localization", "true")
        Frontend = $true; Lint = $true
        Present = @("backend/src/{name}.Infrastructure/Persistence/IdentityControlDbContext.cs", "frontend/src/app/features/platform/components/tenants/tenants.ts")
        Absent = @("backend/src/{name}.Api/Controllers/TenantController.cs", "frontend/src/app/core/services/impersonation-service.ts", "frontend/src/app/features/platform/components/operation-records")
        ReadmeContains = @(); ReadmeExcludes = @()
        ForbiddenTokens = @("ImpersonationService", "App.Tenants.Impersonation", "canImpersonate", "onImpersonate")
    }
    "standalone-capabilities-06" = @{
        Slices = @{ full = "full-05" }
        Arguments = @("--service-role", "Standalone", "--include-frontend", "true", "--include-multi-tenancy", "true", "--include-real-time", "true", "--include-email", "true", "--include-operation-records", "false", "--include-notifications", "false", "--include-external-login", "true", "--include-localization", "false")
        Frontend = $true; Lint = $true
        Present = @("backend/src/{name}.Api/Controllers/AuthController.cs")
        Absent = @("backend/src/{name}.Api/Controllers/TenantController.cs", "frontend/src/app/features/platform/components/operation-records", "frontend/_mock/data/operation-record.ts")
        ReadmeContains = @(); ReadmeExcludes = @()
    }
    "standalone-capabilities-07" = @{
        Slices = @{ full = "full-02" }
        Arguments = @("--service-role", "Standalone", "--include-frontend", "true", "--include-multi-tenancy", "false", "--include-real-time", "true", "--include-email", "false", "--include-operation-records", "false", "--include-notifications", "true", "--include-external-login", "true", "--include-localization", "true")
        Frontend = $true; Lint = $true
        Present = @("backend/src/{name}.Api/Controllers/AuthController.cs")
        Absent = @("backend/src/{name}.Api/Controllers/TenantController.cs", "backend/src/{name}.Infrastructure/Persistence/IdentityControlDbContext.cs", "backend/src/{name}.Infrastructure/Persistence/Migrations/Control", "frontend/src/app/core/services/tenant-context-service.ts", "frontend/src/app/features/platform/components/operation-records", "frontend/_mock/data/operation-record.ts", "backend/src/{name}.Api/Controllers/SettingController.cs", "backend/src/{name}.Application/Auth/AppServices/EmailVerificationAppService.cs")
        ReadmeContains = @(); ReadmeExcludes = @()
    }
    "identity-capabilities-08" = @{
        Slices = @{ full = "full-02" }
        Arguments = @("--service-role", "Identity", "--include-frontend", "true", "--include-multi-tenancy", "false", "--include-real-time", "true", "--include-email", "true", "--include-operation-records", "true", "--include-notifications", "false", "--include-external-login", "false", "--include-localization", "false", "--ci", "gitlab")
        Frontend = $true; Lint = $true; Verify = $true
        Present = @("backend/src/{name}.Api/Controllers/AuthController.cs", ".gitlab-ci.yml")
        Absent = @(".github", "backend/src/{name}.Api/Controllers/TenantController.cs", "backend/src/{name}.Infrastructure/Persistence/IdentityControlDbContext.cs", "backend/src/{name}.Infrastructure/Persistence/Migrations/Control", "frontend/src/app/core/services/tenant-context-service.ts")
        ReadmeContains = @(); ReadmeExcludes = @()
    }
    "resource-capabilities-09" = @{
        Slices = @{ full = "full-05" }
        Arguments = @("--service-role", "Resource", "--include-frontend", "true", "--include-multi-tenancy", "false", "--include-real-time", "false", "--include-email", "false", "--include-operation-records", "false", "--include-notifications", "false", "--include-external-login", "false", "--include-localization", "false")
        Frontend = $true; Lint = $true
        Present = @("backend/src/{name}.DbMigrator/Runners/ResourceAdminBootstrapRunner.cs")
        Absent = @("backend/src/{name}.Api/Controllers/TenantController.cs", "backend/src/{name}.Infrastructure/Persistence/IdentityControlDbContext.cs", "backend/src/{name}.Infrastructure/Persistence/Migrations/Control", "frontend/src/app/core/services/tenant-context-service.ts", "frontend/src/app/features/platform/components/operation-records", "frontend/_mock/data/operation-record.ts", "backend/src/{name}.Api/Controllers/SettingController.cs", "backend/src/{name}.Application/Auth/AppServices/EmailVerificationAppService.cs")
        ReadmeContains = @(); ReadmeExcludes = @()
    }
    "resource-host-api-realtime" = @{
        Slices = @{ full = "full-04" }
        Arguments = @("--service-role", "Resource", "--include-frontend", "false", "--include-multi-tenancy", "false", "--include-real-time", "true", "--include-operation-records", "false", "--ci", "none")
        Frontend = $false; Lint = $true; Verify = $true
        Present = @("backend/tests/{name}.IntegrationTests/ResourceHostPrincipalTests.cs", "backend/tests/{name}.IntegrationTests/RealTimeSubscriptionTests.cs")
        Absent = @(".github", ".gitlab-ci.yml", "frontend", "backend/src/{name}.Api/Notifications", "backend/src/{name}.Infrastructure/Persistence/IdentityControlDbContext.cs")
        ReadmeContains = @(); ReadmeExcludes = @()
    }
    "resource-host-browser-notifications" = @{
        Slices = @{ full = "full-01" }
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

$MatrixTiers = @("core", "full")

# 每档的逻辑分组（与 $AllScenarios 同序）。core 每个场景一片（PR 墙钟取决于最慢的单个场景）；
# full 按最近 full 运行各场景耗时区间静态均衡，成员调整时重测并更新 docs/template/quality-assurance.md「执行组与分组依据」。
$MatrixSlices = [ordered]@{
    core = [ordered]@{
        "identity"                  = "Identity 默认产物"
        "standalone-external-login" = "Standalone 含前端（含容器）"
        "identity-all-features"     = "Identity 可选特性全开（Verify）"
        "resource-capabilities-03"  = "Resource 纯 API（含容器）"
    }
    full = [ordered]@{
        "full-01" = "Identity 默认、租户通知与 Resource 浏览器通知"
        "full-02" = "Standalone 外部登录（含容器）与 Identity 通知、GitLab"
        "full-03" = "Standalone 默认、Resource 纯 API（含容器）与本地化"
        "full-04" = "Resource 默认、通知与实时、Identity 本地化"
        "full-05" = "Identity 全开（Verify）、租户无历史与 Standalone 全量"
    }
}

# 同时认证含前端镜像与纯资源 API 镜像；后者还实际迁移并启动容器。
# Verify = $true 的场景在完整阶段经生成项目的 scripts/verify.ps1 执行构建与测试（有/无前端、开/关本地化、三种 Ci 取值）；
# 其余场景由矩阵逐阶段执行，并核对 verify -List 与矩阵阶段一致。
$ContainerScenarios = @("standalone-external-login", "resource-capabilities-03")

# 本文件被各入口 dot-source：变量名不得与调用方参数同名（PowerShell 变量名不分大小写，
# 循环变量写成 $tier、$slice 会覆盖调用方的 -Tier、-Slice），故统一加 registered 前缀。
# 每个登记场景必须归属全集的一个分片；进 core 档的场景也须归属 core 档的一个分片；每个分片至少一个场景，
# core 档每片恰好一个场景。
foreach ($registeredScenario in $AllScenarios) {
    $registeredSlices = $scenarioMap[$registeredScenario].Slices
    if (-not $MatrixSlices.full.Contains([string]$registeredSlices.full)) {
        throw "Scenario '$registeredScenario' must belong to a full-tier slice."
    }
    foreach ($registeredTierName in @($registeredSlices.Keys)) {
        if ($registeredTierName -cnotin $MatrixTiers) { throw "Scenario '$registeredScenario' names an unknown tier '$registeredTierName'." }
    }
    if ($registeredSlices.Contains("core") -and -not $MatrixSlices.core.Contains([string]$registeredSlices.core)) {
        throw "Scenario '$registeredScenario' names an unknown core-tier slice '$($registeredSlices.core)'."
    }
}
foreach ($registeredTier in $MatrixTiers) {
    foreach ($registeredSlice in $MatrixSlices[$registeredTier].Keys) {
        $registeredMembers = @($AllScenarios | Where-Object { $scenarioMap[$_].Slices[$registeredTier] -eq $registeredSlice })
        if ($registeredMembers.Count -eq 0) { throw "The $registeredTier-tier slice '$registeredSlice' has no scenarios." }
        if ($registeredTier -ceq 'core' -and $registeredMembers.Count -ne 1) { throw "The core-tier slice '$registeredSlice' must hold exactly one scenario." }
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

# 场景生成的项目名（dotnet new -n）：矩阵与模板开发工作区共用，同一场景两处生成的产物同名
function Get-ScenarioProjectName([string]$Scenario) {
    $suffix = (($Scenario -split '-') | ForEach-Object {
        if ($_.Length -eq 0) { return }
        $_.Substring(0, 1).ToUpperInvariant() + $_.Substring(1)
    }) -join ''
    return "Matrix.$suffix"
}

# 每个场景共有的形态：来源标记 .leistd/template.json 总在，且记录本场景的生成名称与模板包标识。
# test-template-matrix.ps1 的 Assert-ScenarioShape 把它并入各场景的 Present / RequiredTokens（{name} 同样展开）。
$CommonScenarioShape = @{
    # 来源标记与读取它的模板同步脚本随每个生成项目下发
    Present = @(".leistd/template.json", "scripts/template-marker.ps1", "scripts/sync-template.ps1")
    RequiredTokens = @{ ".leistd/template.json" = @('"name": "{name}"', '"id": "Leistd.Templates"') }
}

# 场景的全部模板参数取值：template.json 的默认值叠加场景 Arguments（--kebab-name 值；单独的布尔开关为 true）。
# 来源标记的 parameters 必须与之逐项相等。
function Get-ScenarioParameters([string]$Scenario) {
    $config = Get-Content -LiteralPath (Join-Path $PSScriptRoot '..' 'template' '.template.config' 'template.json') -Raw -Encoding UTF8 | ConvertFrom-Json -AsHashtable
    $values = [ordered]@{}
    $options = @{}
    foreach ($entry in $config.symbols.GetEnumerator()) {
        if ($entry.Value.type -cne 'parameter') { continue }
        $values[$entry.Key] = if ($entry.Value.datatype -ceq 'bool') { $entry.Value.defaultValue -ceq 'true' } else { [string]$entry.Value.defaultValue }
        $options['--' + ([regex]::Replace($entry.Key, '(?<!^)(?=[A-Z])', '-')).ToLowerInvariant()] = $entry.Key
    }
    $arguments = @($scenarioMap[$Scenario].Arguments)
    for ($index = 0; $index -lt $arguments.Count; $index++) {
        $name = $options[$arguments[$index]]
        if (-not $name) { throw "Scenario '$Scenario' passes an unknown template option: $($arguments[$index])" }
        $next = if ($index + 1 -lt $arguments.Count) { $arguments[$index + 1] } else { $null }
        if ($values[$name] -is [bool]) {
            if ($next -cin @('true', 'false')) { $values[$name] = $next -ceq 'true'; $index++ } else { $values[$name] = $true }
        } else {
            if ($null -eq $next) { throw "Scenario '$Scenario' option $($arguments[$index]) has no value" }
            $values[$name] = $next; $index++
        }
    }
    return $values
}

# 场景是否带浏览器端到端测试：与模板的 LocalIdentity（ServiceRole 不是 Resource，前端随之必有）同一判据。
# 端到端只在生成项目的 verify.ps1 里执行，回执 E2ETests 按此核对。
function Test-ScenarioE2E([string]$Scenario) {
    return (Get-ScenarioParameters $Scenario).ServiceRole -cne 'Resource'
}

# 核对一个生成产物的来源标记：模板包身份、生成名称、全部参数取值、各项目实际的 UserSecretsId 与生成时的 SDK。
# 调用方先 dot-source template/scripts/template-marker.ps1（Read-TemplateMarker 在那里）。
# 从源码目录安装的模板记录占位身份 0.0.0-source 与全零 SHA；模板包安装的记录打包时写入的版本与源提交。
function Assert-ScenarioMarker([string]$ProjectRoot, [string]$Scenario, [string]$ProjectName, [string]$PackageVersion, [string]$SourceSha, [string]$SdkVersion) {
    $marker = Read-TemplateMarker $ProjectRoot
    $problems = @()
    if ($marker.package.id -cne 'Leistd.Templates') { $problems += "package.id '$($marker.package.id)'" }
    if ($marker.package.version -cne $PackageVersion) { $problems += "package.version '$($marker.package.version)'，应为 '$PackageVersion'" }
    if ($marker.package.sourceSha -cne $SourceSha) { $problems += "package.sourceSha '$($marker.package.sourceSha)'，应为 '$SourceSha'" }
    if ($marker.sdkVersion -cne $SdkVersion) { $problems += "sdkVersion '$($marker.sdkVersion)'，应为 '$SdkVersion'" }
    if ($marker.name -cne $ProjectName) { $problems += "name '$($marker.name)'，应为 '$ProjectName'" }
    $expected = Get-ScenarioParameters $Scenario
    $names = @(@($expected.Keys) + @($marker.parameters.Keys) | Sort-Object -Unique -CaseSensitive)
    foreach ($name in $names) {
        if (-not $marker.parameters.Contains($name) -or -not $expected.Contains($name) -or
            $marker.parameters[$name] -isnot $expected[$name].GetType() -or $marker.parameters[$name] -cne $expected[$name]) {
            $problems += "parameters.$name '$($marker.parameters[$name])'，应为 '$($expected[$name])'"
        }
    }
    $projects = @{}
    foreach ($file in Get-ChildItem -LiteralPath $ProjectRoot -Recurse -File -Filter '*.csproj') {
        $match = [regex]::Match([IO.File]::ReadAllText($file.FullName), '<UserSecretsId>([^<]+)</UserSecretsId>')
        if ($match.Success) { $projects[[IO.Path]::GetRelativePath($ProjectRoot, $file.FullName).Replace('\', '/')] = $match.Groups[1].Value }
    }
    foreach ($project in @(@($projects.Keys) + @($marker.userSecretsIds.Keys) | Sort-Object -Unique -CaseSensitive)) {
        if ($projects[$project] -cne $marker.userSecretsIds[$project]) {
            $problems += "userSecretsIds['$project'] '$($marker.userSecretsIds[$project])'，项目实际为 '$($projects[$project])'"
        }
    }
    if ($problems) { throw "Scenario '$Scenario' source marker differs:`n  $($problems -join "`n  ")" }
}
