# devcheck 基础设施：工具查找、输出、层执行与原生进程封装（被 devcheck.ps1 dot-source）
#
# Invoke-Layer 负责计时 / 捕获异常 / 记录结果；Skip-Layer 抛 LayerSkipped 表示「本层不适用」
# （例如非 Windows 上跳过原生编译），由 Invoke-Layer 统一记为 SKIP 而不是失败。

$script:Results = [System.Collections.Generic.List[object]]::new()
$script:SkipCount = 0

function Get-Tool {
    param([Parameter(Mandatory)][string]$Name)
    $cmd = Get-Command $Name -ErrorAction SilentlyContinue
    if ($cmd) {
        $src = $cmd.Source
        # Windows 上 npm/npx 会同时有 .ps1 与 .cmd；ProcessStartInfo 不能直接执行 .ps1
        if ($script:IsWin -and $src -like '*.ps1') {
            $cmdExe = Join-Path (Split-Path -Parent $src) ($Name + '.cmd')
            if (Test-Path -LiteralPath $cmdExe) { return $cmdExe }
        }
        return $src
    }
    $ext = if ($script:IsWin) { '.exe' } else { '' }
    foreach ($c in @((Join-Path $HOME ".cargo/bin/$Name$ext"), (Join-Path $HOME ".dotnet/$Name$ext"))) {
        if (Test-Path -LiteralPath $c) { return $c }
    }
    return $null
}

function Write-Step { param([string]$Text) Write-Host "── $Text" -ForegroundColor Cyan }
function Write-Ok   { param([string]$Text) Write-Host "   $Text" -ForegroundColor Green }
function Write-Info { param([string]$Text) Write-Host "   $Text" -ForegroundColor DarkGray }
function Write-Bad  { param([string]$Text) Write-Host "   $Text" -ForegroundColor Red }

function Skip-Layer { param([string]$Reason) throw [LayerSkipped]::new($Reason) }

# ---------------------------------------------------------------------------
# 仓库改动锁 / 备份
#
# -SelfTest 会临时改写仓库里的真实文件（.gitmodules、工作流、registry.rs …）来验证各层
# 真会报错。两个进程同时跑就会互相看到对方的注入，出现「检查报错但其实是被别人改的」
# 这种假失败；进程被杀时还会把注入留在工作区。这里用原子创建的锁文件互斥，
# 并把原始内容落到磁盘备份，下一个进程起来时能恢复被杀进程留下的改动。
# ---------------------------------------------------------------------------
function Get-RepoLockPath { Join-Path $DevCheckRoot '.repo-lock' }
function Get-SelfTestBackupDir { Join-Path $DevCheckRoot '.selftest-backup' }

function Enter-RepoLock {
    $lock = Get-RepoLockPath
    $mine = "{0}-{1}" -f $PID, ([guid]::NewGuid().ToString('N').Substring(0, 8))
    $tmp = "$lock.$mine.tmp"
    try {
        # 先写临时文件再原子改名：拿到锁的进程一定读到完整的 PID，不会读到空文件。
        Set-Content -LiteralPath $tmp -Value "$PID`n$mine" -Encoding ascii
        [System.IO.File]::Move($tmp, $lock)
    }
    catch [System.IO.IOException] {
        Remove-Item -LiteralPath $tmp -Force -ErrorAction SilentlyContinue
        $holder = '（读不到持有者 PID）'
        try {
            $lines = @(Get-Content -LiteralPath $lock -ErrorAction Stop)
            if ($lines.Count) { $holder = "PID $($lines[0])" }
        }
        catch { }
        throw "另一个 devcheck 正持有仓库改动锁（$holder）。-SelfTest 会临时改写仓库文件，" +
              '两个进程同时跑会互相污染；等它结束后重试，或删掉 ' + $lock
    }
    finally {
        Remove-Item -LiteralPath $tmp -Force -ErrorAction SilentlyContinue
    }
}

