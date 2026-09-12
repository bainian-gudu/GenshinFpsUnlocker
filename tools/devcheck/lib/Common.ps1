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
