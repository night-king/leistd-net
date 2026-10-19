#!/usr/bin/env pwsh

param(
    [string]$FeedPath,
    [string[]]$PackageIds = @(),
    # CI 输入计划只裁剪消费构建；全部包内容、包集完整性及候选依赖仍先核对。
    [string]$ValidationPlanPath,
    [ValidateSet("Debug", "Release")]
    [string]$Configuration = "Release"
)

$ErrorActionPreference = "Stop"
$repoRoot = (Resolve-Path (Join-Path $PSScriptRoot "../..")).Path
. (Join-Path $PSScriptRoot "candidate-feed.ps1")
$tempRoot = Join-Path $repoRoot ".tmp"
$feedRoot = if ($FeedPath) {
    [IO.Path]::GetFullPath($FeedPath, $repoRoot)
}
else {
    Join-Path $tempRoot "local-feed"
}
$consumerRoot = Join-Path $tempRoot "package-consumer"
$projectsRoot = Join-Path $consumerRoot "projects"
$packagesRoot = Join-Path $consumerRoot "packages"
$nugetConfigPath = Join-Path $consumerRoot "NuGet.Config"

function Assert-TempPath([string]$Path) {
    $resolvedTemp = [IO.Path]::GetFullPath($tempRoot).TrimEnd([IO.Path]::DirectorySeparatorChar) + [IO.Path]::DirectorySeparatorChar
    $resolvedPath = [IO.Path]::GetFullPath($Path)
    if (-not $resolvedPath.StartsWith($resolvedTemp, [StringComparison]::OrdinalIgnoreCase)) {
        throw "Refusing to modify path outside .tmp: $resolvedPath"
    }
}

function Reset-Directory([string]$Path) {
    Assert-TempPath $Path
    if (Test-Path -LiteralPath $Path) {
        Remove-Item -LiteralPath $Path -Recurse -Force
    }
    New-Item -ItemType Directory -Path $Path | Out-Null
}

function Invoke-External([string]$Command, [string[]]$Arguments, [string]$WorkingDirectory = $repoRoot) {
    Write-Host "> $Command $($Arguments -join ' ')" -ForegroundColor DarkGray
    Push-Location $WorkingDirectory
    try {
        & $Command @Arguments
        if ($LASTEXITCODE -ne 0) {
            throw "Command failed with exit code ${LASTEXITCODE}: $Command $($Arguments -join ' ')"
        }
    }
    finally {
        Pop-Location
    }
}

$script:docStats = @{ Blocks = 0; Projects = 0; Counterexamples = 0 }
function Write-PackageProof {
    if (-not $ValidationPlanPath) { return }
    [ordered]@{
        Version = 1; CandidateSha = $validationPlan.CandidateSha; Result = 'pass'
        Packages = $allPackageCount; Consumers = $packages.Count
        Documentation = $validationPlan.PackageDocumentation
        SnippetBlocks = $script:docStats.Blocks; SnippetProjects = $script:docStats.Projects
        Counterexamples = $script:docStats.Counterexamples
    } | ConvertTo-Json -Compress | Set-Content (Join-Path $consumerRoot 'package-results.json') -Encoding utf8
}

