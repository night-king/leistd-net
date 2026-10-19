# 发布通道共用的提交解析：框架与模板两条通道的升版推算、脚注闸门与发布说明都只用这一份判据，
# 否则会出现"升版算成 minor、发布说明却没有新功能"或"过了脚注闸门却不进破坏性小节"这类两处判定相反。
# 由 .github/workflows/release.yml 的步骤 dot-source；判据的夹具测试见 scripts/test-workflow-change-scope.py。

# 逐条读取提交，而不是把所有提交拼成一个文本 blob：「这一版有没有破坏性变更」可以看整体，
# 但「每条带 ! 的提交有没有脚注」必须按条判。记录用 \x1e 分隔、字段用 \x00 分隔，正文里出现换行不会打乱切分。
# 标题与正文保持原样，不 Trim：脚注闸门判的是未经处理的正文，先去掉缩进的话，
# `  BREAKING CHANGE: x` 会在一处被判成缺脚注、在另一处成为脚注。展示时再整理。
# -Paths 只取改动了这些路径的提交（模板通道用）；不传时取范围内全部提交。
function Read-ReleaseCommits([string]$Range, [string[]]$Paths = @()) {
    $arguments = @('log', $Range, '--no-merges', '--pretty=format:%H%x00%s%x00%b%x1e')
    if ($Paths.Count -gt 0) { $arguments += @('--') + $Paths }
    $raw = (& git @arguments) -join "`n"
    if ($LASTEXITCODE -ne 0) { throw "git log $Range 失败" }
    @(
        $raw -split "`u{001e}" | Where-Object { $_.Trim() } | ForEach-Object {
            $parts = $_.TrimStart("`n") -split "`u{0000}"
            [pscustomobject]@{ Sha = $parts[0]; Subject = $parts[1]; Body = if ($parts.Count -gt 2) { $parts[2] } else { '' } }
        }
    )
}

# 破坏性变更的两个判据，全流程共用这一处——升版与脚注闸门用同一把尺。
#
# 脚注必须是 Conventional Commits 的真脚注（行首 BREAKING CHANGE: / BREAKING-CHANGE:），
# 不是正文里任意位置出现这串字样：一条只在正文里提到「这次不算 BREAKING CHANGE」的 docs 提交不能被判成 major。
# 脚注要求：行首、大写、冒号后同一行就有内容。三处识别（闸门、升版、发布说明）必须一致，
# 只在一处接受连字符会出现"过了闸门却不进发布说明"。
#
# (?-i) 是必须的：PowerShell 的 -match 默认不区分大小写，不关掉的话正文里一行
# `breaking change: none` 就会把这一版判成 major。规范规定脚注是大写。
# [ \t]* 而不是 \s*：\s 本身就包含换行，\s* 会跨行——`BREAKING CHANGE:` 独占一行、
# 空行之后随便一段正文也会被算成"脚注非空"，那等于没有非空检查。
# 标题里的 type 仍不区分大小写：`FEAT!:` 也要求脚注，宽进严出，漏判才是问题。
function Test-BreakingMark([string]$Subject) { $Subject -match '^[a-z]+(\([^)]*\))?!:' }
function Test-BreakingFooter([string]$Body) { $Body -match '(?m-i)^BREAKING[ -]CHANGE:[ \t]*\S' }

# 带 ! 的提交必须有脚注：适配方是靠 git log --grep='BREAKING CHANGE' 检索改动的，
# 只有 ! 的提交不在那个结果里，对应的改动会被整体漏掉（versioning.md「标了 ! 就必须有 BREAKING CHANGE: 脚注」）。
function Assert-BreakingFooters($Commits) {
    $missing = @($Commits | Where-Object { (Test-BreakingMark $_.Subject) -and -not (Test-BreakingFooter $_.Body) })
    if ($missing.Count -gt 0) {
        $listed = ($missing | ForEach-Object { "  $($_.Sha.Substring(0,8)) $($_.Subject)" }) -join "`n"
        throw ("这些提交标了 ! 但没有 BREAKING CHANGE: 脚注，适配方按脚注检索时会漏掉：`n$listed`n" +
            "脚注不必复述细节，指向升级说明的具体小节即可（见 docs/framework/versioning.md）。")
    }
}

function Test-BreakingCommits($Commits) {
    @($Commits | Where-Object { (Test-BreakingMark $_.Subject) -or (Test-BreakingFooter $_.Body) }).Count -gt 0
}

# 升版幅度：破坏性 → major，feat → minor，其余 → patch。
# SemVer 第 4 条：0.y.z 是初始开发期，公共 API 不承诺稳定，因此该期间的破坏性变更按 minor 递增，不进 1.0.0。
# 进 1.0.0 必须是一次显式决定，不能由任意一条带 ! 的提交顺带触发——否则「第一次不兼容改动」就成了发布 1.0 的时间点。
# 破坏性变更本身不会被吞掉：发布说明仍按 ! / BREAKING CHANGE 归入「破坏性变更」小节。
function Get-ReleaseBump($Commits, [string]$BaseVersion) {
    $major = [int]$BaseVersion.Split('.')[0]
    $bump = 'patch'
    if (Test-BreakingCommits $Commits) { $bump = 'major' }
    elseif (@($Commits | Where-Object { $_.Subject -match '^feat(\([^)]*\))?:' }).Count -gt 0) { $bump = 'minor' }
    if ($bump -eq 'major' -and $major -eq 0) { $bump = 'minor' }
    return $bump
}

