<#
.SYNOPSIS
    把 MSVC 开发环境（cl.exe / link.exe / Windows SDK）注入当前 PowerShell 会话，
    并导出到 GITHUB_ENV 供后续 step 使用；本机手动跑同样有效（只影响当前进程）。

.EXAMPLE
    pwsh tools/ci/Import-DevCmd.ps1

.NOTES
    原先这一步用 ilammy/msvc-dev-cmd@v1。它最后发布于 2024-04，action.yml 仍声明
    node20，在 runner 上会打 Node 20 弃用告警；上游没有 node24 版本可升，于是把它的
    核心逻辑收进仓库：找 vcvarsall.bat → 在子 cmd 里跑一次拿全量环境变量 → 把变化
    的部分写进 GITHUB_ENV。背景见 installer/README.md「workflow 里那些看着多余的设置」。
#>
param(
    # 传给 vcvarsall.bat 的目标架构，如 x64 / x86 / arm64。
    [string]$Arch = 'x64',
    # 显式指定 vcvarsall.bat（留空则自动查找）。自动化测试会注入假脚本。
    [string]$VcVars = ''
)

$ErrorActionPreference = 'Stop'

# 按标准安装目录枚举，不写死盘符/版本/版本号；路径全部由环境变量推导。
function Find-VcVarsAll {
    $roots = @(${env:ProgramFiles}, ${env:ProgramFiles(x86)}) | Where-Object { $_ }
    $patterns = @(
        'Microsoft Visual Studio\*\*\VC\Auxiliary\Build\vcvarsall.bat'
        'Microsoft Visual C++ Build Tools\vcbuildtools.bat'
    )
    foreach ($pattern in $patterns) {
        foreach ($root in $roots) {
            $hit = Get-Item -Path (Join-Path $root $pattern) -ErrorAction SilentlyContinue |
                Sort-Object FullName -Descending |
                Select-Object -First 1
            if ($hit) { return $hit.FullName }
        }
    }

    throw '没有找到 vcvarsall.bat：这台机器缺少 Visual Studio / MSVC Build Tools'
}

# 只负责「跑 vcvarsall 并把环境变量打印出来」。命令写进临时 .cmd，用
# Start-Process 显式传参执行：既不赌 PowerShell 的引号转义规则，也避开
# 临时目录路径里可能出现的空格。
function Get-DevCmdEnvironment {
    param([string]$VcVars)

    $tempDir = [System.IO.Path]::GetTempPath()
    $script = Join-Path $tempDir "import-devcmd-$PID.cmd"
    $stdout = Join-Path $tempDir "import-devcmd-$PID.out"
    $stderr = Join-Path $tempDir "import-devcmd-$PID.err"
    try {
        Set-Content -LiteralPath $script -Encoding ascii -Value @(
            '@echo off'
            "call `"$VcVars`" $Arch"
            'set'
        )
        # 先切到脚本所在目录再启动 cmd：cmd 不支持把 UNC / Linux 路径当工作目录，
        # 显式指定可以避免这一层环境差异。
        $previousLocation = Get-Location
        try {
            Set-Location -LiteralPath $tempDir
            $proc = Start-Process -FilePath 'cmd.exe' -ArgumentList @('/c', $script) `
                -Wait -PassThru -NoNewWindow `
                -RedirectStandardOutput $stdout -RedirectStandardError $stderr
        }
        finally {
            Set-Location -LiteralPath $previousLocation
        }
        $message = (Get-Content -LiteralPath $stdout -Raw -ErrorAction SilentlyContinue)
        $errorText = (Get-Content -LiteralPath $stderr -Raw -ErrorAction SilentlyContinue)
        if ($proc.ExitCode -ne 0) {
            throw "vcvarsall.bat 执行失败（cmd 退出码 $($proc.ExitCode)）：$message$errorText"
        }
        return @($message -split "`r?`n" | Where-Object { $_ -ne '' })
    }
    finally {
        foreach ($path in @($script, $stdout, $stderr)) {
            Remove-Item -LiteralPath $path -Force -ErrorAction SilentlyContinue
        }
    }
}

function Set-DevCmdEnvironment {
    param([string[]]$Lines)

    # vcvarsall 会先打一段横幅，只挑「名字=值」行；同名变量后出现的覆盖先出现的。
    $vars = [ordered]@{}
    foreach ($line in $Lines) {
        if ($line -match '^([^=]+)=(.*)$') { $vars[$Matches[1]] = $Matches[2] }
    }
    if ($vars.Count -eq 0) {
        throw 'vcvarsall.bat 没有输出任何环境变量'
    }

    foreach ($name in $vars.Keys) {
        Set-Item -Path "Env:$name" -Value $vars[$name] -ErrorAction SilentlyContinue
    }

    # 后续 step 起的是新进程，只有写进 GITHUB_ENV 才留得住；
    # 本机跑时没有这个变量，跳过即可（当前会话已经设好了）。
    if ($env:GITHUB_ENV) {
        $linesToExport = foreach ($name in $vars.Keys) { "$name=$($vars[$name])" }
        Add-Content -LiteralPath $env:GITHUB_ENV -Value $linesToExport -Encoding utf8
    }
}

if (-not $VcVars) { $VcVars = Find-VcVarsAll }
Set-DevCmdEnvironment -Lines (Get-DevCmdEnvironment -VcVars $VcVars)
Write-Host "MSVC 开发环境已注入（$Arch）：$VcVars"
