# devcheck 各层实现（被 devcheck.ps1 dot-source）
#
# 每个 Test-* 函数对应一个层：返回字符串 = 通过的说明，抛异常 = 失败。
# 依赖 devcheck.ps1 的 $RepoRoot / $DevCheckRoot / $KachinaSrc 与 lib/Common.ps1 的助手函数。

function Test-VendoredSource {
    # kachina 必须是「仓库内的源码快照」：CI 与打包脚本都不许从上游
    # YuehaiTeam/kachina-installer 拉源码或下二进制，只能从本仓库构建。
    $notes = [System.Collections.Generic.List[string]]::new()

    # 1) 不是 submodule
    if (Test-Path -LiteralPath (Join-Path $RepoRoot '.gitmodules')) {
        throw '存在 .gitmodules —— kachina 必须是仓库内的源码快照，不是 submodule'
    }

    # 2) 快照完整（缺一个就说明 vendored 源码被误删，CI 会退化成「去别处找」）
    $required = @(
        'installer/kachina/package.json',
        'installer/kachina/pnpm-lock.yaml',
        'installer/kachina/src-tauri/Cargo.toml',
        'installer/kachina/src-tauri/Cargo.lock',
        'installer/kachina/src-tauri/src/installer/uninstall.rs',
        'installer/kachina/src-tauri/src/builder/pack.rs',
        'installer/kachina/src/App.vue',
        'installer/build-kachina.ps1',
        'installer/pack.ps1'
    )
    foreach ($r in $required) {
        if (-not (Test-Path -LiteralPath (Join-Path $RepoRoot $r))) { throw "kachina 源码快照不完整，缺 $r" }
    }
    $notes.Add("快照完整($($required.Count) 个关键文件)")

    # 3) 工作流与打包脚本里不许出现「从外部拉源码/下二进制」的动作
    #    只扫可执行内容：注释行（# 开头）跳过，避免误伤说明性文字
    $scan = @(Get-ChildItem -LiteralPath (Join-Path $RepoRoot '.github/workflows') -Filter '*.yml' -File)
    $scan += @(Get-ChildItem -LiteralPath (Join-Path $RepoRoot 'installer') -Filter '*.ps1' -File)
    $rootBuild = Join-Path $RepoRoot 'build.ps1'
    if (Test-Path -LiteralPath $rootBuild) { $scan += @(Get-Item -LiteralPath $rootBuild) }

    $badPatterns = @(
        'YuehaiTeam', 'kachina-installer\.git', 'releases/download', 'release-downloader',
        'git\s+clone', 'git\s+submodule', 'Invoke-WebRequest', 'Invoke-RestMethod',
        'DownloadFile', 'curl\s', 'wget\s'
    )
    $inBlockComment = $false
    foreach ($f in $scan) {
        $lineNo = 0
        foreach ($line in [System.IO.File]::ReadLines($f.FullName)) {
            $lineNo++
            $t = $line.Trim()
            if ($t -match '^<#' -or $inBlockComment) {
                $inBlockComment = -not ($t -match '#>')
                continue
            }
            if ($t.StartsWith('#')) { continue }
            foreach ($pat in $badPatterns) {
                if ($t -match $pat) {
                    throw "$($f.Name):$lineNo 出现从外部拉取的语句 [$pat]: $t"
                }
            }
        }
    }
    $notes.Add("$($scan.Count) 个工作流/脚本无外部拉取")

    # 4) build.yml 必须真的走「源码构建」这条路
    $buildYml = [System.IO.File]::ReadAllText((Join-Path $RepoRoot '.github/workflows/build.yml'))
    if ($buildYml -notmatch 'build-kachina\.ps1') {
        throw 'build.yml 没有调用 installer/build-kachina.ps1 —— kachina 必须从仓库内源码构建'
    }
    if ($buildYml -notmatch [regex]::Escape("hashFiles('installer/kachina/**')")) {
        throw 'build.yml 的 kachina 缓存 key 没有基于 installer/kachina 源码哈希'
    }
    $notes.Add('CI 从源码构建 + 源码哈希缓存')

    # 5) kachina 的 git 依赖必须在 Cargo.lock 里锁到 commit，且不得指向上游
    $cargoToml = [System.IO.File]::ReadAllText((Join-Path $RepoRoot 'installer/kachina/src-tauri/Cargo.toml'))
    $cargoLock = [System.IO.File]::ReadAllText((Join-Path $RepoRoot 'installer/kachina/src-tauri/Cargo.lock'))
    # 剥掉整行注释再扫：Cargo.toml 里的注释会写「原为 git = ".../rcedit-rs.git"」这类
    # 说明文字，不剥掉就会被当成真依赖，然后因为在 Cargo.lock 里找不到而误报。
    $cargoTomlCode = (([System.IO.File]::ReadAllLines((Join-Path $RepoRoot 'installer/kachina/src-tauri/Cargo.toml'))) |
        Where-Object { -not $_.Trim().StartsWith('#') }) -join "`n"
    $gitDeps = @([regex]::Matches($cargoTomlCode, 'git\s*=\s*"([^"]+)"') |
        ForEach-Object { $_.Groups[1].Value } | Sort-Object -Unique)
    foreach ($g in $gitDeps) {
        if ($g -match 'YuehaiTeam') { throw "kachina 的 cargo 依赖指向上游仓库: $g" }
        $esc = [regex]::Escape($g)
        if ($cargoLock -notmatch "source = `"git\+$esc[^`"]*#[0-9a-f]{40}") {
            throw "git 依赖没有在 Cargo.lock 里锁定 commit（CI 可能拉到漂移的分支）: $g"
        }
    }
    $notes.Add("$($gitDeps.Count) 个 git 依赖已锁 commit")

    # 6) npm 依赖里不许有 git/http/file/link 形式（只能是 registry 版本）
    $pkg = Get-Content -LiteralPath (Join-Path $RepoRoot 'installer/kachina/package.json') -Raw | ConvertFrom-Json
    foreach ($section in @('dependencies', 'devDependencies')) {
        $node = $pkg.$section
        if (-not $node) { continue }
        foreach ($prop in $node.PSObject.Properties) {
            if ($prop.Value -match '^(git|git\+|https?|github|file|link|workspace):' -or
                $prop.Value -match 'YuehaiTeam') {
                throw "kachina 的 npm 依赖 $($prop.Name) 不是 registry 版本: $($prop.Value)"
            }
        }
    }
    $notes.Add('npm 依赖全部来自 registry')

    # 7) rcedit-rs 的 vendored 副本：kachina 唯一需要 C++ 编译器的依赖。
    #    为什么不用 git 依赖、与上游差在哪、怎么升级：副本目录里的 LOCAL_PATCHES.md。
    $rc = 'installer/kachina/vendor/rcedit-rs'
    $rcRequired = @(
        "$rc/Cargo.toml", "$rc/LICENSE", "$rc/LICENSE.rcedit", "$rc/src/lib.rs",
        "$rc/rcedit-sys/Cargo.toml", "$rc/rcedit-sys/build.rs", "$rc/rcedit-sys/src/lib.rs",
        "$rc/rcedit-sys/src/rescle.cc", "$rc/rcedit-sys/src/rescle.h",
        "$rc/rcedit-sys/src/librcedit.cpp"
    )
    foreach ($r in $rcRequired) {
        if (-not (Test-Path -LiteralPath (Join-Path $RepoRoot $r))) { throw "rcedit-rs vendored 副本不完整，缺 $r" }
    }
    # 只看可执行代码：rescle.cc 里解释这处修改的注释本身就会写出 locale::empty()，
    # 不剥掉 // 注释会自己误报自己（跟上面扫工作流时跳过 # 注释是同一个道理）。
    $rescle = (([System.IO.File]::ReadAllLines((Join-Path $RepoRoot "$rc/rcedit-sys/src/rescle.cc"))) |
        ForEach-Object { ($_ -replace '//.*$', '') }) -join "`n"
    if ($rescle -match 'locale::empty\s*\(') {
        throw 'rescle.cc 用回了 std::locale::empty() —— MSVC 14.51(VS 2026) 起已移除，windows-latest 上必然 error C2039'
    }
    if ($cargoTomlCode -match '(?m)^\s*rcedit\s*=\s*\{[^\n]*\bgit\s*=') {
        throw 'kachina 的 rcedit 依赖又指回 git 上游了 —— 必须用 installer/kachina/vendor/rcedit-rs 的副本'
    }
    if ($cargoTomlCode -notmatch '(?m)^\s*rcedit\s*=\s*\{[^\n]*path\s*=') {
        throw 'kachina 的 rcedit 依赖不是 path 形式（应指向 ../vendor/rcedit-rs）'
    }
    if ($cargoLock -match 'source = "git\+https://github\.com/Devolutions/rcedit-rs') {
        throw 'Cargo.lock 里 rcedit 仍然是 git 来源 —— 应随 path 依赖一起更新'
    }
    $notes.Add('rcedit-rs 已 vendored(含 MSVC 14.51 修复)')

    return ($notes -join '；')
}

function Test-Ps1Syntax {
    $files = @(Get-ChildItem -Path $RepoRoot -Recurse -Filter '*.ps1' -File |
        Where-Object { $_.FullName -notmatch '[\\/](node_modules|target|dist|bin|obj|gen|\.git)[\\/]' })
    $bad = 0
    foreach ($f in $files) {
        $tokens = $null; $errs = $null
        [System.Management.Automation.Language.Parser]::ParseFile($f.FullName, [ref]$tokens, [ref]$errs) | Out-Null
        if ($errs -and $errs.Count) {
            $bad++
            Write-Bad $f.FullName.Substring($RepoRoot.Length + 1)
            foreach ($e in $errs) { Write-Bad "   line $($e.Extent.StartLineNumber): $($e.Message)" }
        }
    }
    if ($bad) { throw "$bad / $($files.Count) 个 .ps1 有语法错误" }
    return "$($files.Count) 个 .ps1 语法通过"
}

function New-GenSources {
    $a = @(New-TypecheckGen -RepoRoot $RepoRoot -DevCheckRoot $DevCheckRoot)
    $b = @(New-LogicGen -RepoRoot $RepoRoot -DevCheckRoot $DevCheckRoot)

    # front：把要类型检查的 TS 原样搬到 front/gen/src（保持相对 import 结构）
    $frontGen = Join-Path $DevCheckRoot 'front/gen/src'
    New-Item -ItemType Directory -Path (Join-Path $frontGen 'utils') -Force | Out-Null
    Copy-Item (Join-Path $RepoRoot 'installer/kachina/src/types.ts') (Join-Path $frontGen 'types.ts') -Force
    Copy-Item (Join-Path $RepoRoot 'installer/kachina/src/utils/agreement.ts') (Join-Path $frontGen 'utils/agreement.ts') -Force
    # prettier 要用与 kachina 相同的配置，否则会按默认风格误报
    $rc = Join-Path $RepoRoot 'installer/kachina/.prettierrc'
    if (Test-Path -LiteralPath $rc) { Copy-Item $rc (Join-Path $DevCheckRoot 'front/.prettierrc') -Force }

    return "Rust $($a.Count + $b.Count) 个 + TS 2 个"
}

function Test-RustTypecheck {
    $cargo = Get-Tool 'cargo'
    if (-not $cargo) { Skip-Layer 'cargo 不在 PATH（https://rustup.rs）' }
    $target = 'x86_64-pc-windows-msvc'

    $rustup = Get-Tool 'rustup'
    if ($rustup) {
        $installed = ((& $rustup target list --installed) -join "`n")
        if ($installed -notmatch [regex]::Escape($target)) {
            if ($SkipInstall) { Skip-Layer "缺少 rustup target $target（去掉 -SkipInstall 可自动装）" }
            Write-Info "安装 rustup target $target …"
            & $rustup target add $target | Out-Null
        }
    }

    $r = Invoke-Native -FilePath $cargo `
        -Arguments @('check', '--target', $target, '--message-format', 'short') `
        -WorkingDirectory (Join-Path $DevCheckRoot 'rust/typecheck') -Tail 60
    if ($r.ExitCode -ne 0) { throw 'cargo check（x86_64-pc-windows-msvc）失败，见上方输出' }
    $warn = ([regex]::Matches($r.Output, 'warning:')).Count
    $what = if ($warn) { "（$warn 条 warning）" } else { '，0 warning' }
    return "uninstall.rs + utils/error.rs 在 $target 上类型检查通过$what"
}

function Test-RustLogic {
    $cargo = Get-Tool 'cargo'
    if (-not $cargo) { Skip-Layer 'cargo 不在 PATH（https://rustup.rs）' }
    $r = Invoke-Native -FilePath $cargo -Arguments @('run', '--quiet') `
        -WorkingDirectory (Join-Path $DevCheckRoot 'rust/logic') -Tail 90
    if ($r.ExitCode -ne 0) { throw '行为断言失败，见上方输出' }
    $summary = ($r.Output -split "`r?`n" | Where-Object { $_ -match '====' } | Select-Object -Last 1)
    if (-not $summary) { $summary = '断言全部通过' }
    return $summary.Trim()
}

function Test-NativeDeps {
    # vendored 的 rcedit-sys 带 C++（rescle.cc / librcedit.cpp），只能靠 MSVC 编。
    # 这一层在有 cl.exe 的机器上真编一遍，让工具链/C++ 侧的变化在自动跑的 Devcheck
    # 里就暴露，不必等手动触发 Build。踩过的具体那一次：vendor 目录的 LOCAL_PATCHES.md。
    $crate = Join-Path $RepoRoot 'installer/kachina/vendor/rcedit-rs/rcedit-sys'
    if (-not (Test-Path -LiteralPath (Join-Path $crate 'Cargo.toml'))) {
        throw "找不到 $crate（vendored 副本被删了？）"
    }
    $cargo = Get-Tool 'cargo'
    if (-not $cargo) { Skip-Layer 'cargo 不在 PATH' }
    if (-not (Get-Tool 'cl')) { Skip-Layer 'cl.exe 不在 PATH（需要 Windows + MSVC 开发环境）' }

    # 独立 target 目录：既不污染 kachina 自己的构建产物，也不打乱 CI 的 cargo 缓存
    $prev = $env:CARGO_TARGET_DIR
    $env:CARGO_TARGET_DIR = (Join-Path $DevCheckRoot 'rust/native/target')
    try {
        $r = Invoke-Native -FilePath $cargo `
            -Arguments @('build', '--manifest-path', (Join-Path $crate 'Cargo.toml')) `
            -WorkingDirectory $crate -Tail 25
    }
    finally {
        if ($null -eq $prev) { Remove-Item Env:\CARGO_TARGET_DIR -ErrorAction SilentlyContinue }
        else { $env:CARGO_TARGET_DIR = $prev }
    }
    if ($r.ExitCode -ne 0) { throw 'rcedit-sys 编译失败（C++ 或 Rust 侧，详见上面的 cl.exe / cargo 输出）' }
    return 'vendored rcedit-sys 的 rescle.cc + librcedit.cpp 用 MSVC 编译通过'
}

function Test-Frontend {
    $node = Get-Tool 'node'
    if (-not $node) { Skip-Layer 'node 不在 PATH' }
    $npm = Get-Tool 'npm'
    if (-not $npm) { Skip-Layer 'npm 不在 PATH' }

    $front = Join-Path $DevCheckRoot 'front'
    if (-not (Test-Path (Join-Path $front 'node_modules'))) {
        if ($SkipInstall) { Skip-Layer 'front/node_modules 不存在（去掉 -SkipInstall 可自动 npm install）' }
        Write-Info '首次运行：安装前端最小依赖（typescript/dompurify/vue/@vue/compiler-sfc/prettier）…'
        $r = Invoke-Native -FilePath $npm -Arguments @('install', '--no-audit', '--no-fund', '--prefer-offline') `
            -WorkingDirectory $front -Tail 10
        if ($r.ExitCode -ne 0) { throw 'npm install 失败' }
    }

    $npx = Get-Tool 'npx'
    if (-not $npx) {
        $ext = if ($script:IsWin) { '.cmd' } else { '' }
        $npx = Join-Path (Split-Path -Parent $npm) "npx$ext"
    }

    $checks = @(
        @{ What = 'tsc --noEmit（agreement.ts / types.ts）'; Exe = $npx; Args = @('tsc', '--noEmit', '-p', 'tsconfig.json') }
        @{ What = '.vue 单文件组件编译'; Exe = $node; Args = @('sfccheck.mjs') }
        # 只查我们自己写的文件（types.ts / App.vue 是上游代码，本身不符合仓库的 prettier 风格）。
        # --end-of-line auto：换行统一由 .gitattributes 管，别让旧工作区的 CRLF 淹掉真问题，
        # 详见 README.md「跨平台的坑」。
        @{ What = 'prettier --check'; Exe = $npx; Args = @('prettier', '--check', '--end-of-line', 'auto', 'gen/src/utils/agreement.ts') }
    )
    foreach ($c in $checks) {
        $r = Invoke-Native -FilePath $c.Exe -Arguments $c.Args -WorkingDirectory $front -Tail 30
        if ($r.ExitCode -ne 0) { throw "$($c.What) 失败" }
    }
    return 'tsc --strict / 全部 .vue SFC / prettier 均通过'
}

function Test-Host {
    $dotnet = Get-Tool 'dotnet'
    if (-not $dotnet) { Skip-Layer 'dotnet 不在 PATH（.NET 9 SDK）' }
    $proj = Join-Path $RepoRoot 'src/Host/GenshinFpsUnlocker.Host.csproj'
    if (-not (Test-Path -LiteralPath $proj)) { throw "找不到 $proj" }
    $r = Invoke-Native -FilePath $dotnet `
        -Arguments @('build', $proj, '-c', 'Release', '-p:EnableWindowsTargeting=true', '--nologo', '-v', 'q') `
        -WorkingDirectory $RepoRoot -Tail 30
    if ($r.ExitCode -ne 0) { throw 'dotnet build 失败' }
    $warnLine = ($r.Output -split "`r?`n" | Where-Object { $_ -match 'Warning' } | Select-Object -First 1)
    if ($warnLine) { return $warnLine.Trim() }
    return 'Host 构建通过'
}

function Test-Ui {
    $npm = Get-Tool 'npm'
    if (-not $npm) { Skip-Layer 'npm 不在 PATH' }
    $ui = Join-Path $RepoRoot 'src/Ui'
    if (-not (Test-Path (Join-Path $ui 'node_modules'))) {
        Skip-Layer 'src/Ui/node_modules 不存在（先 cd src/Ui && npm install）'
    }
    $r = Invoke-Native -FilePath $npm -Arguments @('run', 'build') -WorkingDirectory $ui -Tail 30
    if ($r.ExitCode -ne 0) { throw 'src/Ui 构建失败' }
    return 'src/Ui vite build 通过'
}
