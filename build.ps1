# 原神帧率解锁 — 构建脚本
# 依赖策略：
#   - 应用 DLL / Stub（静态 CRT + 内嵌 MinHook）等 → 打进安装载荷（自带）
#   - 无 Node / Python 等语言运行时依赖
#   - .NET Desktop Runtime / VCRedist → Kachina 安装器按 runtimes 配置处理
# 安装器：Kachina（kachina-builder）→ GenshinFpsUnlocker.Install.{ver}.exe
#         安装目录含 GenshinFpsUnlocker.uninst.exe / .update.exe
# 默认：主程序 FDD（包体小）。离线全量：.\build.ps1 -SelfContained
#
# 用法：
#   .\build.ps1
#   .\build.ps1 -Configuration Release
#   .\build.ps1 -SelfContained
#   .\build.ps1 -SkipSetup          # 只编 Host/Stub
#   .\build.ps1 -Install            # 编完后启动 Install.exe（若已生成）
#   .\Build\setup_build.cmd         # 完整 Kachina 打包

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

Write-Host "==> Building Web UI (Vite)" -ForegroundColor Cyan
$uiDir = Join-Path $Root "src/Ui"
$uiDist = Join-Path $uiDir "dist/index.html"
$npm = Get-Command npm -ErrorAction SilentlyContinue
if ($npm) {
    Push-Location $uiDir
    try {
        if (-not (Test-Path (Join-Path $uiDir "node_modules"))) {
            & npm install --no-fund --no-audit
            if ($LASTEXITCODE -ne 0) { throw "npm install failed" }
        }
        & npm run build
        if ($LASTEXITCODE -ne 0) { throw "npm run build failed" }
    } finally { Pop-Location }
    if (-not (Test-Path $uiDist)) { throw "UI dist missing: $uiDist" }
    Write-Host "    UI: $uiDist" -ForegroundColor Green
} elseif (Test-Path $uiDist) {
    Write-Host "    npm not found — using prebuilt src/Ui/dist" -ForegroundColor DarkYellow
} else {
    throw "npm not found and src/Ui/dist missing. Install Node.js or commit a prebuilt UI."
}

Write-Host "==> Building FpsUnlockerStub.dll" -ForegroundColor Cyan
# 勿用 build/：Windows 上与仓库 Build/（Kachina 配置）路径冲突
$StubBuild = Join-Path $Root "out/stub"
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

# 确保 Web UI 在 publish 输出中（csproj Content 可能因路径/条件漏拷）
$uiDistDir = Join-Path $uiDir "dist"
$uiOut = Join-Path $dist "ui"
if (Test-Path (Join-Path $uiDistDir "index.html")) {
    if (Test-Path $uiOut) { Remove-Item $uiOut -Recurse -Force }
    New-Item -ItemType Directory -Force -Path $uiOut | Out-Null
    Copy-Item (Join-Path $uiDistDir "*") $uiOut -Recurse -Force
    Write-Host "    UI copied -> $uiOut" -ForegroundColor Green
} else {
    Write-Warning "src/Ui/dist missing after build — host may fail to load UI"
}


foreach ($extra in @("LICENSE", "USER_AGREEMENT.txt", "config.example.json")) {
    $p = Join-Path $Root $extra
    if (Test-Path $p) {
        Copy-Item $p (Join-Path $dist $extra) -Force
    }
}
# BetterGI 同源应用图标（托盘/快捷方式旁路文件）
$iconSrc = Join-Path $Root "src/Host/Assets/app.ico"
if (Test-Path $iconSrc) {
    Copy-Item $iconSrc (Join-Path $dist "app.ico") -Force
}
$iconPng = Join-Path $Root "src/Host/Assets/app.png"
if (Test-Path $iconPng) {
    Copy-Item $iconPng (Join-Path $dist "app.png") -Force
}

