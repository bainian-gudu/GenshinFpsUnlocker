# 原神帧率解锁 — 构建脚本
# 依赖策略：
#   - 应用 DLL / Stub（静态 CRT + 内嵌 MinHook）等 → 打进 Payload（自带）
#   - 无 Node / Python 等语言运行时依赖
#   - .NET Desktop Runtime → 不打包；首次运行检测并提示下载官方 .exe
# 安装器：MicaSetup（makemica）在 Build\ 生成 Setup.exe + 内嵌 Uninst.exe
# 默认：主程序 FDD（包体小）。离线全量：.\build.ps1 -SelfContained
#
# 用法：
#   .\build.ps1
#   .\build.ps1 -Configuration Release
#   .\build.ps1 -SelfContained
#   .\build.ps1 -SkipSetup          # 只编 Host/Stub，不调用 MicaSetup
#   .\build.ps1 -Install            # 编完后启动 Setup（若已生成）
#   .\Build\setup_build.cmd         # 完整：publish.7z + MicaSetup 安装包

param(
    [ValidateSet("Debug", "Release")]
    [string]$Configuration = "Release",
    [string]$Generator = "",
    [switch]$Install,
    [switch]$SelfContained,
    [switch]$SkipSetup
)

$ErrorActionPreference = "Stop"
$Root = Split-Path -Parent $MyInvocation.MyCommand.Path
Set-Location $Root

$hostSelfContained = [bool]$SelfContained
$hostLabel = if ($hostSelfContained) { "self-contained" } else { "framework-dependent" }
Write-Host "==> Host publish mode: $hostLabel" -ForegroundColor Cyan

Write-Host "==> Building FpsUnlockerStub.dll" -ForegroundColor Cyan
$StubBuild = Join-Path $Root "build/stub"
New-Item -ItemType Directory -Force -Path $StubBuild | Out-Null

$cmakeArgs = @("-S", "src/Stub", "-B", $StubBuild)
if ($Generator) {
    $cmakeArgs += @("-G", $Generator)
}
& cmake @cmakeArgs
if ($LASTEXITCODE -ne 0) { throw "cmake configure failed" }

& cmake --build $StubBuild --config $Configuration
if ($LASTEXITCODE -ne 0) { throw "cmake build failed" }

Write-Host "==> Building GenshinFpsUnlocker host ($hostLabel, win-x64)" -ForegroundColor Cyan
$HostProj = Join-Path $Root "src/Host/GenshinFpsUnlocker.Host.csproj"
$dist = Join-Path $Root "dist"
if (Test-Path $dist) {
    Get-ChildItem $dist -Force | Remove-Item -Recurse -Force -ErrorAction SilentlyContinue
}
New-Item -ItemType Directory -Force -Path $dist | Out-Null

$publishArgs = @(
    "publish", $HostProj,
    "-c", $Configuration,
    "-r", "win-x64",
    "-o", $dist,
    "--self-contained", $(if ($hostSelfContained) { "true" } else { "false" }),
    "-p:PublishSingleFile=false",
    "-p:IncludeNativeLibrariesForSelfExtract=true",
    "-p:PublishTrimmed=false"
)
& dotnet @publishArgs
if ($LASTEXITCODE -ne 0) { throw "dotnet publish host failed" }

$candidates = @(
    (Join-Path $StubBuild "bin/FpsUnlockerStub.dll"),
    (Join-Path $StubBuild "bin/$Configuration/FpsUnlockerStub.dll"),
    (Join-Path $StubBuild "$Configuration/FpsUnlockerStub.dll"),
    (Join-Path $StubBuild "FpsUnlockerStub.dll")
)
$stub = $candidates | Where-Object { Test-Path $_ } | Select-Object -First 1
if (-not $stub) {
    throw "FpsUnlockerStub.dll not found after build. Searched: $($candidates -join ', ')"
}
Copy-Item $stub (Join-Path $dist "FpsUnlockerStub.dll") -Force

# 随载荷附带许可与示例配置
foreach ($extra in @("LICENSE", "config.example.json")) {
    $p = Join-Path $Root $extra
    if (Test-Path $p) {
        Copy-Item $p (Join-Path $dist $extra) -Force
    }
}