function Get-PackageMetadata([IO.FileInfo]$PackageFile) {
    $archive = [IO.Compression.ZipFile]::OpenRead($PackageFile.FullName)
    try {
        $nuspecEntry = @($archive.Entries | Where-Object { $_.FullName.EndsWith(".nuspec", [StringComparison]::OrdinalIgnoreCase) })
        if ($nuspecEntry.Count -ne 1) {
            throw "$($PackageFile.Name) must contain exactly one nuspec file."
        }

        $reader = [IO.StreamReader]::new($nuspecEntry[0].Open())
        try {
            [xml]$nuspec = $reader.ReadToEnd()
        }
        finally {
            $reader.Dispose()
        }

        $metadataNode = $nuspec.SelectSingleNode("/*[local-name()='package']/*[local-name()='metadata']")
        $id = $metadataNode.SelectSingleNode("*[local-name()='id']")?.InnerText
        $version = $metadataNode.SelectSingleNode("*[local-name()='version']")?.InnerText
        if ([string]::IsNullOrWhiteSpace($id) -or [string]::IsNullOrWhiteSpace($version)) {
            throw "$($PackageFile.Name) has an invalid package id or version."
        }

        $entryNames = @($archive.Entries | ForEach-Object { $_.FullName })
        $assemblies = @($entryNames | Where-Object { $_ -match '^lib/[^/]+/[^/]+\.dll$' })
        if ($assemblies.Count -eq 0) {
            throw "$id $version does not contain a lib assembly."
        }

        foreach ($assembly in $assemblies) {
            $xmlPath = $assembly.Substring(0, $assembly.Length - 4) + ".xml"
            if ($xmlPath -notin $entryNames) {
                throw "$id $version is missing XML documentation for $assembly."
            }
            $reader = [IO.StreamReader]::new($archive.GetEntry($xmlPath).Open())
            try {
                [xml]$xmlDocumentation = $reader.ReadToEnd()
                if ($xmlDocumentation.DocumentElement.Name -cne 'doc') { throw "Invalid package XML: $xmlPath" }
            } finally { $reader.Dispose() }
        }

        if (-not ($entryNames | Where-Object { $_ -match '^docs/[^/]+\.md$' })) {
            throw "$id $version does not contain a family document under docs/."
        }
        if ("NuGet.md" -notin $entryNames) {
            throw "$id $version does not contain NuGet.md."
        }

        $sourceProject = @($sourceProjects | Where-Object BaseName -ceq $id)
        if ($sourceProject.Count -ne 1) { throw "Package has no unique source project: $id" }
        $family = Split-Path (Split-Path $sourceProject[0].DirectoryName) -Leaf
        $expectedDocuments = @{ 'NuGet.md' = (Join-Path $repoRoot 'framework/NuGet.md') }
        $familyDoc = Join-Path $repoRoot "framework/docs/components/$family.md"
        if (Test-Path $familyDoc) { $expectedDocuments["docs/$family.md"] = $familyDoc }
        if ($family -ceq 'ddd-struct') { $expectedDocuments['docs/ddd-struct.md'] = Join-Path $repoRoot 'framework/docs/ddd-struct/ddd-struct.md' }
        if (Compare-Object @($expectedDocuments.Keys | Sort-Object) @($entryNames | Where-Object { $_ -ceq 'NuGet.md' -or $_ -cmatch '^docs/.*\.md$' } | Sort-Object) -CaseSensitive) {
            throw "Package Markdown set differs from current Pack sources: $id"
        }
        foreach ($entry in $expectedDocuments.GetEnumerator()) {
            $stream = $archive.GetEntry($entry.Key).Open()
            try { $hash = [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData($stream)) }
            finally { $stream.Dispose() }
            if ($hash -cne (Get-FileHash -LiteralPath $entry.Value -Algorithm SHA256).Hash) {
                throw "Package Markdown differs from candidate source: $id / $($entry.Key)"
            }
        }

        return [PSCustomObject]@{
            Id = $id
            Version = $version
            File = $PackageFile.FullName
            AssemblyCount = $assemblies.Count
            Dependencies = @($metadataNode.SelectNodes(".//*[local-name()='dependency']") |
                ForEach-Object { $_.GetAttribute('id') } | Where-Object { $_ -clike 'Leistd.*' } | Sort-Object -Unique)
        }
    }
    finally {
        $archive.Dispose()
    }
}

