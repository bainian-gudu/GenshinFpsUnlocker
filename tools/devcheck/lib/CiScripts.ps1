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

    $probe = Join-Path $DevCheckRoot '_ci_probe.ps1'
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

    $payload = Join-Path $DevCheckRoot '_ci_payload.txt'
    Set-Content -LiteralPath $payload -Encoding utf8 -Value @(
        "Environment initialized for: 'x64'"
        'VSCMD_ARG_TGT_ARCH=x64'
        'DevEnvDir=C:\Fake VS\Common7\IDE\'
        'INCLUDE=C:\VS\include;C:\WinSDK\include'
        'LIB=C:\VS\lib'
        'FLAG=one'
        'FLAG=two'
    )

    $out = Join-Path $DevCheckRoot '_ci_github_env.txt'
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

    return 'Import-DevCmd.ps1：生成 cmd / 解析 vcvars 输出 / 注入环境 / 写 GITHUB_ENV 均通过'
}