function Step-ReleaseVersion([string]$BaseVersion, [string]$Bump) {
    if ($BaseVersion -notmatch '^\d+\.\d+\.\d+$') { throw "版本非法: $BaseVersion" }
    $p = $BaseVersion.Split('.')
    [int]$maj = $p[0]; [int]$min = $p[1]; [int]$pat = $p[2]
    switch ($Bump) {
        'major' { $maj++; $min = 0; $pat = 0 }
        'minor' { $min++; $pat = 0 }
        'patch' { $pat++ }
        default { throw "未知升版幅度: $Bump" }
    }
    return "$maj.$min.$pat"
}

# 框架 stable 发布回写版本的机器人提交（只改 VERSION 与模板默认的 LeistdFrameworkVersion）。
# 它不是一项变更，两条通道的发布说明都不列它；模板通道也不因它升版：只改框架时不升模板版本。
function Test-ReleaseWriteBack([string]$Subject) { $Subject -cmatch '^chore: 发布 v\d+\.\d+\.\d+ \[skip ci\]$' }

# 发布说明的变更小节：每条记录带上 Conventional Commits 的 scope（如 settings、multi-tenancy），
# 各小节内按 scope 字母序分组，没有 scope 的条目放在最后；组内保持 git log 的顺序。
# Release 正文就是变更记录，消费者按自己用到的范围查找。
function Format-ReleaseChangeSections($Commits) {
    $breaking = New-Object System.Collections.Generic.List[object]
    $features = New-Object System.Collections.Generic.List[object]
    $fixes = New-Object System.Collections.Generic.List[object]
    $others = New-Object System.Collections.Generic.List[object]

    foreach ($commit in $Commits) {
        $subject = $commit.Subject
        $body = $commit.Body
        if ([string]::IsNullOrWhiteSpace($subject)) { continue }
        if ($subject -match '(?i)bump version|发布 v|chore:.*version') { continue }
        $scope = if ($subject -match '^[A-Za-z]+\(([^)]+)\)!?:') { $Matches[1].Trim() } else { '' }
        # 与 Test-BreakingFooter 同一口径：行首、大写（(?-i)）、两种写法等价。
        # (\S.*?) 而不是 (.+?)：冒号后必须同一行就有内容，否则"空脚注 + 空行 + 无关正文"
        # 会被闸门判成无脚注、却被这里提取出正文，两处判定相反。续行仍然收得进来（s 模式）。
        #
        # 结束条件除了空行与正文末尾，还要算上"下一个脚注"：规范允许后一个脚注终止前一个的值，
        # 中间不必空行。不算的话 `BREAKING CHANGE: 见升级说明` 紧跟 `Reviewed-by: X`
        # 会把签名行一起并进破坏性说明里。脚注分隔符按规范是 ": " 或 " #"。
        # BREAKING CHANGE 要单列：规范只给它开了带空格的例外，按 [A-Za-z-]+ 匹配 token
        # 会漏掉它，于是紧邻的第二条 BREAKING CHANGE 被并进第一条。
        $breakingDetail = if ($body -match '(?ms-i)^BREAKING[ -]CHANGE:[ \t]*(\S.*?)(?=\r?\n\r?\n|\r?\nBREAKING[ -]CHANGE:|\r?\n[A-Za-z][A-Za-z-]*(?:: | #)|\s*\z)') {
            ($Matches[1].Trim() -replace '\s*\r?\n\s*', ' ')
        } else { '' }

        $display = $subject.Trim()
        if ($breakingDetail -or (Test-BreakingMark $subject)) {
            $breaking.Add([pscustomobject]@{ Scope = $scope; Line = "- $display" + $(if ($breakingDetail) { " — $breakingDetail" } else { '' }) })
        } elseif ($subject -match '^feat(\([^)]*\))?:\s*(.+)$') {
            $features.Add([pscustomobject]@{ Scope = $scope; Line = "- $($Matches[2])" })
        } elseif ($subject -match '^fix(\([^)]*\))?:\s*(.+)$') {
            $fixes.Add([pscustomobject]@{ Scope = $scope; Line = "- $($Matches[2])" })
        } else {
            $others.Add([pscustomobject]@{ Scope = $scope; Line = "- $display" })
        }
    }

    $lines = New-Object System.Collections.Generic.List[string]
    foreach ($section in @(@('Breaking Changes', $breaking), @('新功能', $features), @('修复', $fixes), @('其他更新', $others))) {
        $entries = $section[1]
        if ($entries.Count -eq 0) { continue }
        $lines.Add("## $($section[0])"); $lines.Add('')
        $groups = @($entries | Group-Object Scope | Sort-Object @{ Expression = { $_.Name -eq '' } }, @{ Expression = { $_.Name } })
        foreach ($group in $groups) {
            $lines.Add($(if ($group.Name) { "### $($group.Name)" } else { '### 未标注范围' })); $lines.Add('')
            $group.Group | ForEach-Object { $lines.Add($_.Line) }
            $lines.Add('')
        }
    }
    if ($breaking.Count + $features.Count + $fixes.Count + $others.Count -eq 0) {
        $lines.Add('## 更新内容'); $lines.Add(''); $lines.Add('- 代码优化和维护更新。'); $lines.Add('')
    }
    return $lines
}
