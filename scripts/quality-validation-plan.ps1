# Receipts are checked against this independently supplied candidate plan.
# Tier full: every registered scenario and responsibility (only a verified documentation push may narrow).
# Tier core: a direct-PR plan; its selection comes from the planner, here only its shape.
#   ScenarioSelection selector: narrowed by proof; representative: inputs unproven, so every core scenario with
#   full stages, all framework tests and consumers, and every job except the path-triggered PostgreSQL/OIDC.
function Read-QualityValidationPlan([string]$Path, [string]$ExpectedTier) {
    $plan = Get-Content -LiteralPath $Path -Raw | ConvertFrom-Json
    $head = git rev-parse HEAD
    $jobNames = @('frontend-gates', 'framework-pack', 'package-consumption', 'template-generation', 'template-slices', 'test', 'postgresql-e2e', 'oidc-e2e')
    if ($LASTEXITCODE -ne 0 -or $plan.Version -ne 4 -or $plan.CandidateSha -cne $head -or $ExpectedTier -cnotin $MatrixTiers -or
        $plan.Tier -cne $ExpectedTier -or $plan.Mode -cnotin @('full', 'frontend', 'backend', 'documentation') -or
        $plan.ReleaseChannel -cnotin @('', 'stable', 'beta') -or
        $plan.DocsOnly -isnot [bool] -or $plan.FrameworkTests -isnot [bool] -or $plan.ContainerSmoke -isnot [bool] -or
        $plan.ReleaseRequired -isnot [bool] -or $plan.GeneratedDocumentation -isnot [bool] -or $plan.PackageDocumentation -isnot [bool] -or
        $plan.DependencyAudit -isnot [bool] -or
        $plan.Jobs -isnot [pscustomobject] -or
        (Compare-Object ($jobNames | Sort-Object) @($plan.Jobs.PSObject.Properties.Name | Sort-Object))) {
        throw 'Invalid quality plan: version, candidate, tier, mode or responsibilities differ.'
    }
    $reuse = $plan.QualityReuse
    $reuseFields = @('Verified', 'RunId', 'Attempt', 'HeadSha', 'Tree', 'PlanArtifactId')
    if ($plan.QualityExecution -cnotin @('current', 'reuse') -or $plan.QualityValidated -isnot [bool] -or
        $reuse -isnot [pscustomobject] -or $reuse.Verified -isnot [bool] -or
        (Compare-Object $reuseFields @($reuse.PSObject.Properties.Name)) -or
        $reuse.RunId -isnot [long] -or $reuse.Attempt -isnot [long] -or $reuse.PlanArtifactId -isnot [long] -or
        $reuse.HeadSha -isnot [string] -or $reuse.Tree -isnot [string]) {
        throw 'Invalid quality execution evidence.'
    }
    if ($plan.QualityExecution -ceq 'current') {
        if ($reuse.Verified -or $reuse.RunId -ne 0 -or $reuse.Attempt -ne 0 -or $reuse.PlanArtifactId -ne 0 -or
            $reuse.HeadSha -cne '' -or $reuse.Tree -cne '') { throw 'Current execution cannot claim reused evidence.' }
    } else {
        if (-not $reuse.Verified -or $reuse.RunId -le 0 -or $reuse.Attempt -le 0 -or $reuse.PlanArtifactId -le 0 -or
            $reuse.HeadSha -cnotmatch '^[0-9a-f]{40}$' -or $reuse.Tree -cnotmatch '^[0-9a-f]{40}$' -or
            $plan.ReleaseChannel -cne 'stable' -or $plan.Event -cne 'push' -or -not $plan.ReleaseRequired -or
            $plan.Mode -cne 'full' -or $plan.Tier -cne 'full') { throw 'Invalid stable reuse evidence.' }
        $parents = (git show -s --format=%P $head) -split ' '
        if ($LASTEXITCODE -ne 0 -or $parents.Count -ne 2 -or $parents[1] -cne $reuse.HeadSha) {
            throw 'Reused evidence is not from the candidate second parent.'
        }
        $tree = git rev-parse "$head^{tree}"
        if ($LASTEXITCODE -ne 0 -or $tree -cne $reuse.Tree) { throw 'Reused evidence tree differs from the candidate.' }
        $sourceTree = git rev-parse "$($reuse.HeadSha)^{tree}"
        if ($LASTEXITCODE -ne 0 -or $sourceTree -cne $tree) { throw 'Reused source tree differs from the candidate.' }
    }
    foreach ($name in $jobNames) {
        if ($plan.Jobs.$name -isnot [bool]) { throw "Invalid job responsibility: $name" }
    }
    # 模板通道：Changed 表示自上一个 template-v tag 起模板输入有变化。main（stable）上框架与模板同号发布：
    # Required 与 ReleaseRequired 相同，模板有变化时两者都必须为真；其他通道 Required 恒为假
    $template = $plan.TemplateRelease
    $stable = $plan.ReleaseChannel -ceq 'stable'
    if ($template -isnot [pscustomobject] -or $template.Changed -isnot [bool] -or $template.Required -isnot [bool] -or
        $template.BaseTag -isnot [string] -or $template.BaseSha -isnot [string] -or $template.Reason -isnot [string] -or
        ($template.Required -and -not $stable) -or
        ($stable -and ($template.Required -ne $plan.ReleaseRequired -or ($template.Changed -and -not $template.Required))) -or
        ($template.Changed -and $plan.ReleaseChannel -cnotin @('stable', 'beta'))) {
        throw 'Invalid template release responsibility.'
    }
    # 前端依赖审计按输入触发，但 stable 是模板发布放行门槛，必须审计
    if ($stable -and -not $plan.DependencyAudit) { throw 'Stable publication requires the frontend dependency audit.' }
    $core = $ExpectedTier -ceq 'core'
    $registered = @(Get-TierScenarios 'full')
    $selected = @($plan.Scenarios)
    if ($plan.Scenarios -isnot [array] -or ($null -ne $plan.ConsumerProjects -and $plan.ConsumerProjects -isnot [array]) -or
        $selected.Count -ne @($selected | Sort-Object -Unique -CaseSensitive).Count -or
        @($selected | Where-Object { $_ -cnotin $registered }).Count -gt 0 -or
        $plan.Jobs.'template-slices' -ne ($selected.Count -gt 0) -or $plan.Jobs.test -ne $plan.FrameworkTests -or
        $plan.DocsOnly -ne (@($jobNames | Where-Object { $plan.Jobs.$_ }).Count -eq 0) -or
        ($plan.Jobs.'package-consumption' -and -not $plan.Jobs.'framework-pack') -or
        ($plan.GeneratedDocumentation -and -not $plan.Jobs.'template-generation') -or
        ($plan.PackageDocumentation -and -not $plan.Jobs.'package-consumption')) { throw 'Invalid quality plan responsibility set.' }
    # 发布候选的放行责任：全部作业（含 PG/OIDC，不得不适用）、全部场景、全部框架测试与两个容器场景
    if (($plan.ReleaseRequired -or $template.Required) -and ($plan.Mode -cne 'full' -or $plan.Tier -cne 'full' -or -not $plan.ContainerSmoke -or
        $plan.FrameworkTestSelection -cne 'all' -or $null -ne $plan.ConsumerProjects -or
        $selected.Count -ne $registered.Count -or @($jobNames | Where-Object { -not $plan.Jobs.$_ }).Count -gt 0)) {
        throw 'Publication requires full candidate validation.'
    }
    $expectedSelection = if ($selected.Count -eq 0) { @('none') } elseif (-not $core) { @('all') } else { @('selector', 'representative') }
    if ($plan.ScenarioSelection -cnotin $expectedSelection) { throw 'Invalid scenario selection.' }
    if ($plan.ScenarioSelection -ceq 'representative' -and ($plan.Mode -cne 'full' -or
        @(Get-TierScenarios 'core' | Where-Object { $_ -cnotin $selected }).Count -gt 0 -or
        $plan.FrameworkTestSelection -cne 'all' -or $null -ne $plan.ConsumerProjects -or
        @($jobNames | Where-Object { $_ -cnotin @('postgresql-e2e', 'oidc-e2e') -and -not $plan.Jobs.$_ }).Count -gt 0)) {
        throw 'Representative smoke requires full stages, every core scenario and complete framework responsibilities.'
    }
    $documentation = $plan.Mode -ceq 'documentation'
    if ($documentation) {
        if ($selected.Count -ne 0 -or $plan.FrameworkTests -or $null -eq $plan.ConsumerProjects -or
            @($plan.ConsumerProjects).Count -ne 0 -or $plan.ContainerSmoke -or
            $plan.Jobs.'postgresql-e2e' -or $plan.Jobs.'oidc-e2e' -or
            $plan.Jobs.'template-generation' -ne $plan.GeneratedDocumentation -or
            $plan.Jobs.'framework-pack' -ne $plan.PackageDocumentation -or
            (-not $core -and ($plan.Event -cne 'push' -or $plan.ReleaseChannel -cnotin @('stable', 'beta') -or $plan.QualityBaseline.Verified -isnot [bool] -or -not $plan.QualityBaseline.Verified))) {
            throw 'Invalid documentation responsibilities.'
        }
    } elseif (-not $core) {
        # full 档的运行时计划不得收窄：全部场景、全部作业、全部框架测试与消费者
        if ($plan.Mode -cne 'full' -or $selected.Count -ne $registered.Count -or @($jobNames | Where-Object { -not $plan.Jobs.$_ }).Count -gt 0 -or
            $plan.FrameworkTestSelection -cne 'all' -or $null -ne $plan.ConsumerProjects) {
            throw 'Full-tier runtime plan may not narrow validation.'
        }
    } else {
        if ($selected.Count -eq 0 -or -not $plan.Jobs.'framework-pack') {
            throw 'Runtime modes must retain scenarios and the candidate package.'
        }
        if (-not $plan.FrameworkTests -and ($null -eq $plan.ConsumerProjects -or @($plan.ConsumerProjects).Count -ne 0 -or
            $plan.Jobs.'package-consumption' -ne $plan.PackageDocumentation)) {
            throw 'Invalid template-stage responsibilities.'
        }
        if (($plan.Mode -cin @('frontend', 'backend') -and $plan.FrameworkTests) -or
            ($plan.Mode -ceq 'frontend' -and -not $plan.Jobs.'frontend-gates') -or
            ($plan.FrameworkTests -and (-not $plan.Jobs.'package-consumption' -or -not $plan.PackageDocumentation -or
                ($null -ne $plan.ConsumerProjects -and @($plan.ConsumerProjects).Count -eq 0)))) {
            throw 'Invalid template-stage responsibilities.'
        }
    }
    if ($plan.FrameworkTestProjects -isnot [array] -or $plan.FrameworkTestSelection -cnotin @('all', 'affected', 'none') -or
        ($plan.FrameworkTestSelection -ceq 'none') -ne (-not $plan.FrameworkTests) -or
        $plan.FrameworkTests -ne (@($plan.FrameworkTestProjects).Count -gt 0) -or
        @($plan.FrameworkTestProjects | Where-Object { $_ -isnot [string] -or $_ -cnotmatch '^framework/tests/[^\s]+\.Tests\.csproj$' }).Count -gt 0 -or
        @($plan.FrameworkTestProjects | Sort-Object -Unique -CaseSensitive).Count -ne @($plan.FrameworkTestProjects).Count) {
        throw 'Invalid framework test project list.'
    }
    if ($plan.ContainerSmoke -and $plan.Mode -cne 'full') { throw 'Container checks require full stages.' }
    # 执行组：core 每个选中场景一组（按登记顺序）；full 按登记分组保留成员与顺序
    $expectedGroups = if ($core) { @($AllScenarios | Where-Object { $_ -cin $selected } | ForEach-Object { , @($_) }) } else {
        @($MatrixSlices.full.Keys | ForEach-Object {
            $members = @(Get-TierScenarios 'full' $_ | Where-Object { $_ -cin $selected })
            if ($members.Count -gt 0) { , $members }
        })
    }
    $count = $expectedGroups.Count
    if ($plan.Slices -isnot [array] -or @($plan.Slices).Count -ne $count) {
        throw 'Invalid quality plan execution group count.'
    }
    $assigned = @()
    for ($index = 0; $index -lt $count; $index++) {
        $group = $plan.Slices[$index]
        if ($group.key -cne ('execution-{0:00}' -f ($index + 1)) -or $group.title -isnot [string] -or
            [string]::IsNullOrWhiteSpace($group.title) -or $group.Scenarios -isnot [array] -or
            @($group.Scenarios).Count -eq 0 -or $group.Containers -isnot [array] -or
            @($group.Scenarios | Where-Object { $_ -cnotin $selected }).Count -gt 0) {
            throw 'Invalid quality plan execution group.'
        }
        if (Compare-Object @($expectedGroups[$index]) @($group.Scenarios) -CaseSensitive -SyncWindow 0) {
            throw 'Quality plan must preserve registered group members and order.'
        }
        $requiredContainers = @($group.Scenarios | Where-Object { $plan.ContainerSmoke -and $_ -cin $ContainerScenarios })
        if (@($group.Containers).Count -ne $requiredContainers.Count -or
            @($group.Containers | Sort-Object -Unique).Count -ne @($group.Containers).Count -or
            @($group.Containers | Where-Object { $_ -cnotin $requiredContainers }).Count -gt 0) {
            throw 'Invalid quality plan container assignment.'
        }
        $assigned += @($group.Scenarios)
    }
    if ($assigned.Count -ne $selected.Count -or @($assigned | Sort-Object -Unique).Count -ne $assigned.Count -or
        @($selected | Where-Object { $_ -cnotin $assigned }).Count -gt 0) {
        throw 'Quality plan must assign every selected scenario exactly once.'
    }
    return $plan
}
