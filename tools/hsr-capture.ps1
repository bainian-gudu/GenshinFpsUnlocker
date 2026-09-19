#Requires -Version 5.1
<#
.SYNOPSIS
  采集崩坏：星穹铁道「解除角色虚化 / 隐藏 UID 水印」注入模块所需的材料。

.DESCRIPTION
  纯只读采集：不改游戏目录里的任何文件、不写注册表、不注入进程。
  复制出来的材料统一落到 WSL 工作区（默认 ..\..\hsr-capture），供 WSL 内离线分析。

  采集内容：
    1. StarRail.exe / GameAssembly.dll / UnityPlayer.dll 的路径、版本、大小、SHA256
    2. GameAssembly.dll 与 global-metadata.dat 的副本（Il2CppDumper 离线分析用）
    3. 进程模块列表 + 反作弊驱动状态（模块列表会被 HoYoProtect 拒绝，见 NOTES）
    4. HKCU\Software\miHoYo\崩坏：星穹铁道 下的 GraphicsSettings_*（含 GraphicsSettings_Model_h* 的 FPS）
    5. %LocalLow%\miHoYo\Star Rail 下 Unity 日志的最后若干行（确认日志目录与 Unity 版本）

.EXAMPLE
  # 推荐：先启动游戏进到有 UID 水印的界面，再跑一次（能采到模块列表）
  powershell.exe -NoProfile -ExecutionPolicy Bypass -File "\\wsl.localhost\Ubuntu\home\tushanhonghong\workspace\GenshinFpsUnlocker-main\tools\hsr-capture.ps1"

.EXAMPLE
  # 也可以直接双击 hsr-capture.cmd（同一个脚本的免命令入口）

.EXAMPLE
  # 安装位置不常规时手动指定
  powershell.exe -NoProfile -ExecutionPolicy Bypass -File ".\hsr-capture.ps1" -GamePath "D:\Games\Star Rail\Game\StarRail.exe"

.NOTES
  只用 Windows 自带的 Windows PowerShell 5.1（Win10/11 预装）+ 系统 API，不需要安装任何东西。
  实测（2026-09-19）：星铁的 HoYoProtect 内核驱动在运行时，跨进程模块枚举一律被拒 ——
  Process.Modules 返回空集合、Toolhelp32 报 win32 error 5（Access is denied），提权也没用。
  所以 modules.txt 里通常只剩反作弊驱动状态；模块基址由 Stub 在进程内 GetModuleHandle 自己取。

  注意：本文件必须保存为「UTF-8 带 BOM」。Windows PowerShell 5.1 会把无 BOM 的脚本按
  系统 ANSI 代码页解码，脚本里的中文注册表路径与进程匹配会变成乱码（PowerShell 7 无此问题，
  所以本仓库其它 .ps1 可以不带 BOM，这个脚本不行）。

  隐私：注册表键下除了 GraphicsSettings_* 还有几百个账号 / 任务进度值，默认不导出，
  需要全量时加 -FullRegistry。unity-log-tail.txt 是游戏自己的日志，可能含账号 ID 与安装路径，
  往外发之前留意一下。
#>
[CmdletBinding()]
param(
    # StarRail.exe 完整路径；不传则自动定位（运行中进程 → 卸载注册表 → 常见目录）
    [string]$GamePath,
    # 输出目录；默认落到仓库上一级的 hsr-capture（即 WSL 工作区内）
    [string]$OutDir,
    # 只采小文件（版本/注册表/日志/模块），不复制两个大文件
    [switch]$SkipBinaries,
    # 注册表默认只导出 GraphicsSettings_*（这个键下其余是账号/任务进度等个人数据）
    [switch]$FullRegistry
)

$ErrorActionPreference = 'Continue'

# 输出被重定向（WSL 侧调用、> 存文件）时统一按 UTF-8 写，避免中文变乱码；
# 直接跑在 Windows 控制台时不动它，跟随控制台代码页（.cmd 入口已经 chcp 65001）。
if ([Console]::IsOutputRedirected) {
    try { [Console]::OutputEncoding = New-Object System.Text.UTF8Encoding($false) } catch { }
}

function Write-Step([string]$text) {
    Write-Host ""
    Write-Host "==> $text" -ForegroundColor Cyan
}

function Write-Item([string]$text) {
    Write-Host "    $text"
}

