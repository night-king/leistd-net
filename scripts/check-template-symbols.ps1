#!/usr/bin/env pwsh

<#
.SYNOPSIS
校验模板符号定义与引用的一致性。

.DESCRIPTION
模板引擎对悬空符号引用**不报错，只在生成时抛 NullReferenceException**，
且错误里只有文件名、没有行号也没有符号名——本仓库为此付过一整轮排查。
本脚本在生成之前把四类引用都查一遍：

  1. computed 的 value
  2. 参数的 isEnabled / isRequired
  3. sources.modifiers 的 condition
  4. 所有文件里的 #if / #elif 条件

另外查两个同样"静默出错"的形态：
  - 注释里写了条件指令的字面形式（引擎会当成真实指令，导致配对错位、整段代码被吞）
  - 恒真嵌套条件（#if (X) 套在 #if (X) 内，内层永真、#else 永不可达）
#>
param([string]$TemplateRoot = (Join-Path $PSScriptRoot ".." "template"))

$ErrorActionPreference = "Stop"
# 规范化：Join-Path 的三参数形式会留下 "scripts/../template" 这样的相对片段，
# 与 Get-ChildItem 返回的绝对路径长度对不上，Substring 会越界
$TemplateRoot = (Resolve-Path -LiteralPath $TemplateRoot).Path
$configPath = Join-Path $TemplateRoot ".template.config/template.json"
$config = Get-Content -LiteralPath $configPath -Raw -Encoding UTF8 | ConvertFrom-Json

$defined = @($config.symbols.PSObject.Properties.Name)
$choiceValues = @()
foreach ($symbol in $config.symbols.PSObject.Properties) {
    if ($symbol.Value.choices) { $choiceValues += $symbol.Value.choices.choice }
}
$known = $defined + $choiceValues + @('true', 'false')
$failures = @()

function Get-Identifiers([string]$Expression) {
    if (-not $Expression) { return @() }
    [regex]::Matches($Expression, '[A-Za-z_][A-Za-z0-9_]*') | ForEach-Object { $_.Value }
}

# 1 & 2：符号自身的表达式字段
foreach ($symbol in $config.symbols.PSObject.Properties) {
    foreach ($field in @('value', 'isEnabled', 'isRequired')) {
        $expression = $symbol.Value.$field
        if ($expression -isnot [string]) { continue }
        foreach ($id in Get-Identifiers $expression) {
            if ($known -notcontains $id) {
                $failures += "symbols.$($symbol.Name).$field 引用未定义符号 '$id'：$expression"
            }
        }
    }
}

# 3：文件排除条件
foreach ($modifier in $config.sources[0].modifiers) {
    foreach ($id in Get-Identifiers $modifier.condition) {
        if ($known -notcontains $id) {
            $failures += "modifier condition 引用未定义符号 '$id'：$($modifier.condition)"
        }
    }
}

# 仓库根 Directory.Build.targets 为仓库内构建模板源码定义的符号：符号改名后这里曾残留旧名，
# 使仓库内编译出的是一个不存在的条件组合（#if (X) 全灭、#if (!X) 全开），且没有任何报错
$buildTargetsPath = Join-Path $TemplateRoot ".." "Directory.Build.targets"
if (Test-Path -LiteralPath $buildTargetsPath) {
    $buildTargets = Get-Content -LiteralPath $buildTargetsPath -Raw -Encoding UTF8
    foreach ($match in [regex]::Matches($buildTargets, '<DefineConstants>([^<]*)</DefineConstants>')) {
        foreach ($name in $match.Groups[1].Value -split ';') {
            $name = $name.Trim()
            if (-not $name -or $name.StartsWith('$(')) { continue }
            if ($defined -notcontains $name) {
                $failures += "Directory.Build.targets 定义了模板中不存在的符号 '$name'"
            }
        }
    }
}

# 4 & 5 & 6：逐文件扫描
$skipDirectories = @('node_modules', 'obj', 'bin', 'dist', '.git')
$files = Get-ChildItem -LiteralPath $TemplateRoot -Recurse -File |
    Where-Object { $parts = $_.FullName -split '[\\/]'; -not ($parts | Where-Object { $skipDirectories -contains $_ }) }

foreach ($file in $files) {
    $lines = try { Get-Content -LiteralPath $file.FullName -Encoding UTF8 } catch { continue }
    $relative = $file.FullName.Substring($TemplateRoot.Length).TrimStart('/', '\')
    $stack = New-Object System.Collections.ArrayList

    for ($i = 0; $i -lt $lines.Count; $i++) {
        $line = $lines[$i]
        $lineNumber = $i + 1

        $directive = [regex]::Match($line, '^\s*(?://|<!--)?\s*#(if|elif|else|endif)\b(?:\s*\(([^)]*)\))?')
        if (-not $directive.Success) {
            # 注释里写了条件指令的字面形式：引擎支持 //#if，会把它当成真实指令
            if ($line -match '^\s*(?://|<!--).*[^A-Za-z]#(if|elif|else|endif)\b') {
                $failures += "${relative}:${lineNumber} 注释里出现条件指令字面形式，模板引擎会当成真实指令：$($line.Trim())"
            }
            continue
        }

        $kind = $directive.Groups[1].Value
        $condition = $directive.Groups[2].Value.Trim()

        if ($kind -eq 'if' -or $kind -eq 'elif') {
            foreach ($id in Get-Identifiers $condition) {
                if ($known -notcontains $id) {
                    $failures += "${relative}:${lineNumber} 条件引用未定义符号 '$id'：$condition"
                }
            }
        }

        switch ($kind) {
            'if' {
                foreach ($outer in $stack) {
                    if ($outer -and ($condition -eq $outer -or
                        $condition.StartsWith("$outer ||") -or $condition.EndsWith("|| $outer"))) {
                        $failures += "${relative}:${lineNumber} 恒真嵌套：内层 ($condition) 被外层 ($outer) 蕴含"
                    }
                }
                [void]$stack.Add($condition)
            }
            'elif' {
                if ($stack.Count -gt 0 -and $stack[$stack.Count - 1] -eq $condition) {
                    $failures += "${relative}:${lineNumber} #elif 与上一个 #if 条件相同，该分支永不可达"
                }
            }
            'endif' {
                if ($stack.Count -gt 0) { $stack.RemoveAt($stack.Count - 1) }
                else { $failures += "${relative}:${lineNumber} #endif 无匹配的 #if" }
            }
        }
    }

    if ($stack.Count -gt 0) {
        $failures += "${relative}: 文件结束时仍有 $($stack.Count) 个未闭合的 #if"
    }
}

if ($failures.Count -gt 0) {
    Write-Host "❌ 模板符号校验失败（共 $($failures.Count) 项）：" -ForegroundColor Red
    $failures | ForEach-Object { Write-Host "  - $_" }
    Write-Host ""
    Write-Host "这些问题在生成时只表现为 'Object reference not set' 或静默少生成代码，因此必须在生成前拦住。"
    exit 1
}

Write-Host "✅ 模板符号校验通过（$($defined.Count) 个符号，$($files.Count) 个文件）。" -ForegroundColor Green
