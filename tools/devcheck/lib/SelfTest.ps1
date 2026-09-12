# devcheck 自检（被 devcheck.ps1 dot-source）
#
# 证明这套检查不是空壳：先正常生成一次，然后往**生成物**里注入错误（仓库源码一个字都不改），
# 逐个确认对应层会失败。任何一层「注入了错误却没报错」= 自检失败。

function Invoke-SelfTest {
    $front = Join-Path $DevCheckRoot 'front'
    $tmpDir = Join-Path $DevCheckRoot '_selftest'
    $cases = [System.Collections.Generic.List[object]]::new()

    function Add-Case {
        param([string]$Name, [scriptblock]$Mutate, [scriptblock]$Run, [scriptblock]$Cleanup = {})
        $cases.Add([pscustomobject]@{ Name = $Name; Mutate = $Mutate; Run = $Run; Cleanup = $Cleanup })
    }

    # --- 0a) vendor：出现 .gitmodules 就必须报错（kachina 不能是 submodule）---
    $gitmodules = Join-Path $RepoRoot '.gitmodules'
    Add-Case 'vendor 层能抓到 kachina 变成 submodule' `
        -Mutate {
            Set-Content -Path $gitmodules -Encoding utf8 -Value @'
[submodule "installer/kachina"]
	path = installer/kachina
	url = https://example.invalid/upstream.git
'@
        } `
        -Run { Test-VendoredSource } `
        -Cleanup { if (Test-Path -LiteralPath $gitmodules) { Remove-Item -LiteralPath $gitmodules -Force } }

    # --- 0b) vendor：工作流里出现从外部拉取的动作就必须报错 ---
    $badWorkflow = Join-Path $RepoRoot '.github/workflows/zz-devcheck-selftest.yml'
    Add-Case 'vendor 层能抓到工作流从上游拉取' `
        -Mutate {
            Set-Content -Path $badWorkflow -Encoding utf8 -Value @'
name: selftest
on: workflow_dispatch
jobs:
  x:
    runs-on: ubuntu-latest
    steps:
      - run: Invoke-WebRequest https://example.invalid/kachina-builder.exe -OutFile installer/tools/kachina-builder.exe
'@
        } `
        -Run { Test-VendoredSource } `
        -Cleanup { if (Test-Path -LiteralPath $badWorkflow) { Remove-Item -LiteralPath $badWorkflow -Force } }

    # --- 0c) vendor：rescle.cc 里再出现 locale::empty() 就必须报错 ---
    #     这是 vendored 副本里唯一的「MSVC 版本敏感」代码，用注释形式注入不算
    #     （检查会剥掉 // 注释），所以注入一行真代码。
    $rescleFile = Join-Path $RepoRoot 'installer/kachina/vendor/rcedit-rs/rcedit-sys/src/rescle.cc'
    Add-Case 'vendor 层能抓到 rescle.cc 用回 locale::empty()' `
        -Mutate {
            $script:RescleBackup = [System.IO.File]::ReadAllText($rescleFile)
            Add-Content -Path $rescleFile -Encoding utf8 `
                -Value "`nstatic void _devcheck_selftest() { std::locale l(std::locale::empty()); (void)l; }"
        } `
        -Run { Test-VendoredSource } `
        -Cleanup {
            if ($script:RescleBackup) {
                [System.IO.File]::WriteAllText($rescleFile, $script:RescleBackup)
                $script:RescleBackup = $null
            }
        }

    # --- 1) ps1：临时放一个语法错误的 .ps1 进仓库 ---
    Add-Case 'ps1 层能抓到 PowerShell 语法错误' `
        -Mutate {
            New-Item -ItemType Directory -Path $tmpDir -Force | Out-Null
            Set-Content -Path (Join-Path $tmpDir 'broken.ps1') -Value 'if ($x { Write-Host "unclosed" ' -Encoding utf8
        } `
        -Run { Test-Ps1Syntax }

    # --- 2) rust：往生成的 uninstall.rs 里塞一个类型错误 ---
    Add-Case 'rust 层能抓到 Rust 类型错误' `
        -Mutate {
            $f = Join-Path $DevCheckRoot 'rust/typecheck/src/gen/uninstall.rs'
            Add-Content -Path $f -Value "`nfn _devcheck_selftest() { let _x: u32 = `"不是数字`"; }" -Encoding utf8
        } `
        -Run { Test-RustTypecheck }

    # --- 3) logic：把注册表安全阀的深度要求从 2 段放宽到 1 段 ---
    # 用 1 而不是 0：usize >= 0 恒真，编译器会多打一条无用的 comparison 告警（噪音），
    # 放宽的效果一样。详见 README.md「日志里哪些 Warning / error 是正常的」。
    Add-Case 'logic 层能抓到安全阀被放宽' `
        -Mutate {
            $f = Join-Path $DevCheckRoot 'rust/logic/src/gen/extracted.rs'
            $t = [System.IO.File]::ReadAllText($f)
            $t2 = $t.Replace('Some(_) => segments.len() >= 2,', 'Some(_) => segments.len() >= 1,')
            if ($t2 -eq $t) { throw '注入失败：没找到 is_safe_registry_target 的深度判断（上游改了？）' }
            Write-GeneratedFile -Path $f -Content $t2
        } `
        -Run { Test-RustLogic }

    # --- 4) front/ts：往生成的 agreement.ts 里塞一个类型错误 ---
    Add-Case 'front 层能抓到 TypeScript 类型错误' `
        -Mutate {
            $f = Join-Path $front 'gen/src/utils/agreement.ts'
            Add-Content -Path $f -Value "`nexport const _devcheckSelfTest: number = 'not a number';" -Encoding utf8
        } `
        -Run {
            $npx = Get-Tool 'npx'
            if (-not $npx) { Skip-Layer 'npx 不在 PATH' }
            $r = Invoke-Native -FilePath $npx -Arguments @('tsc', '--noEmit', '-p', 'tsconfig.json') -WorkingDirectory $front -Tail 10
            if ($r.ExitCode -ne 0) { throw 'tsc 报错（符合预期）' }
        }

    # --- 5) front/sfc：一个 template 不闭合的 .vue ---
    Add-Case 'front 层能抓到 .vue 模板错误' `
        -Mutate {
            $d = Join-Path $front '_selftest'
            New-Item -ItemType Directory -Path $d -Force | Out-Null
            Set-Content -Path (Join-Path $d 'Broken.vue') -Encoding utf8 -Value @'
<template>
  <div class="x">
    <span>未闭合
</template>
<script setup lang="ts">
const a: number = 1;
</script>
'@
        } `
        -Run {
            $node = Get-Tool 'node'
            if (-not $node) { Skip-Layer 'node 不在 PATH' }
            $r = Invoke-Native -FilePath $node -Arguments @('sfccheck.mjs', '_selftest') -WorkingDirectory $front -Tail 10
            if ($r.ExitCode -ne 0) { throw 'SFC 编译报错（符合预期）' }
        }

    Write-Step 'selftest 注入错误自检'
    $caught = 0; $missed = 0; $skipped = 0; $idx = 0
    foreach ($c in $cases) {
        $idx++
        # 每次都从干净的生成物开始
        New-GenSources | Out-Null
        try {
            & $c.Mutate
        }
        catch {
            $missed++
            Write-Bad "  ✗ $($c.Name) —— 注入失败: $($_.Exception.Message)"
            continue
        }
        Write-Host "  ── 注入 $idx/$($cases.Count)：$($c.Name)" -ForegroundColor DarkYellow
        Write-Host '     ↓ 接下来这段报错是故意注入的，看到它才说明这层没被架空' -ForegroundColor DarkGray
        $outcome = 'CAUGHT'
        try {
            & $c.Run | Out-Null
            $outcome = 'MISSED'
        }
        catch [LayerSkipped] { $outcome = 'SKIPPED' }
        catch { $outcome = 'CAUGHT' }
        try { & $c.Cleanup } catch { }

        switch ($outcome) {
            'CAUGHT'  { $caught++;  Write-Ok "  ✓ $($c.Name)" }
            'SKIPPED' { $skipped++; Write-Info "  - $($c.Name)（缺工具链，跳过）" }
            'MISSED'  { $missed++;  Write-Bad "  ✗ $($c.Name) —— 注入了错误却没报错，这层是空壳！" }
        }
    }

    # 收尾：删掉临时目录并恢复干净的生成物
    foreach ($d in @($tmpDir, (Join-Path $front '_selftest'))) {
        if (Test-Path -LiteralPath $d) { Remove-Item -LiteralPath $d -Recurse -Force }
    }
    foreach ($f in @((Join-Path $RepoRoot '.gitmodules'),
                     (Join-Path $RepoRoot '.github/workflows/zz-devcheck-selftest.yml'))) {
        if (Test-Path -LiteralPath $f) { Remove-Item -LiteralPath $f -Force }
    }
    New-GenSources | Out-Null

    Write-Host ''
    if ($missed) {
        Write-Host "✗ 自检失败：$missed 个注入错误没被抓到" -ForegroundColor Red
        return $false
    }
    Write-Host "✓ 自检通过：$caught 个注入错误全部被抓到$(if ($skipped) { "，$skipped 个因缺工具链跳过" } else { '' })" -ForegroundColor Green
    return $true
}