function New-SnippetProject([string]$Directory, [object]$PackageSelection, [object[]]$ExtraPackages) {
    $ids = if ($PackageSelection -is [string] -and $PackageSelection -eq '*') { @($feedPackages.Id) } else { @($PackageSelection) }
    $references = foreach ($id in ($ids | Sort-Object)) {
        $package = @($feedPackages | Where-Object Id -ceq $id)
        if ($package.Count -ne 1) { throw "Doc snippet project references a package missing from the feed: $id" }
        '    <PackageReference Include="{0}" Version="{1}" />' -f [Security.SecurityElement]::Escape($id), [Security.SecurityElement]::Escape($package[0].Version)
    }
    $references += foreach ($extra in $ExtraPackages) {
        '    <PackageReference Include="{0}" Version="{1}" />' -f [Security.SecurityElement]::Escape($extra.id), [Security.SecurityElement]::Escape($extra.version)
    }
    # Web SDK: snippets are host code and get its implicit usings, nothing more.
    # CS4014 (missing await) and CS0618 (obsolete API) are defects in a documented example.
    $project = @"
<Project Sdk="Microsoft.NET.Sdk.Web">
  <PropertyGroup>
    <TargetFramework>net10.0</TargetFramework>
    <OutputType>Library</OutputType>
    <Nullable>enable</Nullable>
    <ImplicitUsings>enable</ImplicitUsings>
    <!-- #line points at Markdown (and at virtual counterexample paths); no PDB must open them. -->
    <DebugType>none</DebugType>
    <WarningsAsErrors>`$(WarningsAsErrors);CS4014;CS0618</WarningsAsErrors>
  </PropertyGroup>
  <ItemGroup>
$($references -join "`n")
  </ItemGroup>
</Project>
"@
    $path = Join-Path $Directory "DocSnippets.csproj"
    [IO.File]::WriteAllText($path, $project, [Text.UTF8Encoding]::new($false))
    return $path
}