$setupExePath = $null
if (-not $SkipSetup) {
    Write-Host "==> Packaging MicaSetup installer (makemica)" -ForegroundColor Cyan
    $buildDir = Join-Path $Root "Build"
    $seven = $null
    foreach ($c in @(
        "7z",
        (Join-Path $buildDir "MicaSetup.Tools\7-Zip\7z.exe"),
        "${env:ProgramFiles}\7-Zip\7z.exe",
        "${env:ProgramFiles(x86)}\7-Zip\7z.exe"
    )) {
        if ($c -eq "7z") {
            $cmd = Get-Command 7z -ErrorAction SilentlyContinue
            if ($cmd) { $seven = $cmd.Source; break }
        } elseif (Test-Path $c) {
            $seven = $c; break
        }
    }

    $makemica = Join-Path $buildDir "makemica.exe"
    if (-not (Test-Path $makemica)) {
        # CI 可能把 makemica 解压到仓库根
        $alt = Join-Path $Root "makemica.exe"
        if (Test-Path $alt) { $makemica = $alt }
    }

    if (-not $seven) {
        Write-Warning "7z not found — skip MicaSetup pack. Portable output remains in dist\"
        Write-Host "    Install 7-Zip or run Build\setup_build.cmd after placing tools." -ForegroundColor DarkYellow
    } elseif (-not (Test-Path $makemica)) {
        Write-Warning "makemica.exe not found — skip MicaSetup pack."
        Write-Host "    Download MicaSetup_v*.7z from https://github.com/lemutec/MicaSetup/releases" -ForegroundColor DarkYellow
        Write-Host "    Extract into Build\ (makemica.exe + template\), then re-run." -ForegroundColor DarkYellow
        Write-Host "    Or use CI workflow which downloads it automatically." -ForegroundColor DarkYellow
    } else {
        # 读版本
        $csproj = Get-Content (Join-Path $Root "src/Host/GenshinFpsUnlocker.Host.csproj") -Raw
        $ver = "1.0.0"
        if ($csproj -match "<Version>([^<]+)</Version>") { $ver = $Matches[1].Trim() }

        $payloadDir = Join-Path $Root "build/mica-payload"
        if (Test-Path $payloadDir) { Remove-Item $payloadDir -Recurse -Force }
        New-Item -ItemType Directory -Force -Path $payloadDir | Out-Null
        Copy-Item (Join-Path $dist "*") $payloadDir -Recurse -Force

        $publish7z = Join-Path $buildDir "publish.7z"
        if (Test-Path $publish7z) { Remove-Item $publish7z -Force }
        & $seven a -t7z $publish7z "$payloadDir\*" -mx=5 -mf=BCJ2 -r -y
        if ($LASTEXITCODE -ne 0) { throw "7z pack publish.7z failed" }

        $portableName = "GenshinFpsUnlocker_v$ver.7z"
        $outDir = Join-Path $buildDir "dist"
        New-Item -ItemType Directory -Force -Path $outDir | Out-Null
        Copy-Item $publish7z (Join-Path $outDir $portableName) -Force
        Copy-Item $publish7z (Join-Path $dist $portableName) -Force

        # makemica 工作目录：Build\（json 中 Package/图标为相对路径）
        $licenseBuild = Join-Path $buildDir "LICENSE"
        if (Test-Path (Join-Path $Root "LICENSE")) {
            Copy-Item (Join-Path $Root "LICENSE") $licenseBuild -Force
        }

        Push-Location $buildDir
        try {
            & $makemica "micasetup.json"
            if ($LASTEXITCODE -ne 0) { throw "makemica failed with exit $LASTEXITCODE" }

            $produced = Join-Path $buildDir "GenshinFpsUnlocker_Setup.exe"
            if (-not (Test-Path $produced)) {
                $produced = Get-ChildItem $buildDir -Filter "*Setup*.exe" -File |
                    Where-Object { $_.Name -notmatch "makemica" } |
                    Select-Object -First 1 -ExpandProperty FullName
            }
            if (-not $produced -or -not (Test-Path $produced)) {
                throw "MicaSetup output exe not found after makemica"
            }

            $setupName = "GenshinFpsUnlocker_Setup_v$ver.exe"
            $setupDestBuild = Join-Path $outDir $setupName
            $setupDestDist = Join-Path $dist $setupName
            Copy-Item $produced $setupDestBuild -Force
            Copy-Item $produced $setupDestDist -Force
            # 兼容旧路径期望
            $setupFolder = Join-Path $dist "Setup"
            New-Item -ItemType Directory -Force -Path $setupFolder | Out-Null
            Copy-Item $produced (Join-Path $setupFolder "GenshinFpsUnlocker.Setup.exe") -Force
            $setupExePath = $setupDestDist
            Write-Host "    Portable:  $dist\$portableName" -ForegroundColor Green
            Write-Host "    Installer: $setupExePath" -ForegroundColor Green
        } finally {
            Pop-Location
        }
    }
}

Write-Host "==> Done (host=$hostLabel). Output: $dist\" -ForegroundColor Green
Get-ChildItem $dist | Format-Table Name, Length
Write-Host ""
Write-Host "Install: run GenshinFpsUnlocker_Setup_v*.exe (MicaSetup，含 Uninst.exe)" -ForegroundColor Cyan
Write-Host "Note: 默认 FDD 主程序；.NET Desktop Runtime 在首次运行时检测提示（无 Node/Python）。" -ForegroundColor DarkGray

if ($Install) {
    $gui = $setupExePath
    if (-not $gui) {
        $gui = Get-ChildItem $dist -Filter "GenshinFpsUnlocker_Setup*.exe" -File -ErrorAction SilentlyContinue |
            Select-Object -First 1 -ExpandProperty FullName
    }
    if (-not $gui) {
        $gui = Join-Path $dist "Setup\GenshinFpsUnlocker.Setup.exe"
    }
    if (-not (Test-Path $gui)) { throw "Setup exe missing: run without -SkipSetup and ensure makemica is available" }
    Write-Host "==> Launching installer..." -ForegroundColor Cyan
    Start-Process -FilePath $gui -WorkingDirectory (Split-Path $gui) -Verb RunAs -Wait
}