function Exit-RepoLock {
    $lock = Get-RepoLockPath
    if (-not (Test-Path -LiteralPath $lock)) { return }
    try {
        $lines = @(Get-Content -LiteralPath $lock -ErrorAction Stop)
        # 只删自己那把；别人重新拿到的锁不能被我误删。
        if (-not $lines.Count -or [int]$lines[0] -ne $PID) { return }
        Remove-Item -LiteralPath $lock -Force -ErrorAction SilentlyContinue
    }
    catch { }
}

# 仓库内文件的改动前备份：备份名由仓库相对路径转义而来（禁用字符替换成 '_'），
# 因此同一个文件在任何进程里都映射到同一份备份，崩溃后还能被下一个进程找到。
function Get-RepoBackupPath {
    param([Parameter(Mandatory)][string]$Path)
    $dir = Get-SelfTestBackupDir
    New-Item -ItemType Directory -Path $dir -Force | Out-Null
    $rel = [System.IO.Path]::GetRelativePath($RepoRoot, $Path)
    $invalid = [System.IO.Path]::GetInvalidFileNameChars() -join ''
    $name = ($rel -replace "[$([regex]::Escape($invalid))]", '_')
    return Join-Path $dir $name
}

function Backup-RepoFile {
    param([Parameter(Mandatory)][string]$Path)
    $backup = Get-RepoBackupPath -Path $Path
    if (Test-Path -LiteralPath $Path) {
        Copy-Item -LiteralPath $Path -Destination $backup -Force
    }
    else {
        # 备份不存在 + 原文件当时不存在 = 这个文件是注入创建的，清理时直接删。
        Remove-Item -LiteralPath $backup -Force -ErrorAction SilentlyContinue
    }
    return $backup
}

function Restore-RepoFile {
    param([Parameter(Mandatory)][string]$Backup, [Parameter(Mandatory)][string]$Path)
    if (Test-Path -LiteralPath $Backup) {
        Copy-Item -LiteralPath $Backup -Destination $Path -Force
    }
    else {
        Remove-Item -LiteralPath $Path -Force -ErrorAction SilentlyContinue
    }
}

# -SelfTest 注入过的仓库内文件；下一次运行（哪怕上一次是被杀掉）先按它清场。
$script:SelfTestRepoFiles = @(
    '.gitmodules',
    '.github/workflows/zz-devcheck-selftest.yml',
    'installer/kachina/src/devcheck-selftest-telemetry.ts',
    'installer/kachina/src-tauri/src/utils/mod.rs',
    'installer/kachina/src-tauri/Cargo.toml',
    'installer/kachina/src-tauri/src/installer/registry.rs',
    'installer/kachina/vendor/rcedit-rs/rcedit-sys/src/rescle.cc',
    'tools/ci/Import-DevCmd.ps1',
    'src/Host/ProcessRunner.cs'
)

function Clear-RepoMutations {
    param([switch]$Quiet)
    # 内容一致说明该用例自己的 Cleanup 已经还原过了，不必再报「被中断」。
    # 只有内容确实不一样时才是上一次运行留下的注入。
    $restored = 0
    foreach ($rel in $script:SelfTestRepoFiles) {
        $path = Join-Path $RepoRoot $rel
        $backup = Get-RepoBackupPath -Path $path
        if (-not (Test-Path -LiteralPath $backup)) { continue }
        $same = (Test-Path -LiteralPath $path) -and
            ([System.IO.File]::ReadAllText($path) -eq [System.IO.File]::ReadAllText($backup))
        if (-not $same) {
            Restore-RepoFile -Backup $backup -Path $path
            $restored++
            if (-not $Quiet) { Write-Info "已从 -SelfTest 备份恢复 $rel（上次运行被中断留下的注入）" }
        }
    }
    if ($restored -and -not $Quiet) { Write-Info "共恢复 $restored 个被中断注入的文件" }
}