function New-SnippetSolution([string]$Path, [string[]]$Projects) {
    $document = [Xml.XmlDocument]::new()
    $root = $document.CreateElement("Solution")
    $null = $document.AppendChild($root)
    foreach ($project in $Projects) {
        $relative = [IO.Path]::GetRelativePath((Split-Path $Path), $project).Replace('\', '/')
        $folder = $document.CreateElement("Folder")
        $folder.SetAttribute("Name", "/$(Split-Path (Split-Path $project) -Leaf)/")
        $element = $document.CreateElement("Project")
        $element.SetAttribute("Path", $relative)
        $null = $folder.AppendChild($element)
        $null = $root.AppendChild($folder)
    }
    $document.Save($Path)
}

# Component-doc examples in `## 注册` / `## 使用` must compile against the packed
# packages (development-guide §5.1/§6.3). The extractor also emits compile-time
# counterexamples (original P4-5..P4-7 snippets, an injected missing using);
# each must fail with its expected diagnostic, otherwise the gate proves nothing.
function Invoke-DocSnippetCompilation {
    $python = $null
    foreach ($candidate in @("python3", "python")) {
        $command = Get-Command $candidate -ErrorAction SilentlyContinue
        if ($command -and ((& $command.Source --version 2>&1) -match '^Python 3\.')) { $python = $command.Source; break }
    }
    if (-not $python) { throw "Python 3 (python3/python) is required to extract doc snippets." }

    $snippetRoot = Join-Path $consumerRoot "doc-snippets"
    Assert-TempPath $snippetRoot
    Invoke-External $python @((Join-Path $repoRoot "scripts/extract-doc-snippets.py"), "--output", $snippetRoot)
    $manifest = Get-Content -LiteralPath (Join-Path $snippetRoot "manifest.json") -Raw | ConvertFrom-Json

    $passing = [System.Collections.Generic.List[string]]::new()
    $failing = [System.Collections.Generic.List[object]]::new()
    foreach ($entry in $manifest.projects) {
        $path = New-SnippetProject (Join-Path $snippetRoot $entry.name) $entry.packages @($manifest.extraPackages)
        if ($entry.expect -eq "pass") { $passing.Add($path) } else { $failing.Add([PSCustomObject]@{ Entry = $entry; Path = $path }) }
    }

    $passSolution = Join-Path $snippetRoot "DocSnippets.slnx"
    New-SnippetSolution $passSolution $passing
    Invoke-External "dotnet" @("restore", $passSolution, "--configfile", $nugetConfigPath) $snippetRoot
    Invoke-External "dotnet" @("build", $passSolution, "-c", $Configuration, "--no-restore", "-maxcpucount:4") $snippetRoot

    $failSolution = Join-Path $snippetRoot "SelfTest.slnx"
    New-SnippetSolution $failSolution @($failing.Path)
    Invoke-External "dotnet" @("restore", $failSolution, "--configfile", $nugetConfigPath) $snippetRoot
    Assert-CandidatePackages $packagesRoot $candidateFeed
    foreach ($item in $failing) {
        Write-Host "> dotnet build $($item.Path) (expected to fail)" -ForegroundColor DarkGray
        $output = (& dotnet build $item.Path -c $Configuration --no-restore 2>&1 | Out-String)
        if ($LASTEXITCODE -eq 0) { throw "Doc snippet counterexample $($item.Entry.name) compiled; the snippet gate no longer detects it." }
        foreach ($diagnostic in $item.Entry.diagnostics) {
            $pattern = '{0}\(\d+,\d+\): error (?:{1}):' -f [regex]::Escape($diagnostic.path), (($diagnostic.codes | ForEach-Object { [regex]::Escape($_) }) -join '|')
            if ($output -notmatch $pattern) {
                Write-Host $output
                throw "Doc snippet counterexample $($item.Entry.name) failed without $($diagnostic.codes -join '/') at $($diagnostic.path)."
            }
        }
        Write-Host "Counterexample $($item.Entry.name) rejected with $($diagnostic.codes -join '/')." -ForegroundColor DarkGray
    }
    if ($manifest.snippets -le 0 -or $passing.Count -le 0 -or $failing.Count -le 0) { throw 'Documentation compilation produced no proof.' }
    $script:docStats = @{ Blocks = $manifest.snippets; Projects = $passing.Count; Counterexamples = $failing.Count }
    Write-Host "Doc snippets compiled: $($manifest.snippets) block(s) in $($passing.Count) project(s), $(@($manifest.exempt).Count) exempt; $($failing.Count) counterexample(s) rejected." -ForegroundColor Green
}

if (-not (Test-Path -LiteralPath $feedRoot -PathType Container)) {
    throw "Package feed does not exist: $feedRoot. Run dotnet pack first."
}

$packageFiles = @(Get-ChildItem -LiteralPath $feedRoot -File -Filter "*.nupkg")
if ($packageFiles.Count -eq 0) {
    throw "No nupkg files were found in $feedRoot. Run dotnet pack first."
}
# 消费的候选版本取自 candidate.json；restore 后的断言核对每个 Leistd.* 都是这个版本、来自本包源
$candidateFeed = Read-CandidateManifest $feedRoot

$sourceProjects = @(Get-ChildItem -LiteralPath (Join-Path $repoRoot 'framework') -Recurse -File -Filter 'Leistd.*.csproj' |
    Where-Object { $_.FullName -notmatch '[\\/](obj|bin|tests)[\\/]' })
$packages = @($packageFiles | ForEach-Object { Get-PackageMetadata $_ })
$allPackageCount = $packages.Count
$feedPackages = $packages
$foreignVersions = @($packages | Where-Object Version -cne $candidateFeed.CandidateVersion)
if ($foreignVersions.Count -gt 0) {
    throw "Package versions differ from candidate $($candidateFeed.CandidateVersion): $(($foreignVersions | ForEach-Object { "$($_.Id) $($_.Version)" }) -join ', ')"
}

# feed 里不得存在没有对应源码项目的包。持久化 feed 会保留已被删除的组件——
# 消费它等于在验证一个仓库里已经不存在的东西，而它带来的告警（例如已删组件的
# 传递依赖漏洞）会被当成当前代码的问题去排查。
$projectIds = @(
    Get-ChildItem -LiteralPath (Join-Path $repoRoot "framework") -Recurse -File -Filter "Leistd.*.csproj" |
        Where-Object { $_.FullName -notmatch "[\\/](obj|bin)[\\/]" -and $_.FullName -notmatch "[\\/]tests[\\/]" } |
        ForEach-Object { [IO.Path]::GetFileNameWithoutExtension($_.Name) }
)
$orphaned = @($packages | Where-Object { $_.Id -notin $projectIds })
if ($orphaned.Count -gt 0) {
    throw ("The feed contains packages with no source project: {0}. " -f (($orphaned.Id | Sort-Object) -join ', ')) +
        "This happens when a component is removed but a persistent feed keeps its old .nupkg. " +
        "Delete the feed directory and pack again, or pass -FeedPath pointing at a per-run feed."
}

# 全量消费必须收到当前源码的完整包集；分片下载漏包不能退回已发布旧包而假绿。
# 人工 -PackageIds 仍可缩小消费范围，但 CI 不传该参数。
if ($PackageIds.Count -eq 0) {
    $missing = @($projectIds | Where-Object { $_ -notin $packages.Id })
    if ($missing.Count -gt 0) {
        throw "The full feed is missing source packages: $($missing -join ', ')"
    }
}

$duplicates = @($packages | Group-Object Id | Where-Object Count -gt 1)
if ($duplicates.Count -gt 0) {
    throw "The feed must contain one version per package id. Duplicates: $($duplicates.Name -join ', ')"
}

if ($PackageIds.Count -gt 0) {
    $requestedIds = @($PackageIds | Where-Object { -not [string]::IsNullOrWhiteSpace($_) } | Sort-Object -Unique)
    $missingIds = @($requestedIds | Where-Object { $_ -notin $packages.Id })
    if ($missingIds.Count -gt 0) {
        throw "Requested packages were not found in the feed: $($missingIds -join ', ')"
    }
    $packages = @($packages | Where-Object { $_.Id -in $requestedIds })
}

if ($ValidationPlanPath) {
    if ($PackageIds.Count -gt 0) { throw 'Validation plan cannot combine with manual PackageIds.' }
    . (Join-Path $repoRoot 'scripts/template-matrix-scenarios.ps1')
    . (Join-Path $repoRoot 'scripts/quality-validation-plan.ps1')
    $declaredTier = (Get-Content -LiteralPath $ValidationPlanPath -Raw | ConvertFrom-Json).Tier
    if ($declaredTier -cnotin @('core', 'full')) { throw 'Invalid consumer plan tier.' }
    $validationPlan = Read-QualityValidationPlan $ValidationPlanPath $declaredTier
    if (-not $validationPlan.Jobs.'package-consumption') { throw 'Plan has no package consumer responsibility.' }
    foreach ($package in $packages) {
        $missingDependencies = @($package.Dependencies | Where-Object { $_ -cnotin $packages.Id })
        if ($missingDependencies.Count -gt 0) { throw "Candidate dependency missing: $($missingDependencies -join ', ')" }
    }
    if ($null -ne $validationPlan.ConsumerProjects) {
        $selectedIds = @($validationPlan.ConsumerProjects)
        if (@($selectedIds | Where-Object { $_ -cnotin $packages.Id }).Count -gt 0) { throw 'Unknown consumer seed package.' }
        # Actual packed dependencies, across all target framework groups. This
        # selects restore/build consumers only, never runtime/DI/PG/OIDC tests.
        do {
            $previousCount = $selectedIds.Count
            $selectedIds = @($packages | Where-Object {
                $_.Id -cin $selectedIds -or @($_.Dependencies | Where-Object { $_ -cin $selectedIds }).Count -gt 0
            } | ForEach-Object Id)
        } while ($selectedIds.Count -ne $previousCount)
        $packages = @($packages | Where-Object { $_.Id -cin $selectedIds })
    }
}

Write-Host "Verified contents of $allPackageCount packages; selected $($packages.Count) isolated consumers."

Reset-Directory $consumerRoot
New-Item -ItemType Directory -Path $projectsRoot, $packagesRoot -Force | Out-Null

$escapedFeedRoot = [Security.SecurityElement]::Escape($feedRoot)
$escapedPackagesRoot = [Security.SecurityElement]::Escape($packagesRoot)
$nugetConfig = @"
<?xml version="1.0" encoding="utf-8"?>
<configuration>
  <config>
    <add key="globalPackagesFolder" value="$escapedPackagesRoot" />
  </config>
  <packageSources>
    <clear />
    <add key="local-feed" value="$escapedFeedRoot" />
    <add key="nuget.org" value="https://api.nuget.org/v3/index.json" />
  </packageSources>
  <packageSourceMapping>
    <packageSource key="local-feed">
      <package pattern="Leistd.*" />
    </packageSource>
    <packageSource key="nuget.org">
      <package pattern="*" />
    </packageSource>
  </packageSourceMapping>
</configuration>
"@
[IO.File]::WriteAllText($nugetConfigPath, $nugetConfig, [Text.UTF8Encoding]::new($false))

$checkSnippets = -not $ValidationPlanPath -or $validationPlan.PackageDocumentation
if ($packages.Count -eq 0) {
    if (-not $ValidationPlanPath) { throw 'No package consumers selected without a validation plan.' }
    Write-Host 'Consumer build not applicable; all candidate package contents verified.'
    if ($checkSnippets) { Invoke-DocSnippetCompilation }
    Write-PackageProof
    return
}

$results = [System.Collections.Generic.List[object]]::new()
$solution = [Xml.XmlDocument]::new()
$solutionRoot = $solution.CreateElement("Solution")
$null = $solution.AppendChild($solutionRoot)
foreach ($package in $packages | Sort-Object Id) {
    $projectRoot = Join-Path $projectsRoot $package.Id.ToLowerInvariant()
    New-Item -ItemType Directory -Path $projectRoot -Force | Out-Null
    $projectPath = Join-Path $projectRoot "PackageConsumer.csproj"
    $escapedId = [Security.SecurityElement]::Escape($package.Id)
    $escapedVersion = [Security.SecurityElement]::Escape($package.Version)
    $project = @"
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>net10.0</TargetFramework>
    <Nullable>enable</Nullable>
  </PropertyGroup>
  <ItemGroup>
    <PackageReference Include="$escapedId" Version="$escapedVersion" />
  </ItemGroup>
</Project>
"@
    [IO.File]::WriteAllText($projectPath, $project, [Text.UTF8Encoding]::new($false))

    # Each package keeps its own restore graph. Distinct solution folders permit
    # the identical PackageConsumer project names without combining references.
    $folder = $solution.CreateElement("Folder")
    $folder.SetAttribute("Name", "/$($package.Id)/")
    $projectElement = $solution.CreateElement("Project")
    $projectElement.SetAttribute("Path", "projects/$($package.Id.ToLowerInvariant())/PackageConsumer.csproj")
    $null = $folder.AppendChild($projectElement)
    $null = $solutionRoot.AppendChild($folder)

    $results.Add([PSCustomObject]@{
        Package = $package.Id
        Version = $package.Version
        Assemblies = $package.AssemblyCount
        Restore = "pass"
        Build = "pass"
    })
}

$solutionPath = Join-Path $consumerRoot "Consumers.slnx"
$solution.Save($solutionPath)
Invoke-External "dotnet" @("restore", $solutionPath, "--configfile", $nugetConfigPath) $consumerRoot
Assert-CandidatePackages $packagesRoot $candidateFeed
# Bound parallel compilation on small runners; any project failure fails the batch.
Invoke-External "dotnet" @("build", $solutionPath, "-c", $Configuration, "--no-restore", "-maxcpucount:4") $consumerRoot

$results | Format-Table -AutoSize
Write-Host "Package consumption passed for $($results.Count) package(s)." -ForegroundColor Green

# Snippets reference every family, so they need the complete feed; manual -PackageIds may point at a partial one.
if ($PackageIds.Count -gt 0) {
    Write-Host "Doc snippet compilation not run: -PackageIds narrows the feed." -ForegroundColor Yellow
}
elseif ($checkSnippets) {
    Invoke-DocSnippetCompilation
}
Write-PackageProof
