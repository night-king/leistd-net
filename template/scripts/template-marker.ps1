# 来源标记 .leistd/template.json 的读取与按标记重建；需要重建同版本基线的脚本 dot-source 本文件。
#
# 标记由 dotnet new 在生成时写入，记录：
#   package.id / package.version / package.sourceSha  生成所用的模板包、版本与源提交
#   sdkVersion                                         生成时 dotnet CLI 的 SDK 版本
#   name                                               生成名称（dotnet new -n）
#   parameters                                         全部模板参数的取值（键为参数名，布尔为 JSON 布尔）
#   userSecretsIds                                     各项目实际生成的 UserSecretsId（键为项目文件相对路径）
#
# 同一模板版本、相同输入重建时，除生成时随机产生的 UserSecretsId 外与原产物逐字节相同；
# New-TemplateRebuild 把重建产物里的 UserSecretsId 换回标记记录的值，之后两棵树应零差异。

$script:TemplateMarkerPath = '.leistd/template.json'
$script:TemplateShortName = 'fullstack-app'

# Relative 缺省为来源标记；同步待完成时目标版本的标记另存一份，同样按本格式读取
function Read-TemplateMarker([string]$ProjectRoot, [string]$Relative = $script:TemplateMarkerPath) {
    $path = Join-Path $ProjectRoot $Relative
    if (-not (Test-Path -LiteralPath $path -PathType Leaf)) {
        throw "缺少来源标记 ${Relative}：该项目不是由模板包生成，或标记已被删除。"
    }
    $marker = Get-Content -LiteralPath $path -Raw -Encoding UTF8 | ConvertFrom-Json -AsHashtable
    $problems = @()
    if ($marker.schemaVersion -ne 1) { $problems += "schemaVersion 应为 1，实际为 '$($marker.schemaVersion)'" }
    foreach ($field in 'id', 'version', 'sourceSha') {
        if ($marker.package -isnot [hashtable] -or $marker.package[$field] -isnot [string] -or -not $marker.package[$field]) {
            $problems += "缺少 package.$field"
        }
    }
    if ($marker.sdkVersion -isnot [string]) { $problems += '缺少 sdkVersion' }
    if ($marker.name -isnot [string] -or -not $marker.name) { $problems += '缺少 name' }
    if ($marker.parameters -isnot [hashtable] -or $marker.parameters.Count -eq 0) { $problems += '缺少 parameters' }
    else {
        foreach ($entry in $marker.parameters.GetEnumerator()) {
            if ($entry.Value -isnot [string] -and $entry.Value -isnot [bool]) { $problems += "参数 $($entry.Key) 的取值不是字符串或布尔" }
        }
    }
    if ($marker.userSecretsIds -isnot [hashtable] -or $marker.userSecretsIds.Count -eq 0) { $problems += '缺少 userSecretsIds' }
    else {
        foreach ($entry in $marker.userSecretsIds.GetEnumerator()) {
            if ($entry.Value -notmatch '^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$') { $problems += "$($entry.Key) 的 UserSecretsId 不是 GUID" }
        }
    }
    if ($problems) { throw "来源标记 $path 无效：`n  $($problems -join "`n  ")" }
    return $marker
}

# dotnet new 的参数：选项名是参数名的短横线小写形式（ServiceRole → --service-role），布尔取 true/false
function Get-TemplateMarkerArguments([hashtable]$Marker) {
    $arguments = @('-n', $Marker.name)
    foreach ($name in @($Marker.parameters.Keys | Sort-Object)) {
        $value = $Marker.parameters[$name]
        $option = '--' + ([regex]::Replace($name, '(?<!^)(?=[A-Z])', '-')).ToLowerInvariant()
        $arguments += @($option, $(if ($value -is [bool]) { $value.ToString().ToLowerInvariant() } else { $value }))
    }
    return , $arguments
}

function Invoke-TemplateCli([string[]]$Arguments) {
    $output = & dotnet @Arguments 2>&1
    if ($LASTEXITCODE -ne 0) { throw "dotnet $($Arguments -join ' ') 失败（退出码 $LASTEXITCODE）：`n$($output -join "`n")" }
}

# 把模板包装进隔离的模板目录（不影响本机已安装的模板）。Package 是 nupkg 路径、模板源码目录或 Leistd.Templates::<版本>；
# Sources 是额外的 NuGet 源（地址或本地目录），只用于按版本安装
function Install-TemplatePackage([string]$Package, [string]$HivePath, [string[]]$Sources = @()) {
    $arguments = @('new', 'install', $Package, '--debug:custom-hive', $HivePath, '--force')
    foreach ($source in $Sources) { $arguments += @('--add-source', $source) }
    Invoke-TemplateCli $arguments
}

# 在隔离模板目录里按标记的输入生成到 OutputPath，再把随机生成的 UserSecretsId 换回标记记录的值。
# 同一版本重建时项目集合必须与标记一致；AllowProjectChanges 用于按旧标记生成新版本（新版可增删项目）：
# 共有项目换回记录值，新增项目若与某个共有项目生成了同一个 GUID（同一 guids 条目）也换成对应的记录值
function New-TemplateRebuild([hashtable]$Marker, [string]$HivePath, [string]$OutputPath, [switch]$AllowProjectChanges) {
    if (Test-Path -LiteralPath $OutputPath) { throw "重建目录已存在：$OutputPath" }
    Invoke-TemplateCli (@('new', $script:TemplateShortName, '-o', $OutputPath, '--debug:custom-hive', $HivePath) + (Get-TemplateMarkerArguments $Marker))
    $rebuilt = Read-TemplateMarker $OutputPath
    $markerFile = Join-Path $OutputPath $script:TemplateMarkerPath
    $missing = @($Marker.userSecretsIds.Keys | Where-Object { -not $rebuilt.userSecretsIds.ContainsKey($_) }) +
        @($rebuilt.userSecretsIds.Keys | Where-Object { -not $Marker.userSecretsIds.ContainsKey($_) })
    if ($missing -and -not $AllowProjectChanges) { throw "重建产物与标记记录的项目不一致：$($missing -join ', ')" }
    $replacements = @{}
    foreach ($project in @($rebuilt.userSecretsIds.Keys | Where-Object { $Marker.userSecretsIds.ContainsKey($_) })) {
        $replacements[$rebuilt.userSecretsIds[$project]] = $Marker.userSecretsIds[$project]
    }
    if ($replacements.Count -eq 0) { throw "重建产物与标记记录没有共同的项目，无法换回 UserSecretsId：$($missing -join ', ')" }
    foreach ($project in @($rebuilt.userSecretsIds.Keys)) {
        $generated = $rebuilt.userSecretsIds[$project]
        if (-not $replacements.ContainsKey($generated)) { continue }
        foreach ($file in @((Join-Path $OutputPath $project), $markerFile)) {
            $text = [IO.File]::ReadAllText($file)
            [IO.File]::WriteAllText($file, $text.Replace($generated, $replacements[$generated]), [Text.UTF8Encoding]::new($false))
        }
    }
}

# 两棵树逐文件比较内容：返回差异路径（相对路径，按序），空表示零差异。忽略 .git/
function Compare-TemplateTree([string]$Left, [string]$Right) {
    function Get-TreeHashes([string]$Root) {
        $hashes = @{}
        $full = (Resolve-Path -LiteralPath $Root).Path
        foreach ($file in Get-ChildItem -LiteralPath $full -Recurse -File -Force) {
            $relative = [IO.Path]::GetRelativePath($full, $file.FullName).Replace('\', '/')
            if ($relative -eq '.git' -or $relative.StartsWith('.git/')) { continue }
            $hashes[$relative] = (Get-FileHash -LiteralPath $file.FullName -Algorithm SHA256).Hash
        }
        return $hashes
    }
    $leftHashes = Get-TreeHashes $Left
    $rightHashes = Get-TreeHashes $Right
    return , @(@($leftHashes.Keys) + @($rightHashes.Keys) | Sort-Object -Unique -CaseSensitive |
        Where-Object { $leftHashes[$_] -cne $rightHashes[$_] })
}
