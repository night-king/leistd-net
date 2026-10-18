# Receipts are checked against this independently supplied candidate plan.
function Read-QualityValidationPlan([string]$Path, [string]$ExpectedTier) {
    $plan = Get-Content -LiteralPath $Path -Raw | ConvertFrom-Json
    $head = git rev-parse HEAD
    $jobNames = @('frontend-gates', 'framework-pack', 'package-consumption', 'template-generation', 'template-slices', 'test', 'postgresql-e2e', 'oidc-e2e')
    if ($LASTEXITCODE -ne 0 -or $plan.Version -ne 3 -or $plan.CandidateSha -cne $head -or
        $plan.Tier -cne $ExpectedTier -or $plan.Mode -cnotin @('full', 'frontend', 'backend', 'documentation') -or
        $plan.DocsOnly -isnot [bool] -or $plan.FrameworkTests -isnot [bool] -or $plan.ContainerSmoke -isnot [bool] -or
        $plan.ReleaseRequired -isnot [bool] -or $plan.GeneratedDocumentation -isnot [bool] -or $plan.PackageDocumentation -isnot [bool] -or
        $plan.Jobs -isnot [pscustomobject] -or
        (Compare-Object ($jobNames | Sort-Object) @($plan.Jobs.PSObject.Properties.Name | Sort-Object))) {
        throw 'Invalid quality plan: version, candidate, tier, mode or responsibilities differ.'
    }
    foreach ($name in $jobNames) {
        if ($plan.Jobs.$name -isnot [bool]) { throw "Invalid job responsibility: $name" }
    }
    $registered = @(Get-TierScenarios $ExpectedTier)
    $selected = @($plan.Scenarios)
    if ($plan.Scenarios -isnot [array] -or ($null -ne $plan.ConsumerProjects -and $plan.ConsumerProjects -isnot [array]) -or
        $selected.Count -ne @($selected | Sort-Object -Unique -CaseSensitive).Count -or
        @($selected | Where-Object { $_ -cnotin $registered }).Count -gt 0 -or
        $plan.Jobs.'template-slices' -ne ($selected.Count -gt 0) -or $plan.Jobs.test -ne $plan.FrameworkTests -or
        $plan.DocsOnly -ne (@($jobNames | Where-Object { $plan.Jobs.$_ }).Count -eq 0) -or
        ($plan.Jobs.'package-consumption' -and -not $plan.Jobs.'framework-pack') -or
        ($plan.GeneratedDocumentation -and -not $plan.Jobs.'template-generation') -or
        ($plan.PackageDocumentation -and -not $plan.Jobs.'package-consumption')) { throw 'Invalid quality plan responsibility set.' }
    if ($plan.ReleaseRequired -and ($plan.Mode -cne 'full' -or $plan.Tier -cne 'full' -or
        $plan.FrameworkTestSelection -cne 'all' -or $null -ne $plan.ConsumerProjects -or
        $selected.Count -ne $registered.Count -or @($jobNames | Where-Object { -not $plan.Jobs.$_ }).Count -gt 0)) {
        throw 'Publication requires full candidate validation.'
    }
    $documentation = $plan.Mode -ceq 'documentation'
    if ($documentation) {
        if ($selected.Count -ne 0 -or $plan.FrameworkTests -or $null -eq $plan.ConsumerProjects -or
            @($plan.ConsumerProjects).Count -ne 0 -or $plan.ContainerSmoke -or
            $plan.Jobs.'postgresql-e2e' -or $plan.Jobs.'oidc-e2e' -or
            $plan.Jobs.'template-generation' -ne $plan.GeneratedDocumentation -or
            $plan.Jobs.'framework-pack' -ne $plan.PackageDocumentation -or
            ($ExpectedTier -ceq 'full' -and ($plan.Event -cne 'push' -or $plan.ReleaseChannel -cnotin @('stable', 'beta') -or $plan.QualityBaseline.Verified -isnot [bool] -or -not $plan.QualityBaseline.Verified))) {
            throw 'Invalid documentation responsibilities.'
        }
    } else {
        if ($selected.Count -eq 0 -or @($jobNames | Where-Object { $_ -cnotin @('test', 'frontend-gates', 'package-consumption') -and -not $plan.Jobs.$_ }).Count -gt 0) {
            throw 'Runtime modes must retain independent dynamic jobs.'
        }
        if ($plan.Mode -ceq 'full' -and (-not $plan.FrameworkTests -or -not $plan.Jobs.'frontend-gates' -or -not $plan.Jobs.'package-consumption' -or
            $selected.Count -ne $registered.Count -or ($null -ne $plan.ConsumerProjects -and @($plan.ConsumerProjects).Count -eq 0))) {
            throw 'Full mode must retain all scenarios and framework tests.'
        }
        if ($plan.Mode -cin @('frontend', 'backend') -and ($plan.FrameworkTests -or $null -eq $plan.ConsumerProjects -or
            @($plan.ConsumerProjects).Count -ne 0 -or $plan.Jobs.'package-consumption' -ne $plan.PackageDocumentation -or
            ($plan.Mode -ceq 'frontend' -and -not $plan.Jobs.'frontend-gates'))) {
            throw 'Invalid template-stage responsibilities.'
        }
        if ($ExpectedTier -ceq 'full' -and ($plan.Mode -cne 'full' -or $plan.FrameworkTestSelection -cne 'all' -or
            $null -ne $plan.ConsumerProjects)) { throw 'Full-tier runtime plan may not narrow validation.' }
    }
    if ($plan.FrameworkTestProjects -isnot [array] -or $plan.FrameworkTestSelection -cnotin @('all', 'affected', 'none') -or
        ($plan.FrameworkTestSelection -ceq 'none') -ne (-not $plan.FrameworkTests) -or
        $plan.FrameworkTests -ne (@($plan.FrameworkTestProjects).Count -gt 0) -or
        @($plan.FrameworkTestProjects | Where-Object { $_ -isnot [string] -or $_ -cnotmatch '^framework/tests/[^\s]+\.Tests\.csproj$' }).Count -gt 0 -or
        @($plan.FrameworkTestProjects | Sort-Object -Unique -CaseSensitive).Count -ne @($plan.FrameworkTestProjects).Count) {
        throw 'Invalid framework test project list.'
    }
    if ($plan.ContainerSmoke -and $plan.Mode -cne 'full') { throw 'Container checks require full stages.' }
    $logicalGroups = @($MatrixSlices[$ExpectedTier].Keys | Where-Object {
        $logical = $_
        @(Get-TierScenarios $ExpectedTier $logical | Where-Object { $_ -cin $selected }).Count -gt 0
    })
    $count = $logicalGroups.Count
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
        $expectedMembers = @(Get-TierScenarios $ExpectedTier $logicalGroups[$index] | Where-Object { $_ -cin $selected })
        if (Compare-Object $expectedMembers @($group.Scenarios) -CaseSensitive -SyncWindow 0) {
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
