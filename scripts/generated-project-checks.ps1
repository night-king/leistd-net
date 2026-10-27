# Shared generated documentation contracts; code and runtime checks stay in the matrix.
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

function Get-Python3Command([string]$Purpose) {
    foreach ($candidate in @('python3', 'python')) {
        $found = Get-Command $candidate -ErrorAction SilentlyContinue
        if ($found -and ((& $found.Name --version 2>&1) -match 'Python 3\.')) { return $found.Name }
    }
    throw "未找到 Python 3 解释器（python3/python），无法$Purpose。"
}

# 文档引用（链接、反引号路径、npm/ng/脚本命令）按条件裁剪后的产物检查：命令只存在于未启用分支、
# 文件被裁掉而文档仍引用时，只有生成产物上看得见。规则、白名单与自检在 check-doc-references.py，
# check-all 登记其自检与源码模式。
function Assert-DocReferences([string]$ProjectRoot) {
    Invoke-External (Get-Python3Command '检查生成项目的文档引用') @((Join-Path $repoRoot 'scripts/check-doc-references.py'), '--root', $ProjectRoot)
}

# 章节锚点按生成后的标题计算：条件裁剪删掉被链接章节时，只有生成产物上看得见。
# 规则与自检夹具在 check-markdown-anchors.py，check-all 登记其自检。
function Assert-MarkdownAnchors([string]$ProjectRoot) {
    Invoke-External (Get-Python3Command '检查 Markdown 章节锚点') @((Join-Path $repoRoot 'scripts/check-markdown-anchors.py'), $ProjectRoot)
}

# 入口指针：AGENTS.md 只指向协作 Skill 与文档索引，CLAUDE.md 只导入 AGENTS.md。
function Assert-AgentEntryPoints([string]$ProjectRoot) {
    $agents = Get-Content -LiteralPath (Join-Path $ProjectRoot "AGENTS.md") -Raw -Encoding UTF8
    foreach ($pointer in @(".agents/skills/leistd-project-workflow/SKILL.md", "docs/README.md")) {
        if (-not $agents.Contains("]($pointer)")) {
            throw "Generated AGENTS.md must link $pointer"
        }
        if (-not (Test-Path -LiteralPath (Join-Path $ProjectRoot $pointer))) {
            throw "Generated AGENTS.md points to a missing file: $pointer"
        }
    }
    $claude = (Get-Content -LiteralPath (Join-Path $ProjectRoot "CLAUDE.md") -Raw -Encoding UTF8).Trim()
    if ($claude -cne "@AGENTS.md") {
        throw "Generated CLAUDE.md must contain only '@AGENTS.md', found: $claude"
    }
}

function Assert-GeneratedText([object[]]$TextFiles) {
    $residuals = $textFiles | Select-String -Pattern '<!--#(if|endif)', 'CompanyName\.ProjectName', '\bMyProject\b', 'companyname-projectname'
    if ($residuals) {
        $sample = $residuals | Select-Object -First 20 | ForEach-Object { "$($_.Path):$($_.LineNumber): $($_.Line.Trim())" }
        throw "Generated project contains template residue:`n$($sample -join "`n")"
    }

    $removedGuidanceReferences = $textFiles | Select-String -Pattern 'template/\.claude', 'agent-workflow\.md', 'api-standard\.md', 'code-standard/(common|backend|frontend)-develop\.md', 'ui-design-strategy\.md', '(?<![\w-])ui-design\.md'
    if ($removedGuidanceReferences) {
        $sample = $removedGuidanceReferences | Select-Object -First 20 | ForEach-Object { "$($_.Path):$($_.LineNumber): $($_.Line.Trim())" }
        throw "Generated project references removed guidance:`n$($sample -join "`n")"
    }

}

