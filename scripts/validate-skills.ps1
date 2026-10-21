#!/usr/bin/env pwsh
param(
    [string]$RepositoryRoot = (Join-Path $PSScriptRoot ".."),
    [string]$SkillCreatorValidator = $env:SKILL_CREATOR_VALIDATOR,
    [string]$PythonPath
)

$ErrorActionPreference = "Stop"
$repoRoot = (Resolve-Path $RepositoryRoot).Path
$errors = [System.Collections.Generic.List[string]]::new()
$skills = [System.Collections.Generic.List[object]]::new()

function Add-ValidationError([string]$Message) {
    $errors.Add($Message)
}

function Get-RepoRelativePath([string]$Path) {
    return [IO.Path]::GetRelativePath($repoRoot, $Path).Replace('\', '/')
}

$skillSets = @(
    @{ Path = ".agents/skills"; Kind = "repository"; Expected = @("developing-leistd-framework", "developing-leistd-template", "maintaining-leistd-repository", "spartan") },
    @{ Path = "template/.agents/skills"; Kind = "template"; Expected = @("leistd-project-workflow", "spartan") },
    @{ Path = "skills"; Kind = "distribution"; Expected = @("leistd-net-framework") }
)
$requiredSkillMarkers = @{
    "developing-leistd-framework" = @("test-package-consumption.ps1", "不保留未发布兼容层")
    "maintaining-leistd-repository" = @("单独修改 framework 或 template", "多个交付面")
    "leistd-net-framework" = @("目标包未安装时", "启动真实宿主")
    "leistd-project-workflow" = @("以用户的最终意图决定交付边界", "缺失规范或文档不阻断低风险任务", "references/bootstrap.md")
    "spartan" = @("@spartan-ng/cli:info --json", "@spartan-ng/cli:healthcheck")
}

foreach ($set in $skillSets) {
    $skillsRoot = Join-Path $repoRoot $set.Path
    if (-not (Test-Path -LiteralPath $skillsRoot)) {
        Add-ValidationError "Missing skill root: $($set.Path)"
        continue
    }

    $directories = @(Get-ChildItem -LiteralPath $skillsRoot -Directory | Where-Object {
        Test-Path -LiteralPath (Join-Path $_.FullName "SKILL.md")
    })
    $actualNames = @($directories.Name | Sort-Object)
    $difference = Compare-Object ($set.Expected | Sort-Object) $actualNames
    if ($difference) {
        Add-ValidationError "Skill set differs in $($set.Path): $($difference | Out-String)"
    }

    foreach ($directory in $directories) {
        $skillName = $directory.Name
        $skillFile = Join-Path $directory.FullName "SKILL.md"
        $lines = @(Get-Content -LiteralPath $skillFile -Encoding UTF8)
        $text = $lines -join "`n"
        $skills.Add([PSCustomObject]@{
            Name = $skillName
            Kind = $set.Kind
            Directory = $directory.FullName
            File = $skillFile
            Text = $text
        })

        if ($lines.Count -lt 5 -or $lines[0] -ne "---") {
            Add-ValidationError "[$skillName] SKILL.md must start with YAML frontmatter"
            continue
        }

        $closingIndex = -1
        for ($index = 1; $index -lt $lines.Count; $index++) {
            if ($lines[$index] -eq "---") {
                $closingIndex = $index
                break
            }
        }
        if ($closingIndex -lt 3) {
            Add-ValidationError "[$skillName] frontmatter is not closed or is incomplete"
            continue
        }

        $frontmatter = @($lines[1..($closingIndex - 1)])
        $keys = @($frontmatter | ForEach-Object {
            if ($_ -match '^(?<key>[A-Za-z0-9-]+):') { $Matches.key }
        })
        $allowedFrontmatterKeys = if ($skillName -eq "spartan") {
            @("name", "description", "user-invocable", "allowed-tools")
        }
        else {
            @("name", "description")
        }
        $unexpectedKeys = @($keys | Where-Object { $_ -notin $allowedFrontmatterKeys })
        if ($unexpectedKeys.Count -gt 0) {
            Add-ValidationError "[$skillName] unsupported frontmatter keys: $($unexpectedKeys -join ', ')"
        }
        if (@($keys | Where-Object { $_ -eq "name" }).Count -ne 1 -or @($keys | Where-Object { $_ -eq "description" }).Count -ne 1) {
            Add-ValidationError "[$skillName] frontmatter must contain exactly name and description"
        }

        $nameLine = $frontmatter | Where-Object { $_ -match '^name:' } | Select-Object -First 1
        $declaredName = ($nameLine -replace '^name:\s*', '').Trim('"', "'")
        if ($declaredName -ne $skillName) {
            Add-ValidationError "[$skillName] frontmatter name must match the folder name"
        }
        if ($declaredName.Length -gt 64 -or $declaredName -notmatch '^[a-z0-9]+(?:-[a-z0-9]+)*$') {
            Add-ValidationError "[$skillName] name must be kebab-case and no longer than 64 characters"
        }

        $descriptionLine = $frontmatter | Where-Object { $_ -match '^description:' } | Select-Object -First 1
        $description = ($descriptionLine -replace '^description:\s*', '').Trim('"', "'")
        $isYamlBlockScalar = $description -in @(">", ">-", "|", "|-")
        if (-not $isYamlBlockScalar -and ([string]::IsNullOrWhiteSpace($description) -or $description.Length -gt 1024)) {
            Add-ValidationError "[$skillName] description must contain 1-1024 characters"
        }
        if (-not $isYamlBlockScalar -and $description -match '[<>]') {
            Add-ValidationError "[$skillName] description must not contain angle brackets"
        }

        $body = if ($closingIndex + 1 -lt $lines.Count) { $lines[($closingIndex + 1)..($lines.Count - 1)] -join "`n" } else { "" }
        if ([string]::IsNullOrWhiteSpace($body)) {
            Add-ValidationError "[$skillName] SKILL.md body is empty"
        }
        if ($lines.Count -gt 500) {
            Add-ValidationError "[$skillName] SKILL.md is too long ($($lines.Count) lines, expected <= 500)"
        }
        foreach ($marker in @($requiredSkillMarkers[$skillName])) {
            if (-not [string]::IsNullOrWhiteSpace($marker) -and -not $text.Contains($marker)) {
                Add-ValidationError "[$skillName] missing required workflow marker: $marker"
            }
        }

        $allowedEntries = if ($skillName -eq "spartan") {
            @(
                "SKILL.md",
                "cli.md",
                "customization.md",
                "mcp.md",
                "references",
                "registry.md",
                "rules"
            )
        }
        else {
            @("SKILL.md", "agents", "assets", "references", "scripts")
        }
        foreach ($entry in Get-ChildItem -LiteralPath $directory.FullName -Force) {
            if ($entry.Name -notin $allowedEntries) {
                Add-ValidationError "[$skillName] unexpected skill resource: $($entry.Name)"
            }
        }

        foreach ($match in [regex]::Matches($text, '\]\((?<path>(?:references|scripts|assets)/[^)#\s]+)')) {
            $resourcePath = $match.Groups['path'].Value
            if (-not (Test-Path -LiteralPath (Join-Path $directory.FullName $resourcePath))) {
                Add-ValidationError "[$skillName] missing linked resource: $resourcePath"
            }
        }

        if ($set.Kind -eq "template" -and $skillName -eq "leistd-project-workflow") {
            if ($lines.Count -gt 120) {
                Add-ValidationError "[$skillName] project workflow SKILL.md must stay concise (expected <= 120 lines)"
            }

            $expectedReferences = @("bootstrap.md", "delivery.md", "development.md", "documentation.md", "operations.md", "quality.md", "template-sync.md")
            $referencesRoot = Join-Path $directory.FullName "references"
            $actualReferences = if (Test-Path -LiteralPath $referencesRoot) {
                @(Get-ChildItem -LiteralPath $referencesRoot -File -Filter "*.md" | ForEach-Object Name | Sort-Object)
            }
            else {
                @()
            }
            $referenceDifference = Compare-Object ($expectedReferences | Sort-Object) $actualReferences
            if ($referenceDifference) {
                Add-ValidationError "[$skillName] reference set differs: $($referenceDifference | Out-String)"
            }

            foreach ($referenceFile in Get-ChildItem -LiteralPath $referencesRoot -File -Filter "*.md" -ErrorAction SilentlyContinue) {
                if (@(Get-Content -LiteralPath $referenceFile.FullName -Encoding UTF8).Count -gt 100) {
                    Add-ValidationError "[$skillName] reference must stay focused or add a contents section: $($referenceFile.Name)"
                }
            }
        }

        if ($set.Kind -ne "distribution") {
            foreach ($match in [regex]::Matches($text, '`(?<path>(?:docs|framework/docs|scripts|template/docs)/[A-Za-z0-9_./-]+\.(?:md|ps1))`')) {
                $relativePath = $match.Groups['path'].Value
                $basePath = if ($set.Kind -eq "template") { Join-Path $repoRoot "template" } else { $repoRoot }
                if (-not (Test-Path -LiteralPath (Join-Path $basePath $relativePath))) {
                    Add-ValidationError "[$skillName] missing referenced file: $relativePath"
                }
            }
        }
    }
}

$scenarioMapPath = Join-Path $repoRoot "docs/architecture/collaboration-scenarios.md"
if (-not (Test-Path -LiteralPath $scenarioMapPath)) {
    Add-ValidationError "Missing skill scenario map: docs/architecture/collaboration-scenarios.md"
}
else {
    $scenarioMapText = Get-Content -LiteralPath $scenarioMapPath -Raw -Encoding UTF8
    foreach ($marker in @("## 6. Skill 验收", ".agents/skills/developing-leistd-framework", "template/.agents/skills/leistd-project-workflow", "在模板项目中实现订单管理")) {
        if (-not $scenarioMapText.Contains($marker)) {
            Add-ValidationError "Skill scenario map is missing required marker: $marker"
        }
    }
}

# 生成项目的 AI 入口：AGENTS.md 只放协作 Skill 与文档索引两个指针，CLAUDE.md 只导入 AGENTS.md。
# 平台适配命令仍由 template/README.md 维护，这里不重复校验其内容。
$templateAgentsPath = Join-Path $repoRoot "template/AGENTS.md"
$templateClaudePath = Join-Path $repoRoot "template/CLAUDE.md"
if (-not (Test-Path -LiteralPath $templateAgentsPath) -or -not (Test-Path -LiteralPath $templateClaudePath)) {
    Add-ValidationError "Template must provide AGENTS.md and CLAUDE.md entry points"
}
else {
    $templateAgents = Get-Content -LiteralPath $templateAgentsPath -Raw -Encoding UTF8
    foreach ($pointer in @(".agents/skills/leistd-project-workflow/SKILL.md", "docs/README.md")) {
        if (-not $templateAgents.Contains("]($pointer)")) {
            Add-ValidationError "template/AGENTS.md must link $pointer"
        }
        elseif (-not (Test-Path -LiteralPath (Join-Path $repoRoot "template/$pointer"))) {
            Add-ValidationError "template/AGENTS.md points to a missing file: $pointer"
        }
    }
    if ((Get-Content -LiteralPath $templateClaudePath -Raw -Encoding UTF8).Trim() -cne "@AGENTS.md") {
        Add-ValidationError "template/CLAUDE.md must contain only '@AGENTS.md'"
    }
}

$forbiddenPaths = @(
    "template/.claude",
    "template/backend/CLAUDE.md",
    "template/backend/AGENTS.md",
    "template/docs/requirements/req-template-plan.md",
    "template/docs/requirements/registry.md",
    "template/docs/guides",
    "template/docs/modules/_template",
    "template/docs/reports",
    "template/docs/standards/README.md",
    "template/docs/standards/agent-workflow.md",
    "template/docs/standards/api-standard.md",
    "template/docs/standards/code-standard",
    "template/docs/standards/test.md",
    "template/docs/standards/ui-design.md",
    "template/docs/standards/ui-design-strategy.md",
    "template/docs/standards/document-classification.md",
    "template/docs/standards/document-naming.md"
)
foreach ($relativePath in $forbiddenPaths) {
    if (Test-Path -LiteralPath (Join-Path $repoRoot $relativePath)) {
        Add-ValidationError "Removed template mechanism still exists: $relativePath"
    }
}

# 仓库根副本用真实文件而非软链接：Windows 的 checkout 在未启用 core.symlinks 时会把软链接落成文本文件，
# 而本脚本在 docs-sync-windows 中运行；单一来源由下面的逐文件内容比对保证。
$repositorySpartanRoot = Join-Path $repoRoot ".agents/skills/spartan"
$templateSpartanRoot = Join-Path $repoRoot "template/.agents/skills/spartan"
if ((Test-Path -LiteralPath $repositorySpartanRoot) -and (Test-Path -LiteralPath $templateSpartanRoot)) {
    $repositorySpartanFiles = @(Get-ChildItem -LiteralPath $repositorySpartanRoot -Recurse -File | ForEach-Object {
        [IO.Path]::GetRelativePath($repositorySpartanRoot, $_.FullName).Replace('\', '/')
    } | Sort-Object)
    $templateSpartanFiles = @(Get-ChildItem -LiteralPath $templateSpartanRoot -Recurse -File | ForEach-Object {
        [IO.Path]::GetRelativePath($templateSpartanRoot, $_.FullName).Replace('\', '/')
    } | Sort-Object)
    $spartanFileDifference = Compare-Object $repositorySpartanFiles $templateSpartanFiles
    if ($spartanFileDifference) {
        Add-ValidationError "Repository and template Spartan skill file sets differ: $($spartanFileDifference | Out-String)"
    }
    else {
        foreach ($relativePath in $repositorySpartanFiles) {
            $repositoryHash = (Get-FileHash -LiteralPath (Join-Path $repositorySpartanRoot $relativePath) -Algorithm SHA256).Hash
            $templateHash = (Get-FileHash -LiteralPath (Join-Path $templateSpartanRoot $relativePath) -Algorithm SHA256).Hash
            if ($repositoryHash -ne $templateHash) {
                Add-ValidationError "Repository and template Spartan skill content differs: $relativePath"
            }
        }
    }
}

# Spartan 版本治理：Brain/CLI 钉版校验无条件执行；MCP 仅供仓库维护者使用，模板不得内置。
$repositoryMcpPath = Join-Path $repoRoot ".mcp.json"
$templateMcpPath = Join-Path $repoRoot "template/.mcp.json"
if (Test-Path -LiteralPath $templateMcpPath) {
    Add-ValidationError "Template must not bundle .mcp.json (generated projects rely on the spartan skill, official docs, and vendored libs/ui)"
}

$frontendPackagePath = Join-Path $repoRoot "template/frontend/package.json"
$localizedFrontendPackagePath = Join-Path $repoRoot "template/.template.config/localization/frontend/package.json"
if (-not (Test-Path -LiteralPath $frontendPackagePath) -or -not (Test-Path -LiteralPath $localizedFrontendPackagePath)) {
    Add-ValidationError "Frontend package.json files are required for Spartan version validation"
}
else {
    $frontendPackage = Get-Content -LiteralPath $frontendPackagePath -Raw -Encoding UTF8 | ConvertFrom-Json
    $localizedFrontendPackage = Get-Content -LiteralPath $localizedFrontendPackagePath -Raw -Encoding UTF8 | ConvertFrom-Json
    $brainVersion = $frontendPackage.dependencies.'@spartan-ng/brain'
    $cliVersion = $frontendPackage.devDependencies.'@spartan-ng/cli'
    $localizedBrainVersion = $localizedFrontendPackage.dependencies.'@spartan-ng/brain'
    $localizedCliVersion = $localizedFrontendPackage.devDependencies.'@spartan-ng/cli'

    foreach ($versionEntry in @(
        @{ Name = "Brain"; Value = $brainVersion },
        @{ Name = "CLI"; Value = $cliVersion }
    )) {
        if ([string]::IsNullOrWhiteSpace($versionEntry.Value) -or $versionEntry.Value -notmatch '^\d+\.\d+\.\d+$') {
            Add-ValidationError "Spartan $($versionEntry.Name) must use an exact semantic version"
        }
    }

    if ($brainVersion -ne $cliVersion) {
        Add-ValidationError "Spartan Brain and CLI versions must match"
    }
    if ($localizedBrainVersion -ne $brainVersion -or $localizedCliVersion -ne $cliVersion) {
        Add-ValidationError "Localized frontend Spartan versions must match the primary frontend"
    }

    # 仓库根 .mcp.json（维护者工具）：如存在，其钉版必须与 Brain 一致。
    if (Test-Path -LiteralPath $repositoryMcpPath) {
        $mcpConfig = Get-Content -LiteralPath $repositoryMcpPath -Raw -Encoding UTF8 | ConvertFrom-Json
        $mcpPackage = @($mcpConfig.mcpServers.spartan.args | Where-Object { $_ -like '@spartan-ng/mcp@*' }) | Select-Object -First 1
        $mcpVersion = if ($mcpPackage) { $mcpPackage -replace '^@spartan-ng/mcp@', '' } else { $null }
        if ([string]::IsNullOrWhiteSpace($mcpVersion) -or $mcpVersion -notmatch '^\d+\.\d+\.\d+$') {
            Add-ValidationError "Repository Spartan MCP must use an exact semantic version"
        }
        elseif ($mcpVersion -ne $brainVersion) {
            Add-ValidationError "Repository Spartan MCP version must match the Brain version"
        }
    }

    # Skill 文档中的 MCP 安装/配置示例必须钉版且与 Brain 一致，避免文档拉到不兼容版本。
    foreach ($mcpDoc in @(".agents/skills/spartan/mcp.md", "template/.agents/skills/spartan/mcp.md")) {
        $mcpDocPath = Join-Path $repoRoot $mcpDoc
        if (-not (Test-Path -LiteralPath $mcpDocPath)) { continue }
        $lineNumber = 0
        foreach ($line in Get-Content -LiteralPath $mcpDocPath -Encoding UTF8) {
            $lineNumber++
            if ($line -notmatch '@spartan-ng/mcp') { continue }
            # 只校验命令/配置示例，跳过正文散文提及。
            if ($line -notmatch 'npm install|npx|"args"|spartan-mcp') { continue }
            if ($line -notmatch '@spartan-ng/mcp@(\d+\.\d+\.\d+)') {
                Add-ValidationError "${mcpDoc}:${lineNumber} must pin @spartan-ng/mcp to an exact version"
            }
            elseif ($Matches[1] -ne $brainVersion) {
                Add-ValidationError "${mcpDoc}:${lineNumber} pins @spartan-ng/mcp $($Matches[1]) but Brain is $brainVersion"
            }
        }
    }
}

# 两个模板变体的 lockfile 必须解析出一致的共享依赖版本：否则「是否启用本地化」会改变
# 生产运行时（如 SignalR）与构建工具基线。出现漂移时同时重新生成两份 lockfile，不要单独重生成其中一份。
$mainLockPath = Join-Path $repoRoot "template/frontend/package-lock.json"
$localizedLockPath = Join-Path $repoRoot "template/.template.config/localization/frontend/package-lock.json"
if ((Test-Path -LiteralPath $mainLockPath) -and (Test-Path -LiteralPath $localizedLockPath)) {
    # 本地化变体额外引入 @jsverse/transloco-utils（要求 cosmiconfig ^8），迫使顶层提升不同 major；
    # 每个消费者仍各自拿到满足自身范围的版本，属结构性差异而非漂移。
    $lockDriftExceptions = @("node_modules/cosmiconfig")
    # lockfile 的根包键是空字符串，ConvertFrom-Json 必须用 -AsHashtable 才能解析。
    $mainPackages = (Get-Content -LiteralPath $mainLockPath -Raw -Encoding UTF8 | ConvertFrom-Json -AsHashtable).packages
    $localizedPackages = (Get-Content -LiteralPath $localizedLockPath -Raw -Encoding UTF8 | ConvertFrom-Json -AsHashtable).packages
    $drift = [System.Collections.Generic.List[string]]::new()
    foreach ($name in $mainPackages.Keys) {
        if ([string]::IsNullOrWhiteSpace($name) -or $lockDriftExceptions -contains $name) { continue }
        if (-not $localizedPackages.ContainsKey($name)) { continue }
        $mainVersion = $mainPackages[$name].version
        $localizedVersion = $localizedPackages[$name].version
        if ($mainVersion -and $localizedVersion -and $mainVersion -ne $localizedVersion) {
            $drift.Add("$($name -replace '^node_modules/', '') ($mainVersion vs $localizedVersion)")
        }
    }
    if ($drift.Count -gt 0) {
        $sample = ($drift | Select-Object -First 5) -join ", "
        Add-ValidationError "Template and localized lockfiles resolve $($drift.Count) shared package(s) differently; regenerate both together. Examples: $sample"
    }
}

$validatorCandidates = [System.Collections.Generic.List[string]]::new()
if ($SkillCreatorValidator) {
    $validatorCandidates.Add($SkillCreatorValidator)
}
$validatorCandidates.Add((Join-Path $repoRoot "scripts/vendor/skill-creator/quick_validate.py"))
if ($env:CODEX_HOME) {
    $validatorCandidates.Add((Join-Path $env:CODEX_HOME "skills/.system/skill-creator/scripts/quick_validate.py"))
}
if ($HOME) {
    $validatorCandidates.Add((Join-Path $HOME ".codex/skills/.system/skill-creator/scripts/quick_validate.py"))
}
$validatorPath = $validatorCandidates | Where-Object { Test-Path -LiteralPath $_ } | Select-Object -First 1

$pythonCandidates = [System.Collections.Generic.List[string]]::new()
if ($PythonPath) {
    $pythonCandidates.Add($PythonPath)
}
foreach ($candidate in @(
    (Join-Path $repoRoot ".tmp/skill-validator-venv/Scripts/python.exe"),
    (Join-Path $repoRoot ".tmp/skill-validator-venv/bin/python")
)) {
    if (Test-Path -LiteralPath $candidate) {
        $pythonCandidates.Add($candidate)
    }
}
foreach ($commandName in @("python", "python3")) {
    $command = Get-Command $commandName -ErrorAction SilentlyContinue
    if ($command) {
        $pythonCandidates.Add($command.Source)
    }
}

$officialValidation = "skipped"
if ($validatorPath) {
    $pythonWithYaml = $pythonCandidates | Where-Object {
        & $_ -c "import yaml" 2>$null
        $LASTEXITCODE -eq 0
    } | Select-Object -First 1

    if ($pythonWithYaml) {
        $previousPythonUtf8 = $env:PYTHONUTF8
        $env:PYTHONUTF8 = "1"
        try {
            foreach ($skill in $skills) {
                & $pythonWithYaml $validatorPath $skill.Directory
                if ($LASTEXITCODE -ne 0) {
                    Add-ValidationError "[$($skill.Name)] official skill-creator validation failed"
                }
            }
            $officialValidation = "passed"
        }
        finally {
            $env:PYTHONUTF8 = $previousPythonUtf8
        }
    }
    else {
        Add-ValidationError "Official skill-creator validator requires a Python environment with PyYAML"
    }
}
else {
    Add-ValidationError "Official skill-creator validator was not found; set SKILL_CREATOR_VALIDATOR"
}

if ($errors.Count -gt 0) {
    Write-Host "Skill validation failed:" -ForegroundColor Red
    foreach ($validationError in $errors) {
        Write-Host " - $validationError" -ForegroundColor Red
    }
    exit 1
}

Write-Host "Skill validation passed for $($skills.Count) skills. Official skill-creator: $officialValidation." -ForegroundColor Green
