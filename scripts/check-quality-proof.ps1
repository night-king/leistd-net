#!/usr/bin/env pwsh
param(
    [Parameter(Mandatory)][string]$ValidationPlanPath,
    [Parameter(Mandatory)][ValidateSet('package', 'documentation', 'generation', 'postgresql', 'oidc')][string]$Kind,
    [Parameter(Mandatory)][string]$ResultsPath
)
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'template-matrix-scenarios.ps1')
. (Join-Path $PSScriptRoot 'quality-validation-plan.ps1')
$tier = (Get-Content -LiteralPath $ValidationPlanPath -Raw | ConvertFrom-Json).Tier
$plan = Read-QualityValidationPlan $ValidationPlanPath $tier
$files = @(Get-ChildItem -LiteralPath $ResultsPath -Recurse -File -Filter '*.json')
# 生成与生成文档按片产出：三个形态片加一个对照片，各一份证明；其余责任恰好一份
$sharded = $Kind -cin @('generation', 'documentation')
$shards = @('0', '1', '2', 'control')
if ($files.Count -ne $(if ($sharded) { $shards.Count } else { 1 })) { throw 'Expected exactly one responsibility proof per shard.' }
$proofs = @($files | ForEach-Object { Get-Content -LiteralPath $_.FullName -Raw | ConvertFrom-Json })
foreach ($proof in $proofs) {
    if ($proof.Version -ne 1 -or $proof.Result -cne 'pass' -or $proof.CandidateSha -cne $plan.CandidateSha) { throw 'Invalid candidate proof.' }
}
$proof = $proofs[0]
function Test-Integer($value) { $value -is [long] -or $value -is [int] }
function Get-ShardProofs([string]$Responsibility) {
    $byShard = @{}
    foreach ($item in $proofs) {
        if ($item.Shard -isnot [string] -or $item.Shard -cnotin $shards -or $byShard.ContainsKey($item.Shard)) {
            throw "$Responsibility proof shards are missing, unknown or duplicated."
        }
        $byShard[$item.Shard] = $item
    }
    return $byShard
}
if ($Kind -ceq 'package') {
    $projects = @(git ls-files 'framework/components/*.csproj' 'framework/ddd-struct/*.csproj')
    if (-not $plan.Jobs.'package-consumption' -or $projects.Count -eq 0 -or $proof.Packages -ne $projects.Count -or
        $proof.Documentation -isnot [bool] -or $proof.Documentation -ne $plan.PackageDocumentation -or
        -not (Test-Integer $proof.Consumers) -or $proof.Consumers -lt 0 -or
        ($null -ne $plan.ConsumerProjects -and @($plan.ConsumerProjects).Count -eq 0 -and $proof.Consumers -ne 0) -or
        ($null -eq $plan.ConsumerProjects -and $proof.Consumers -ne $projects.Count)) { throw 'Package proof responsibility differs.' }
    foreach ($field in @('SnippetBlocks', 'SnippetProjects', 'Counterexamples')) {
        if (-not (Test-Integer $proof.$field) -or
            ($plan.PackageDocumentation -and $proof.$field -le 0) -or
            (-not $plan.PackageDocumentation -and $proof.$field -ne 0)) { throw "Invalid documentation compilation proof: $field" }
    }
} elseif ($Kind -ceq 'generation') {
    if (-not $plan.Jobs.'template-generation') { throw 'Template generation proof is incomplete.' }
    $byShard = Get-ShardProofs 'Template generation'
    # 四片同一候选源码：逐文件摘要彼此相同，且等于本检出的模板源码
    $sources = @{}
    $templateRoot = Join-Path (git rev-parse --show-toplevel) 'template'
    foreach ($file in @(Get-ChildItem -LiteralPath $templateRoot -Recurse -File -Force)) {
        $relative = [IO.Path]::GetRelativePath($templateRoot, $file.FullName).Replace('\', '/')
        if (@($relative -split '/' | Where-Object { $_ -cin @('node_modules', 'bin', 'obj', '.cache') }).Count -gt 0) { continue }
        $sources["template/$relative"] = (Get-FileHash -LiteralPath $file.FullName -Algorithm SHA256).Hash.ToLowerInvariant()
    }
    foreach ($item in $byShard.Values) {
        $declared = @($item.TemplateSourceDigests.PSObject.Properties)
        if ($item.ShapeShards -ne 3 -or $item.EffectiveShapes -ne 320 -or $declared.Count -ne $sources.Count -or
            @($declared | Where-Object { $sources[$_.Name] -cne $_.Value }).Count -gt 0) {
            throw 'Template generation shards describe different candidate sources.'
        }
    }
    # 形态片：320 个形态按序号取模恰好各出现一次，形态互不相同
    $shapes = @{}
    foreach ($shard in @('0', '1', '2')) {
        $entries = @($byShard[$shard].Shapes)
        if ($byShard[$shard].Shapes -isnot [array] -or $entries.Count -eq 0) { throw 'Template generation proof is incomplete.' }
        foreach ($entry in $entries) {
            if (-not (Test-Integer $entry.Index) -or $entry.Index -lt 0 -or $entry.Index -ge 320 -or ($entry.Index % 3) -ne [int]$shard -or
                $shapes.ContainsKey([int]$entry.Index) -or $entry.Effective -isnot [array] -or $entry.Digest -cnotmatch '^[0-9a-f]{64}$' -or
                $entry.VerifyKey.Script -cnotmatch '^[0-9a-f]{64}$' -or $entry.VerifyKey.Steps -isnot [array]) {
                throw 'Template generation shapes are missing, duplicated or misassigned.'
            }
            $shapes[[int]$entry.Index] = $entry
        }
    }
    $effective = @($shapes.Values | ForEach-Object { ConvertTo-Json -InputObject @($_.Effective) -Compress } | Sort-Object -Unique -CaseSensitive)
    if ($shapes.Count -ne 320 -or $effective.Count -ne 320) { throw 'Template generation shapes are missing, duplicated or misassigned.' }
    # 对照片：768 组互不相同的原始输入映射到同一组 320 个形态；角色等价、Ci 变体与 verify 检查
    $control = $byShard['control']
    $mapping = @($control.InputMapping)
    $mappedShapes = @($mapping | ForEach-Object { ConvertTo-Json -InputObject @($_.effective) -Compress } | Sort-Object -Unique -CaseSensitive)
    $representatives = @{}
    foreach ($entry in @($control.Representatives)) {
        $shape = $shapes[[int]$entry.Index]
        if (-not (Test-Integer $entry.Index) -or $null -eq $shape -or $entry.Digest -cne $shape.Digest -or
            (ConvertTo-Json -InputObject @($entry.Effective) -Compress) -cne (ConvertTo-Json -InputObject @($shape.Effective) -Compress)) {
            throw 'Control shard representatives differ from the generated shapes.'
        }
        $representatives[[int]$entry.Index] = $entry
    }
    $comparisons = @($control.EquivalenceComparisons)
    $roles = @($comparisons | ForEach-Object { $_.role } | Sort-Object -Unique -CaseSensitive)
    $verify = @($control.VerifyChecks)
    $checkedKeys = @($verify | ForEach-Object { ConvertTo-Json -InputObject $_.Key -Compress -Depth 4 })
    if ($control.RawInputs -ne 768 -or $control.InputMapping -isnot [array] -or $mapping.Count -ne 768 -or
        @($mapping | ForEach-Object { ConvertTo-Json -InputObject @($_.input) -Compress } | Sort-Object -Unique -CaseSensitive).Count -ne 768 -or
        $mappedShapes.Count -ne 320 -or (Compare-Object $mappedShapes $effective -CaseSensitive) -or
        $control.EquivalenceComparisons -isnot [array] -or (Compare-Object @('Identity', 'Resource', 'Standalone') $roles -CaseSensitive) -or
        @($comparisons | Where-Object { -not $representatives.ContainsKey([int]$_.representative) -or
            $_.Digest -cne $representatives[[int]$_.representative].Digest }).Count -gt 0 -or
        $control.CiVariants -isnot [array] -or @($control.CiVariants).Count -ne 4 -or
        @($control.CiVariants | Where-Object { -not $representatives.ContainsKey([int]$_.representative) }).Count -gt 0 -or
        $control.VerifyChecks -isnot [array] -or $verify.Count -eq 0 -or -not (Test-Integer $control.VerifySignatures) -or
        $verify.Count -ne $control.VerifySignatures -or
        @($verify | Where-Object { $_.Steps -isnot [array] -or @($_.Steps).Count -eq 0 -or $_.FailingStep -cnotin @($_.Steps) -or
            $_.ExitCode -ne 7 -or $_.Calls -ne ([array]::IndexOf(@($_.Steps), $_.FailingStep) + 1) -or
            (Compare-Object @($_.Key.Steps) @($_.Steps) -CaseSensitive -SyncWindow 0) }).Count -gt 0 -or
        @($shapes.Values | Where-Object { (ConvertTo-Json -InputObject $_.VerifyKey -Compress -Depth 4) -cnotin $checkedKeys }).Count -gt 0) {
        throw 'Template generation proof is incomplete.'
    }
} elseif ($Kind -cin @('postgresql', 'oidc')) {
    $job = if ($Kind -ceq 'postgresql') { 'postgresql-e2e' } else { 'oidc-e2e' }
    if (-not $plan.Jobs.$job -or -not (Test-Integer $proof.Assertions) -or $proof.Assertions -le 0) {
        throw "End-to-end proof executed no assertions: $job"
    }
} else {
    if (-not $plan.GeneratedDocumentation) { throw 'Generated documentation space is incomplete.' }
    $byShard = Get-ShardProofs 'Generated documentation'
    $indexes = @(foreach ($shard in @('0', '1', '2')) { @($byShard[$shard].ShapeIndexes) })
    if (@($indexes | Where-Object { -not (Test-Integer $_) -or $_ -lt 0 -or $_ -ge 320 }).Count -gt 0 -or
        $indexes.Count -ne 320 -or @($indexes | Sort-Object -Unique).Count -ne 320 -or
        @($byShard['control'].ShapeIndexes).Count -ne 0 -or $byShard['control'].CiVariants -ne 4 -or
        @($shards | Where-Object { $byShard[$_].EffectiveShapes -ne 320 -or -not (Test-Integer $byShard[$_].Projects) -or $byShard[$_].Projects -le 0 }).Count -gt 0 -or
        @('0', '1', '2' | Where-Object { $byShard[$_].CiVariants -ne 0 }).Count -gt 0 -or
        (($shards | ForEach-Object { $byShard[$_].Projects } | Measure-Object -Sum).Sum -lt 324)) {
        throw 'Generated documentation space is incomplete.'
    }
    foreach ($item in $byShard.Values) {
        foreach ($field in @('MarkdownFiles', 'Skills', 'FrontendMarkdownFiles', 'FormattedContents')) {
            if (-not (Test-Integer $item.$field) -or $item.$field -le 0) { throw "Empty generated documentation proof: $field" }
        }
    }
}
Write-Host "Verified $Kind proof for $($plan.CandidateSha)."