function Get-Sha256([string]$path) {
    try { return (Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash } catch { return "(失败: $($_.Exception.Message))" }
}

function Get-FileVersionText([string]$path) {
    try {
        $info = [System.Diagnostics.FileVersionInfo]::GetVersionInfo($path)
        return "$($info.FileVersion) / product=$($info.ProductVersion) / desc=$($info.FileDescription)"
    } catch { return "(失败: $($_.Exception.Message))" }
}

# 统一写 UTF-8（无 BOM）文本，便于 WSL 侧直接 grep
function Write-TextFile([string]$path, $lines) {
    $encoding = New-Object System.Text.UTF8Encoding($false)
    try { [System.IO.File]::WriteAllLines($path, [string[]]$lines, $encoding) }
    catch { Write-Host "写文件失败：$path —— $($_.Exception.Message)" -ForegroundColor Yellow }
}

# Toolhelp32 模块枚举源码。Process.Modules 被反作弊挡掉时用它重试；
# Add-Type 用的是系统自带的 .NET Framework 编译器，同样不需要安装任何东西。
$toolhelpSource = @'
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Runtime.InteropServices;

public static class HsrModEnum
{
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct MODULEENTRY32W
    {
        public uint dwSize;
        public uint th32ModuleID;
        public uint th32ProcessID;
        public uint GlblcntUsage;
        public uint ProccntUsage;
        public IntPtr modBaseAddr;
        public uint modBaseSize;
        public IntPtr hModule;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)] public string szModule;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)] public string szExePath;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr CreateToolhelp32Snapshot(uint dwFlags, uint th32ProcessID);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool Module32FirstW(IntPtr hSnapshot, ref MODULEENTRY32W lpme);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool Module32NextW(IntPtr hSnapshot, ref MODULEENTRY32W lpme);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(IntPtr hObject);

    private const uint TH32CS_SNAPMODULE = 0x00000008;
    private const uint TH32CS_SNAPMODULE32 = 0x00000010;

    public static string[] List(int pid)
    {
        List<string> result = new List<string>();
        IntPtr snapshot = CreateToolhelp32Snapshot(TH32CS_SNAPMODULE | TH32CS_SNAPMODULE32, (uint)pid);
        if (snapshot == new IntPtr(-1))
        {
            throw Describe("CreateToolhelp32Snapshot", Marshal.GetLastWin32Error());
        }
        try
        {
            MODULEENTRY32W entry = new MODULEENTRY32W();
            entry.dwSize = (uint)Marshal.SizeOf(typeof(MODULEENTRY32W));
            if (!Module32FirstW(snapshot, ref entry))
            {
                throw Describe("Module32FirstW", Marshal.GetLastWin32Error());
            }
            do
            {
                result.Add(string.Format("{0,-32} 0x{1:X16} {2,12}  {3}",
                    entry.szModule, entry.modBaseAddr.ToInt64(), entry.modBaseSize, entry.szExePath));
                entry.dwSize = (uint)Marshal.SizeOf(typeof(MODULEENTRY32W));
            }
            while (Module32NextW(snapshot, ref entry));
        }
        finally
        {
            CloseHandle(snapshot);
        }
        return result.ToArray();
    }

    private static Exception Describe(string api, int error)
    {
        return new Exception(api + " failed, win32 error " + error + " (" + new Win32Exception(error).Message + ")");
    }
}
'@

