function Test-CiScripts {
    # 工作流用的 action 版本必须 ≥ installer/README.md 里登记的下限（低于下限会在
    # runner 上打 Node 20 弃用告警）。README 改了、工作流忘了跟着升，这里就会失败。
    $readme = Get-Content -LiteralPath (Join-Path $RepoRoot 'installer/README.md') -Raw
    $floorRow = ($readme -split "`n") | Where-Object { $_ -match 'action 的版本下限' } | Select-Object -First 1
    if (-not $floorRow) { throw 'installer/README.md 里找不到「工作流里 action 的版本下限」那一行' }
    $floors = @{}
    foreach ($m in [regex]::Matches($floorRow, '`([\w\-]+/[\w\-]+)`\s*≥\s*v(\d+)')) {
        $floors[$m.Groups[1].Value] = [int]$m.Groups[2].Value
    }
    if ($floors.Count -eq 0) { throw '版本下限那一行里没有解析出任何 action' }

    $checked = 0
    foreach ($wf in Get-ChildItem -LiteralPath (Join-Path $RepoRoot '.github/workflows') -Filter '*.yml' -File) {
        $text = Get-Content -LiteralPath $wf.FullName -Raw
        foreach ($m in [regex]::Matches($text, 'uses:\s*([\w\-]+/[\w\-]+)@v(\d+)')) {
            $name = $m.Groups[1].Value
            if (-not $floors.ContainsKey($name)) { continue }
            $used = [int]$m.Groups[2].Value
            $checked++
            if ($used -lt $floors[$name]) {
                throw "$($wf.Name) 里 $name@v$used 低于 README 登记的下限 v$($floors[$name])"
            }
        }
    }
    if ($checked -eq 0) { throw '工作流里没有找到任何受版本下限约束的 action，检查正则是否失效' }

    # tools/ci/Import-DevCmd.ps1 是 build-kachina job 里唯一负责注入 MSVC 环境的一步，
    # 它退化了 job 只会在 9 分钟冷构建之后才炸。这里注入一份假的 vcvarsall 输出，
    # 断言「解析 → 写进程环境 → 写 GITHUB_ENV」这条链路。
    $scriptPath = Join-Path $RepoRoot 'tools/ci/Import-DevCmd.ps1'
    if (-not (Test-Path -LiteralPath $scriptPath)) {
        throw '缺少 tools/ci/Import-DevCmd.ps1 —— build-kachina / devcheck 的 MSVC 注入步骤没有实现'
    }

    # 探测脚本与它读写的三个文件都带进程唯一后缀：两个 devcheck 进程并行跑时
    # 不再争同一批文件（同名文件会让一方读到另一方写的 GITHUB_ENV）。
    $token = '{0}-{1}' -f $PID, ([guid]::NewGuid().ToString('N').Substring(0, 8))
    $probe = Join-Path $DevCheckRoot "_ci_probe.$token.ps1"
    # Start-Process 在这里被替换成「把准备好的输出拷过去」，因此不需要真的 cl.exe：
    # Linux 与 Windows 上的 devcheck 都能跑，断言的是脚本自己的逻辑。
    $probeSource = @'
param([string]$Script, [string]$Payload, [string]$OutFile)
function Start-Process {
    param([string]$FilePath, [object]$ArgumentList, [switch]$Wait, [switch]$PassThru,
          [switch]$NoNewWindow, [string]$RedirectStandardOutput, [string]$RedirectStandardError)
    $cmdText = Get-Content -LiteralPath $ArgumentList[1] -Raw
    if ($cmdText -notmatch '^@echo off') { throw "生成的 .cmd 缺少 @echo off：$cmdText" }
    if ($cmdText -notmatch 'call ".*vcvarsall\.bat" x64') { throw "生成的 .cmd 没有用 x64 调 vcvarsall：$cmdText" }
    Copy-Item -LiteralPath $Payload -Destination $RedirectStandardOutput -Force
    Set-Content -LiteralPath $RedirectStandardError -Value '' -Encoding utf8
    [pscustomobject]@{ ExitCode = 0 }
}

$env:GITHUB_ENV = $OutFile
Remove-Item -LiteralPath $OutFile -ErrorAction SilentlyContinue
& $Script -Arch x64 -VcVars 'C:\Fake VS\VC\Auxiliary\Build\vcvarsall.bat'

if ($env:INCLUDE -ne 'C:\VS\include;C:\WinSDK\include') { throw "INCLUDE 未注入：[$env:INCLUDE]" }
if ($env:LIB -ne 'C:\VS\lib') { throw "LIB 未注入：[$env:LIB]" }
if ($env:FLAG -ne 'two') { throw "同名变量应以最后一个为准，实际 [$env:FLAG]" }
if ($env:DevEnvDir -ne 'C:\Fake VS\Common7\IDE\') { throw "含空格变量未注入：[$env:DevEnvDir]" }
if ($env:VSCMD_ARG_TGT_ARCH -ne 'x64') { throw "未透传 vcvars 的参数变量：[$env:VSCMD_ARG_TGT_ARCH]" }

$exported = @(Get-Content -LiteralPath $OutFile)
foreach ($line in @('INCLUDE=C:\VS\include;C:\WinSDK\include', 'LIB=C:\VS\lib', 'FLAG=two',
                    'DevEnvDir=C:\Fake VS\Common7\IDE\', 'VSCMD_ARG_TGT_ARCH=x64')) {
    if ($exported -notcontains $line) { throw "GITHUB_ENV 缺少行：$line" }
}
'@
    Set-Content -LiteralPath $probe -Encoding utf8 -Value $probeSource

    $payload = Join-Path $DevCheckRoot "_ci_payload.$token.txt"
    Set-Content -LiteralPath $payload -Encoding utf8 -Value @(
        "Environment initialized for: 'x64'"
        'VSCMD_ARG_TGT_ARCH=x64'
        'DevEnvDir=C:\Fake VS\Common7\IDE\'
        'INCLUDE=C:\VS\include;C:\WinSDK\include'
        'LIB=C:\VS\lib'
        'FLAG=one'
        'FLAG=two'
    )

    $out = Join-Path $DevCheckRoot "_ci_github_env.$token.txt"
    try {
        $result = Invoke-Native -FilePath (Get-Tool 'pwsh') `
            -Arguments @('-NoProfile', '-File', $probe, '-Script', $scriptPath, '-Payload', $payload, '-OutFile', $out) `
            -Tail 30
        if ($result.ExitCode -ne 0) {
            throw "Import-DevCmd.ps1 回归检查失败，见上方输出"
        }
    }
    finally {
        Remove-Item -LiteralPath $probe, $payload, $out -Force -ErrorAction SilentlyContinue
    }

    # all 集合的每一层都必须在 devcheck.yml 里真有一步跑它 ——
    # 新加一层却忘了接进工作流，本地和 CI 都会「绿」，那层等于没写。
    $main = Get-Content -LiteralPath (Join-Path $DevCheckRoot 'devcheck.ps1') -Raw
    $allBlock = [regex]::Match($main, "\`$wanted\s*=\s*if\s*\([^\n]*\)\s*\{\s*@\(([^)]*)\)")
    if (-not $allBlock.Success) { throw 'devcheck.ps1 里找不到 all 层的 $wanted 定义，解析规则失效了' }
    $layers = @([regex]::Matches($allBlock.Groups[1].Value, "'([a-z0-9]+)'") |
        ForEach-Object { $_.Groups[1].Value })
    if ($layers.Count -lt 5) { throw "只从 all 集合里解析出 $($layers.Count) 层，解析规则失效了" }

    $workflow = Get-Content -LiteralPath (Join-Path $RepoRoot '.github/workflows/devcheck.yml') -Raw
    # 无参数的那一步才会跳过参数解析、跑 $wanted 里由 all 定义的层；
    # 只写 -Layer all 不会命中这条，所以 all 集合必须在 CI 上真跑过。
    $runsAll = $workflow -match '(?m)^\s*(?:-\s*)?run:\s*pwsh\s+-NoProfile\s+-File\s+\S*devcheck\.ps1\s*$'
    if (-not $runsAll) {
        throw 'devcheck.yml 里没有一步是不带 -Layer 跑 devcheck.ps1 —— all 集合的层不会在 CI 上执行'
    }
    # 每层要么被 -Layer <名字> 单独点名，要么由上面那次裸调用覆盖。
    $single = @($layers | Where-Object { $workflow -match "-Layer\s+$_\b" })

    return "Import-DevCmd.ps1 链路通过；devcheck.yml 用 all 覆盖 $($layers.Count) 层（其中 $($single.Count) 层有单独步骤：$($single -join '、')）"
}