function Invoke-Layer {
    param(
        [Parameter(Mandatory)][string]$Name,
        [Parameter(Mandatory)][scriptblock]$Body
    )
    Write-Step $Name
    $sw = [System.Diagnostics.Stopwatch]::StartNew()
    $status = 'PASS'
    $detail = ''
    try {
        $raw = & $Body
        if ($null -ne $raw) { $detail = ($raw | Out-String).Trim() }
    }
    catch [LayerSkipped] {
        $status = 'SKIP'
        $detail = $_.Exception.Message
        $script:SkipCount++
        Write-Info "SKIP: $detail"
    }
    catch {
        $status = 'FAIL'
        $detail = $_.Exception.Message
        Write-Bad $detail
    }
    $sw.Stop()
    if ($status -eq 'PASS') {
        $suffix = if ($detail) { " — $detail" } else { '' }
        Write-Ok ("通过 ({0:n1}s){1}" -f $sw.Elapsed.TotalSeconds, $suffix)
    }
    $script:Results.Add([pscustomobject]@{
            Layer   = $Name.Split(' ')[0]
            Status  = $status
            Seconds = [math]::Round($sw.Elapsed.TotalSeconds, 1)
            Detail  = if ($detail.Length -gt 110) { $detail.Substring(0, 110) + '…' } else { $detail }
        })
}

# 跑外部命令：实时回显尾部输出，返回 (ExitCode, Output)
function Invoke-Native {
    param(
        [Parameter(Mandatory)][string]$FilePath,
        [string[]]$Arguments = @(),
        [string]$WorkingDirectory = $RepoRoot,
        [int]$Tail = 40,
        [int]$TimeoutSec = 600
    )
    Write-Info "`$ $(Split-Path -Leaf $FilePath) $($Arguments -join ' ')"
    $psi = [System.Diagnostics.ProcessStartInfo]::new()
    $psi.FileName = $FilePath
    $psi.WorkingDirectory = $WorkingDirectory
    $psi.RedirectStandardOutput = $true
    $psi.RedirectStandardError = $true
    $psi.UseShellExecute = $false
    foreach ($a in $Arguments) { $psi.ArgumentList.Add($a) }

    $proc = [System.Diagnostics.Process]::Start($psi)
    # stdout / stderr 必须并发读：串行读在 Windows 上会死锁（管道缓冲只有 4 KB）。
    # 完整原因与本地复现方法见同目录 README.md「跨平台的坑」。
    $outTask = $proc.StandardOutput.ReadToEndAsync()
    $errTask = $proc.StandardError.ReadToEndAsync()
    if (-not $proc.WaitForExit($TimeoutSec * 1000)) {
        try { $proc.Kill($true) } catch { }
        throw ("{0} 超过 {1} 秒仍未结束，已终止（疑似卡死或在等交互输入）" -f
            (Split-Path -Leaf $FilePath), $TimeoutSec)
    }
    $stdout = ''; $stderr = ''
    try { $stdout = $outTask.Result } catch { }
    try { $stderr = $errTask.Result } catch { }

    # 并发读的两个流分别读完再拼接，stderr 一律排在 stdout 后面（时间顺序会错位），
    # 所以两边都非空时插一行分隔说明。详见 README.md「日志里哪些 Warning / error 是正常的」。
    $parts = @()
    if ($stdout.Trim()) { $parts += $stdout.Trim() }
    if ($stderr.Trim()) {
        if ($parts.Count) { $parts += '──── 以上 stdout / 以下 stderr（顺序不代表先后） ────' }
        $parts += $stderr.Trim()
    }
    $all = ($parts -join "`n")
    if ($all) {
        $lines = $all -split "`r?`n"
        $shown = if ($lines.Count -gt $Tail) { @("…(省略 $($lines.Count - $Tail) 行)") + $lines[-$Tail..-1] } else { $lines }
        foreach ($l in $shown) { Write-Host "   | $l" -ForegroundColor DarkGray }
    }
    return [pscustomobject]@{ ExitCode = $proc.ExitCode; Output = $all }
}
