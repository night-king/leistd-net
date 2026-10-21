#!/usr/bin/env pwsh
# 文档同步闸门（P7 / 引用链路 L3+L4 一致性）
# 校验：每个 framework/components/<家族>/ 都有对应 docs/components/<家族>.md 且已在组件索引登记；
#       ddd-struct 有层文档且已登记。任一缺失即失败，防止组件与文档静默漂移。
# 用法：pwsh framework/build/check-docs-sync.ps1  （从仓库根运行）
$ErrorActionPreference = 'Stop'
$fail = @()

$componentsDir = 'framework/components'
$componentDocs = 'framework/docs/components'
$componentIndex = Join-Path $componentDocs 'README.md'
$indexText = Get-Content $componentIndex -Raw

foreach ($dir in Get-ChildItem -Directory $componentsDir) {
    $fam = $dir.Name
    $docPath = Join-Path $componentDocs "$fam.md"
    if (-not (Test-Path $docPath)) {
        $fail += "组件 '$fam' 缺少文档 docs/components/$fam.md"
    }
    # 索引登记：形如 (./<fam>.md) 的链接
    if ($indexText -notmatch [regex]::Escape("(./$fam.md)")) {
        $fail += "组件 '$fam' 未在 docs/components/README.md 索引登记"
    }
}

# 总览开头的计数：它是一句会随新增家族静默变错的事实陈述，而读者拿它当权威。
# 家族目录与 csproj 就在手上，顺手钉住，不另建闸门。
$familyCount = (Get-ChildItem -Directory $componentsDir).Count
$componentPackageCount = (Get-ChildItem -Recurse -Filter '*.csproj' $componentsDir).Count
$dddPackageCount = if (Test-Path 'framework/ddd-struct') {
    (Get-ChildItem -Recurse -Filter '*.csproj' 'framework/ddd-struct').Count
} else { 0 }

$countMatch = [regex]::Match($indexText, '共有 \*\*(\d+) 个能力分组、(\d+) 个 NuGet 包\*\*')
if (-not $countMatch.Success) {
    $fail += "docs/components/README.md 找不到「共有 **N 个能力分组、M 个 NuGet 包**」这句计数"
} else {
    $statedFamilies = [int]$countMatch.Groups[1].Value
    $statedPackages = [int]$countMatch.Groups[2].Value
    if ($statedFamilies -ne $familyCount) {
        $fail += "组件索引写「$statedFamilies 个能力分组」，实际 $familyCount 个"
    }
    if ($statedPackages -ne $componentPackageCount) {
        $fail += "组件索引写「$statedPackages 个 NuGet 包」，实际 $componentPackageCount 个"
    }
}

$dddMatch = [regex]::Match($indexText, 'DDD 四层基座的 (\d+) 个包')
if ($dddMatch.Success -and [int]$dddMatch.Groups[1].Value -ne $dddPackageCount) {
    $fail += "组件索引写「DDD 四层基座的 $($dddMatch.Groups[1].Value) 个包」，实际 $dddPackageCount 个"
}

# DDD 基座
if (Test-Path 'framework/ddd-struct') {
    if (-not (Test-Path 'framework/docs/ddd-struct/ddd-struct.md')) {
        $fail += "ddd-struct 缺少层文档 docs/ddd-struct/ddd-struct.md"
    }
    $dddIndex = Get-Content 'framework/docs/ddd-struct/README.md' -Raw
    if ($dddIndex -notmatch 'ddd-struct\.md') {
        $fail += "ddd-struct 文档未在 docs/ddd-struct/README.md 索引登记"
    }
}

if ($fail.Count -gt 0) {
    Write-Host "❌ 文档同步闸门失败：" -ForegroundColor Red
    $fail | ForEach-Object { Write-Host "  - $_" }
    exit 1
}
Write-Host "✅ 文档同步闸门通过：所有组件家族与 ddd-struct 均有文档且已索引。" -ForegroundColor Green