# ---------------------------------------------------------------- 输出目录
if (-not $OutDir) {
    # 脚本在 <workspace>\GenshinFpsUnlocker-main\tools\ 下，采集结果放到 <workspace>\hsr-capture\
    $repoRoot = Split-Path -Parent $PSScriptRoot
    $OutDir = Join-Path (Split-Path -Parent $repoRoot) 'hsr-capture'
}
$binDir = Join-Path $OutDir 'bin'
try {
    New-Item -ItemType Directory -Force -Path $OutDir, $binDir | Out-Null
} catch {
    Write-Host "输出目录不可用：$OutDir" -ForegroundColor Red
    Write-Host "  $($_.Exception.Message)" -ForegroundColor Red
    Write-Host "  用 -OutDir 指定一个可写目录，例如：-OutDir `"$env:USERPROFILE\Desktop\hsr-capture`"" -ForegroundColor Yellow
    exit 3
}
$info = New-Object System.Collections.Generic.List[string]
$info.Add("崩坏：星穹铁道 采集报告")
$info.Add("时间(本机): " + (Get-Date).ToString('yyyy-MM-dd HH:mm:ss zzz'))
$info.Add("采集脚本: " + $PSCommandPath)
$info.Add("输出目录: " + $OutDir)
$info.Add("PowerShell: $($PSVersionTable.PSVersion) / $([System.Environment]::Is64BitProcess)")
$isAdmin = ([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
$info.Add("以管理员身份运行: $isAdmin")

# ---------------------------------------------------------------- 定位游戏
Write-Step "定位 StarRail.exe"
$candidates = New-Object System.Collections.Generic.List[string]
if ($GamePath) { $candidates.Add($GamePath) }

$process = Get-Process -Name 'StarRail' -ErrorAction SilentlyContinue | Select-Object -First 1
if ($process) {
    try { if ($process.Path) { $candidates.Add($process.Path) } } catch { Write-Item "读取运行中进程路径失败：$($_.Exception.Message)" }
    $startTime = '(读取失败)'
    try { $startTime = $process.StartTime } catch { /* 受保护进程可能拒绝 */ }
    $info.Add("运行中进程: PID=$($process.Id) 启动=$startTime")
} else {
    $info.Add("运行中进程: 未运行（模块列表与日志尾部会缺失，建议进游戏后再跑一次）")
}

foreach ($root in @('HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall',
                    'HKLM:\SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall',
                    'HKCU:\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall')) {
    Get-ChildItem $root -ErrorAction SilentlyContinue | ForEach-Object {
        $p = Get-ItemProperty $_.PSPath -ErrorAction SilentlyContinue
        if (-not $p) { return }
        $name = [string]$p.DisplayName
        if ($name -match '星穹铁道|Star\s?Rail') {
            $info.Add("卸载表命中: $name / InstallLocation=$($p.InstallLocation) / DisplayIcon=$($p.DisplayIcon)")
            foreach ($value in @($p.InstallLocation, $p.DisplayIcon)) {
                if (-not $value) { continue }
                $candidate = ([string]$value).Split(',')[0].Trim().Trim('"')
                if (Test-Path -LiteralPath $candidate -PathType Container) {
                    $candidates.Add((Join-Path $candidate 'StarRail.exe'))
                    $candidates.Add((Join-Path $candidate 'Game\StarRail.exe'))
                }
                elseif ($candidate -match '\.exe$' -and (Test-Path -LiteralPath $candidate -PathType Leaf)) {
                    $candidates.Add($candidate)
                }
                # DisplayIcon 常见是 .ico，只记进报告，绝不能当主程序用
            }
        }
    }
}

# 米哈游启动器 / HoYoPlay 自己记录的游戏安装路径（比卸载表可靠：卸载表的 DisplayIcon 是图标）
foreach ($hypRoot in @('HKCU:\Software\miHoYo\HYP',
                       'HKCU:\Software\Cognosphere\HYP',
                       'HKLM:\SOFTWARE\miHoYo\HYP',
                       'HKLM:\SOFTWARE\Cognosphere\HYP')) {
    if (-not (Test-Path -LiteralPath $hypRoot)) { continue }
    foreach ($sub in @(Get-ChildItem -LiteralPath $hypRoot -Recurse -ErrorAction SilentlyContinue)) {
        $recorded = (Get-ItemProperty -LiteralPath $sub.PSPath -Name 'GameInstallPath' -ErrorAction SilentlyContinue).GameInstallPath
        if (-not $recorded) { continue }
        $info.Add("启动器记录: $($sub.Name) → $recorded")
        $candidates.Add((Join-Path $recorded 'StarRail.exe'))
        $candidates.Add((Join-Path $recorded 'Game\StarRail.exe'))
    }
}

foreach ($drive in (Get-PSDrive -PSProvider FileSystem | Where-Object { $_.Root -match '^[A-Za-z]:\\$' })) {
    foreach ($relative in @('Star Rail\Game\StarRail.exe',
                            'Program Files\Star Rail\Game\StarRail.exe',
                            'Program Files\miHoYo Launcher\games\Star Rail Game\StarRail.exe',
                            'Program Files\HoYoPlay\games\Star Rail Game\StarRail.exe',
                            'HoYoPlay\games\Star Rail Game\StarRail.exe',
                            'miHoYo\Star Rail\Game\StarRail.exe',
                            '崩坏：星穹铁道\Game\StarRail.exe',
                            'Games\Star Rail\Game\StarRail.exe')) {
        $candidates.Add((Join-Path $drive.Root $relative))
    }
}

$exe = $null
foreach ($candidate in ($candidates | Where-Object { $_ } | Select-Object -Unique)) {
    # 自动发现的候选必须真的叫 StarRail.exe；显式 -GamePath 传进来的路径不卡名字
    if ($candidate -notmatch '\\StarRail\.exe$' -and $candidate -ne $GamePath) { continue }
    if (Test-Path -LiteralPath $candidate -PathType Leaf) { $exe = (Resolve-Path -LiteralPath $candidate).Path; break }
}
if (-not $exe) {
    Write-Host "未能自动找到 StarRail.exe，请用 -GamePath 指定完整路径。" -ForegroundColor Yellow
    Write-TextFile (Join-Path $OutDir 'info.txt') $info
    exit 2
}
$gameDir = Split-Path -Parent $exe
$dataDir = Join-Path $gameDir 'StarRail_Data'
Write-Item "游戏主程序: $exe"
$info.Add("游戏主程序: $exe")
$info.Add("主程序版本: " + (Get-FileVersionText $exe))
$info.Add("主程序大小: " + (Get-Item -LiteralPath $exe).Length + " 字节")
$info.Add("主程序 SHA256: " + (Get-Sha256 $exe))

# ---------------------------------------------------------------- 关键文件
Write-Step "关键文件（GameAssembly.dll / global-metadata.dat）"
$gameAssembly = Join-Path $gameDir 'GameAssembly.dll'
$metadata = Join-Path $dataDir 'il2cpp_data\Metadata\global-metadata.dat'
$unityPlayer = Join-Path $gameDir 'UnityPlayer.dll'
$hypBase = Join-Path $gameDir 'mhypbase.dll'
$appInfo = Join-Path $dataDir 'app.info'

Write-Item "计算 SHA256（GameAssembly.dll 有几百 MB，要等一会儿）"
foreach ($pair in @(@('GameAssembly.dll', $gameAssembly), @('global-metadata.dat', $metadata), @('UnityPlayer.dll', $unityPlayer), @('mhypbase.dll', $hypBase))) {
    $label = $pair[0]; $path = $pair[1]
    if (Test-Path -LiteralPath $path -PathType Leaf) {
        $item = Get-Item -LiteralPath $path
        Write-Item "$label : $($item.Length) 字节"
        $info.Add("$label : $path")
        $info.Add("$label 版本: " + (Get-FileVersionText $path))
        $info.Add("$label 大小: $($item.Length) 字节")
        $info.Add("$label SHA256: " + (Get-Sha256 $path))
        $info.Add("$label 修改时间: $($item.LastWriteTime)")
    } else {
        Write-Item "$label : 缺失（$path）"
        $info.Add("$label : 缺失（$path）")
    }
}

if (Test-Path -LiteralPath $appInfo -PathType Leaf) {
    # app.info 是 UTF-8；万一碰上 ANSI 的版本，出现替换字符就退回默认编码重读
    $appInfoLines = @(Get-Content -LiteralPath $appInfo -TotalCount 5 -Encoding UTF8 -ErrorAction SilentlyContinue)
    if (($appInfoLines -join '') -match [char]0xFFFD) {
        $appInfoLines = @(Get-Content -LiteralPath $appInfo -TotalCount 5 -ErrorAction SilentlyContinue)
    }
    $unityVersion = $appInfoLines -join ' | '
    Write-Item "app.info: $unityVersion"
    $info.Add("app.info: $unityVersion")
    Copy-Item -LiteralPath $appInfo -Destination (Join-Path $OutDir 'app.info') -Force
}

if (-not $SkipBinaries) {
    foreach ($pair in @(@($gameAssembly, 'GameAssembly.dll'), @($metadata, 'global-metadata.dat'))) {
        $src = $pair[0]; $name = $pair[1]
        if (-not (Test-Path -LiteralPath $src -PathType Leaf)) { continue }
        Write-Item "复制 $name → bin\（大文件，稍等）"
        Copy-Item -LiteralPath $src -Destination (Join-Path $binDir $name) -Force
    }
}

# ---------------------------------------------------------------- 进程模块
Write-Step "进程模块列表（GameAssembly / UnityPlayer / mhypbase）"
$moduleLines = New-Object System.Collections.Generic.List[string]
if ($process) {
    $dotnetModuleError = ''
    $toolhelpModuleError = ''

    # 1) 先试 .NET 的 Process.Modules。星铁反作弊会让它抛异常，或者更隐蔽地返回空集合，
    #    所以不能只看有没有报错，还要看实际拿到几条。
    try {
        foreach ($module in $process.Modules) {
            $moduleLines.Add(("{0,-32} 0x{1:X16} {2,12}  {3}" -f $module.ModuleName, $module.BaseAddress.ToInt64(), $module.ModuleMemorySize, $module.FileName))
        }
        if ($moduleLines.Count -gt 0) { Write-Item "Process.Modules：$($moduleLines.Count) 个模块" }
        else {
            $dotnetModuleError = '返回空集合，没有报错（反作弊常见）'
            Write-Item "Process.Modules 返回空集合（反作弊常见）"
        }
    } catch {
        $dotnetModuleError = $_.Exception.Message
        Write-Item "Process.Modules 失败（反作弊常见）：$dotnetModuleError"
    }

    # 2) 换 Toolhelp32 再试一次：另一套 API，可能绕过前面的限制。
    if ($moduleLines.Count -eq 0) {
        Write-Item "改用 Toolhelp32 重试……"
        try {
            Add-Type -TypeDefinition $toolhelpSource -Language CSharp -ErrorAction Stop
            foreach ($line in [HsrModEnum]::List($process.Id)) { $moduleLines.Add($line) }
            if ($moduleLines.Count -gt 0) { Write-Item "Toolhelp32：$($moduleLines.Count) 个模块" }
        } catch {
            $toolhelpModuleError = $_.Exception.Message
            Write-Item "Toolhelp32 也失败：$toolhelpModuleError"
        }
    }

    # 3) 两条路都不通时写清楚原因，别留一个空文件让人猜。
    if ($moduleLines.Count -eq 0) {
        $moduleLines.Add("两种方式都枚举不到模块：星铁的反作弊（HoYoKProtect / mhypbase）屏蔽了模块列表。")
        $moduleLines.Add("  Process.Modules: $dotnetModuleError")
        $moduleLines.Add("  Toolhelp32:      $toolhelpModuleError")
        $moduleLines.Add("  （本次是否管理员: $isAdmin）")
        $moduleLines.Add("这不影响静态分析 —— GameAssembly.dll 与 global-metadata.dat 已经在 bin\ 里。")
        $moduleLines.Add("这条数据本来也不是必需品：Stub 在游戏进程内用 GetModuleHandle 自己取基址，")
        $moduleLines.Add("采集它只是为了确认反作弊模块是否加载。真要看得用 x64dbg / Process Hacker。")
    }

    try {
        $commandLine = (Get-CimInstance Win32_Process -Filter "ProcessId=$($process.Id)" -ErrorAction Stop).CommandLine
        if ($commandLine) { $info.Add("命令行: $commandLine") }
    } catch { /* 反作弊常拒绝 */ }
} else {
    $moduleLines.Add("游戏未运行：本次没有模块信息。进游戏后再跑一次脚本即可。")
    Write-Item "游戏未运行，跳过（建议进游戏后再跑一次）"
}

# 反作弊驱动状态：这条不碰游戏进程，即使模块列表被挡也能采到。
# HoYoProtect 在跑 = 跨进程模块查询（PSAPI / Toolhelp32）注定被拒，属于预期。
try {
    $antiCheatDrivers = @(Get-CimInstance Win32_SystemDriver -ErrorAction SilentlyContinue |
        Where-Object { $_.Name -match 'HoYo|mhy|KProtect|AntiCheat' })
    if ($antiCheatDrivers.Count -gt 0) {
        $moduleLines.Add("")
        $moduleLines.Add("反作弊驱动（不需要访问游戏进程，始终可采）：")
        foreach ($driver in $antiCheatDrivers) {
            $moduleLines.Add("  $($driver.Name) = $($driver.State)  $($driver.PathName)")
        }
        Write-Item "反作弊驱动: $($antiCheatDrivers.Count) 个"
    }
} catch { }

Write-TextFile (Join-Path $OutDir 'modules.txt') $moduleLines

# ---------------------------------------------------------------- 注册表
Write-Step "注册表（HKCU\Software\miHoYo\崩坏：星穹铁道）"
$registryLines = New-Object System.Collections.Generic.List[string]
foreach ($keyPath in @('HKCU:\Software\miHoYo\崩坏：星穹铁道',
                       'HKCU:\Software\miHoYo\Star Rail',
                       'HKCU:\Software\miHoYo\HYP',
                       'HKCU:\Software\Cognosphere\Star Rail')) {
    if (-not (Test-Path -LiteralPath $keyPath)) {
        $registryLines.Add("[缺失] $keyPath")
        continue
    }
    $registryLines.Add("[$keyPath]")
    $key = Get-Item -LiteralPath $keyPath
    $skipped = 0
    foreach ($name in ($key.GetValueNames() | Sort-Object)) {
        # 默认只留图形设置：这个键下绝大多数是账号 / 任务进度等个人数据，没必要带走
        if (-not $FullRegistry -and $name -notlike 'GraphicsSettings_*') { $skipped++; continue }
        $value = $key.GetValue($name)
        try { $kind = $key.GetValueKind($name) } catch { $kind = 'Unknown' }
        # 游戏把设置写成 REG_BINARY，内容其实是 UTF-8 的 JSON（末尾还带 NUL），不是普通字符串
        if ($kind -eq 'Binary') {
            $text = [System.Text.Encoding]::UTF8.GetString([byte[]]$value).TrimEnd([char]0)
            $registryLines.Add("  $name = <$kind, $($value.Length) 字节> $text")
        } else {
            $text = [string]$value
            $registryLines.Add("  $name = $text")
        }
        if ($name -like 'GraphicsSettings_Model_h*') {
            try {
                $json = $text | ConvertFrom-Json
                $registryLines.Add("    → 解析后 FPS = $($json.FPS) / EnableVSync = $($json.EnableVSync)")
            } catch {
                $registryLines.Add("    → JSON 解析失败: $($_.Exception.Message)")
            }
        }
    }
    if ($skipped -gt 0) {
        $registryLines.Add("  （其余 $skipped 个与图形设置无关的值已省略；要全量导出加 -FullRegistry）")
    }
}
Write-TextFile (Join-Path $OutDir 'registry.txt') $registryLines
Write-Item "写入 registry.txt（默认只含 GraphicsSettings_*）"

# ---------------------------------------------------------------- Unity 日志
Write-Step "Unity 日志尾部（%LocalLow%\miHoYo\Star Rail）"
$localLow = Join-Path $env:USERPROFILE 'AppData\LocalLow\miHoYo'
$logFiles = @()
foreach ($folder in @('Star Rail', '崩坏：星穹铁道', 'StarRail')) {
    foreach ($file in @('Player.log', 'output_log.txt', 'Player-prev.log')) {
        $path = Join-Path (Join-Path $localLow $folder) $file
        if (Test-Path -LiteralPath $path -PathType Leaf) { $logFiles += $path }
    }
}
if ($logFiles.Count -eq 0) {
    Write-Item "没找到 Unity 日志（游戏至少成功启动过一次才会有）"
} else {
    $logOut = New-Object System.Collections.Generic.List[string]
    foreach ($file in $logFiles) {
        $logOut.Add("===== $file ($((Get-Item -LiteralPath $file).LastWriteTime)) =====")
        $tail = @(Get-Content -LiteralPath $file -Tail 200 -ErrorAction SilentlyContinue)
        if ($tail.Count -gt 0) { $logOut.AddRange([string[]]$tail) }
    }
    Write-TextFile (Join-Path $OutDir 'unity-log-tail.txt') $logOut
    Write-Item "写入 unity-log-tail.txt（$($logFiles.Count) 个文件）"
}

# ---------------------------------------------------------------- 收尾
$info.Add("")
$info.Add("下一步：把整个 $OutDir 目录交给 WSL 侧分析（objdump 看导出、Il2CppDumper 出 dump.cs）。")
Write-TextFile (Join-Path $OutDir 'info.txt') $info

Write-Step "完成"
Write-Host "采集结果: $OutDir" -ForegroundColor Green
Write-Host "  info.txt           版本 / 大小 / SHA256" -ForegroundColor Green
Write-Host "  bin\GameAssembly.dll, bin\global-metadata.dat" -ForegroundColor Green
Write-Host "  modules.txt        运行时模块基址（游戏运行时采集才有内容）" -ForegroundColor Green
Write-Host "  registry.txt       GraphicsSettings_Model_h* 与 FPS" -ForegroundColor Green
Write-Host "  unity-log-tail.txt Unity 日志尾部" -ForegroundColor Green
Write-Host ""
Write-Host "提示：如果这次游戏没在运行，进游戏（到有 UID 水印的界面）后再跑一次，模块列表就齐了。" -ForegroundColor Yellow