$installExePath = $null
if (-not $SkipSetup) {
    Write-Host "==> Packaging Kachina installer (kachina-builder)" -ForegroundColor Cyan
    $buildDir = Join-Path $Root "Build"
    $builder = $null
    foreach ($c in @(
        (Join-Path $buildDir "kachina-builder.exe"),
        (Join-Path $Root "kachina-builder.exe")
    )) {
        if (Test-Path $c) { $builder = $c; break }
    }

    if (-not $builder) {
        Write-Warning "kachina-builder.exe not found — skip installer pack."
        Write-Host "    Download from https://github.com/YuehaiTeam/kachina-installer/releases" -ForegroundColor DarkYellow
        Write-Host "    Place as Build\kachina-builder.exe, then re-run (or use CI)." -ForegroundColor DarkYellow
        Write-Host "    Portable output remains in dist\" -ForegroundColor DarkYellow
    } else {
        $csproj = Get-Content (Join-Path $Root "src/Host/GenshinFpsUnlocker.Host.csproj") -Raw
        $ver = "1.0.0"
        if ($csproj -match "<Version>([^<]+)</Version>") { $ver = $Matches[1].Trim() }

        $appName = "GenshinFpsUnlocker"
        $work = Join-Path $Root "build/kachina-work"
        if (Test-Path $work) { Remove-Item $work -Recurse -Force }
        $appDir = Join-Path $work $appName
        New-Item -ItemType Directory -Force -Path $appDir | Out-Null
        Copy-Item (Join-Path $dist "*") $appDir -Recurse -Force

        $agree = Join-Path $Root "USER_AGREEMENT.txt"
        if (Test-Path $agree) {
            Copy-Item $agree (Join-Path $appDir "USER_AGREEMENT.txt") -Force
        }

        $config = Join-Path $buildDir "kachina.config.json"
        if (-not (Test-Path $config)) { throw "missing $config" }

        $updaterName = "$appName.update.exe"
        $updaterPath = Join-Path $appDir $updaterName
        Write-Host "    pack updater → $updaterName" -ForegroundColor DarkCyan
        $sideImg = Join-Path $buildDir "installer-side.webp"
        $packExtra = @()
        if (Test-Path $sideImg) { $packExtra += @("-t", $sideImg) }
        & $builder pack -c $config -o $updaterPath @packExtra
        if ($LASTEXITCODE -ne 0) { throw "kachina pack updater failed" }

        $meta = Join-Path $work "metadata.json"
        $hashed = Join-Path $work "hashed"
        Write-Host "    gen metadata / hashed" -ForegroundColor DarkCyan
        Push-Location $work
        try {
            & $builder gen -j 6 -i $appName -m "metadata.json" -o "hashed" `
                -r "bainian-gudu/GenshinFpsUnlocker" -t $ver -u ".\$appName\$updaterName"
            if ($LASTEXITCODE -ne 0) { throw "kachina gen failed" }

            $installName = "$appName.Install.$ver.exe"
            $installOut = Join-Path $work $installName
            Write-Host "    pack offline installer → $installName" -ForegroundColor DarkCyan
            & $builder pack -c $config -m "metadata.json" -d "hashed" -o $installName @packExtra
            if ($LASTEXITCODE -ne 0) { throw "kachina pack install failed" }
        } finally {
            Pop-Location
        }

        $outBuild = Join-Path $buildDir "dist"
        New-Item -ItemType Directory -Force -Path $outBuild | Out-Null
        Copy-Item (Join-Path $work $installName) (Join-Path $outBuild $installName) -Force
        Copy-Item (Join-Path $work $installName) (Join-Path $dist $installName) -Force
        # 便携目录副本（含 update.exe）
        $portable = Join-Path $dist $appName
        if (Test-Path $portable) { Remove-Item $portable -Recurse -Force }
        Copy-Item $appDir $portable -Recurse -Force

        # 可选 7z
        $seven = $null
        foreach ($c in @(
            "7z",
            "${env:ProgramFiles}\7-Zip\7z.exe",
            "${env:ProgramFiles(x86)}\7-Zip\7z.exe"
        )) {
            if ($c -eq "7z") {
                $cmd = Get-Command 7z -ErrorAction SilentlyContinue
                if ($cmd) { $seven = $cmd.Source; break }
            } elseif (Test-Path $c) { $seven = $c; break }
        }
        if ($seven) {
            $archive = "GenshinFpsUnlocker_v$ver.7z"
            $arcPath = Join-Path $dist $archive
            if (Test-Path $arcPath) { Remove-Item $arcPath -Force }
            & $seven a -t7z $arcPath $portable -mx=5 -mf=BCJ2 -r -y
            if ($LASTEXITCODE -eq 0) {
                Copy-Item $arcPath (Join-Path $outBuild $archive) -Force
            }
        }

        $installExePath = Join-Path $dist $installName
        Write-Host "    Installer: $installExePath" -ForegroundColor Green
    }
}

Write-Host "==> Done (host=$hostLabel). Output: $dist\" -ForegroundColor Green
Get-ChildItem $dist | Format-Table Name, Length
Write-Host ""
Write-Host "Install: GenshinFpsUnlocker.Install.{ver}.exe (Kachina，含 uninst/update)" -ForegroundColor Cyan
Write-Host "Note: 默认 FDD；安装器可按配置安装 .NET Desktop Runtime 9 + VCRedist。" -ForegroundColor DarkGray

if ($Install) {
    $gui = $installExePath
    if (-not $gui) {
        $gui = Get-ChildItem $dist -Filter "GenshinFpsUnlocker.Install.*.exe" -File -ErrorAction SilentlyContinue |
            Select-Object -First 1 -ExpandProperty FullName
    }
    if (-not $gui -or -not (Test-Path $gui)) {
        throw "Install exe missing: run without -SkipSetup and ensure kachina-builder is available"
    }
    Write-Host "==> Launching installer..." -ForegroundColor Cyan
    Start-Process -FilePath $gui -WorkingDirectory (Split-Path $gui) -Verb RunAs -Wait
}