function Assert-GeneratedDocumentation([string]$ProjectRoot) {
    $hasFrontend = Test-Path -LiteralPath (Join-Path $ProjectRoot "frontend")
    $requiredFiles = @(
        "AGENTS.md",
        "CLAUDE.md",
        ".agents/skills/leistd-project-workflow/SKILL.md",
        ".agents/skills/leistd-project-workflow/references/bootstrap.md",
        ".agents/skills/leistd-project-workflow/references/delivery.md",
        ".agents/skills/leistd-project-workflow/references/development.md",
        ".agents/skills/leistd-project-workflow/references/quality.md",
        ".agents/skills/leistd-project-workflow/references/documentation.md",
        ".agents/skills/leistd-project-workflow/references/operations.md",
        ".agents/skills/spartan/SKILL.md",
        "docs/README.md",
        "docs/deploy/README.md",
        "docs/deploy/configuration.md",
        "docs/operations/runbooks/postgres-redis-major-upgrade.md",
        "docs/operations/runbooks/grant-new-permissions.md",
        "backend/README.md",
        "frontend/components.json"
    )
    # 规范文件集合的唯一登记处：前端专属规范随 SpaFrontend 裁剪（template.json 的 !SpaFrontend 排除项），
    # frontend-i18n.md 另随 IncludeLocalization 裁剪（!IncludeLocalization 排除项），前端词条目录与它同进退。
    $hasFrontendI18n = Test-Path -LiteralPath (Join-Path $ProjectRoot "frontend/public/i18n")
    $expectedStandards = @("api.md", "api-batch-statistics.md", "auth.md", "background-jobs.md", "coding-backend.md",
        "coding-common.md", "project-structure.md", "service-invocation.md", "settings.md", "tech-stack.md", "testing.md", "testing-containers.md")
    if ($hasFrontend) {
        $expectedStandards += @("coding-frontend.md", "frontend-ui.md", "frontend-lists.md", "frontend-forms.md",
            "frontend-navigation.md", "frontend-routing.md", "frontend-data.md", "frontend-spartan.md")
    }
    if ($hasFrontendI18n) { $expectedStandards += "frontend-i18n.md" }
    $requiredFiles += @($expectedStandards | ForEach-Object { "docs/standards/$_" })
    if (-not $hasFrontend) {
        $requiredFiles = @($requiredFiles | Where-Object { $_ -notin @(".agents/skills/spartan/SKILL.md", "frontend/components.json") })
    }
    foreach ($relativePath in $requiredFiles) {
        if (-not (Test-Path -LiteralPath (Join-Path $ProjectRoot $relativePath))) {
            throw "Generated project is missing required guidance: $relativePath"
        }
    }

    $skillRoot = Join-Path $ProjectRoot ".agents/skills"
    $skillNames = @(Get-ChildItem -LiteralPath $skillRoot -Directory | ForEach-Object Name)
    # 生成项目携带项目协作和前端 UI 两个独立入口。
    $allowedSkills = if ($hasFrontend) { @("leistd-project-workflow", "spartan") } else { @("leistd-project-workflow") }
    if (@($allowedSkills | Where-Object { $skillNames -notcontains $_ }).Count -gt 0) {
        throw "Generated project is missing a required skill in $skillRoot"
    }
    $unexpectedSkills = @($skillNames | Where-Object { $allowedSkills -notcontains $_ })
    if ($unexpectedSkills.Count -gt 0) {
        throw "Generated project has unexpected skills in $skillRoot : $($unexpectedSkills -join ', ')"
    }

    $projectReadme = Get-Content -LiteralPath (Join-Path $ProjectRoot "README.md") -Raw -Encoding UTF8
    foreach ($marker in @("ln -s ../.agents/skills .claude/skills", "mklink /D", ".agents/skills/leistd-project-workflow/SKILL.md")) {
        if (-not $projectReadme.Contains($marker)) {
            throw "Generated project README is missing AI CLI compatibility guidance: $marker"
        }
    }

    $standardsRoot = Join-Path $ProjectRoot "docs/standards"
    $actualStandards = @(Get-ChildItem -LiteralPath $standardsRoot -File -Filter "*.md" | ForEach-Object Name | Sort-Object)
    $standardDifference = Compare-Object ($expectedStandards | Sort-Object) $actualStandards
    if ($standardDifference -or @(Get-ChildItem -LiteralPath $standardsRoot -Directory).Count -gt 0) {
        throw "Generated project standards must be flat and contain only the expected files: $($expectedStandards -join ', ')"
    }

    Assert-AgentEntryPoints $ProjectRoot

    $forbiddenPaths = @(
        ".claude",
        "backend/CLAUDE.md",
        "backend/AGENTS.md",
        "docs/guides",
        "docs/standards/README.md",
        "docs/standards/agent-workflow.md",
        "docs/standards/api-standard.md",
        "docs/standards/code-standard",
        "docs/standards/test.md",
        "docs/standards/ui-design.md",
        "docs/standards/ui-design-strategy.md"
    )
    foreach ($relativePath in $forbiddenPaths) {
        if (Test-Path -LiteralPath (Join-Path $ProjectRoot $relativePath)) {
            throw "Generated project contains removed guidance path: $relativePath"
        }
    }

    $textFiles = @(Get-ChildItem -LiteralPath $ProjectRoot -Recurse -File -Filter "*.md" | Where-Object { $_.FullName -notmatch "[\\/](bin|obj|node_modules|\.git)[\\/]" })
    Assert-GeneratedText $textFiles
    $backendReadme = Get-Content -LiteralPath (Join-Path $ProjectRoot "backend/README.md") -Raw -Encoding UTF8
    if (-not $backendReadme.Contains('DbMigrator') -or
        -not $backendReadme.Contains('启动时不自动迁移')) {
        throw "Generated backend guidance must explain the independent DbMigrator boundary."
    }

    Assert-DocReferences $ProjectRoot
    Assert-MarkdownAnchors $ProjectRoot
}
