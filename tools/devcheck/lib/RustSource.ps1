# Rust 源码抽取工具（被 devcheck.ps1 dot-source）
#
# 目的：不启动 tauri / 不装 Windows 工具链，也能对 installer/kachina 里被我们改过的
# Rust 逻辑做「编译期检查」和「行为断言」。
#
# 做法有两种，分别对应 tools/devcheck/rust 下的两个 crate：
#   typecheck/ —— 整文件塞进最小依赖 crate，cargo check --target x86_64-pc-windows-msvc
#   logic/     —— 按名字抽取若干 item（mock 掉 Windows 专有实现），在任意平台跑断言
#
# 抽取用「先把字符串/注释挖空，再做括号配对」的方式定位 item 边界，
# 比按下标切片稳：上游在文件里增删别的函数不会影响结果，找不到目标会直接抛错。

Set-StrictMode -Version Latest

# 把字符串字面量、字符字面量、注释里的内容替换成空格（保留长度与换行），
# 这样后续的括号配对不会被 "{"/"}"/";" 之类的字面量内容骗到。
function Get-RustMaskedText {
    param([Parameter(Mandatory)][AllowEmptyString()][string]$Text)

    $sb = [System.Text.StringBuilder]::new($Text.Length)
    $n = $Text.Length
    $i = 0
    while ($i -lt $n) {
        $c = $Text[$i]
        $next = if ($i + 1 -lt $n) { $Text[$i + 1] } else { [char]0 }

        # 行注释
        if ($c -eq '/' -and $next -eq '/') {
            while ($i -lt $n -and $Text[$i] -ne "`n") { [void]$sb.Append(' '); $i++ }
            continue
        }

        # 块注释（Rust 支持嵌套）
        if ($c -eq '/' -and $next -eq '*') {
            $depth = 0
            while ($i -lt $n) {
                $d = if ($i + 1 -lt $n) { $Text[$i + 1] } else { [char]0 }
                if ($Text[$i] -eq '/' -and $d -eq '*') {
                    $depth++; [void]$sb.Append('  '); $i += 2; continue
                }
                if ($Text[$i] -eq '*' -and $d -eq '/') {
                    $depth--; [void]$sb.Append('  '); $i += 2
                    if ($depth -le 0) { break }
                    continue
                }
                if ($Text[$i] -eq "`n") { [void]$sb.Append("`n") } else { [void]$sb.Append(' ') }
                $i++
            }
            continue
        }

        # 原始字符串 r"..." / r#"..."#（含 br 前缀）
        $rStart = -1
        if ($c -eq 'r') { $rStart = $i }
        elseif ($c -eq 'b' -and $next -eq 'r') { $rStart = $i }
        if ($rStart -ge 0) {
            $j = $rStart
            if ($Text[$j] -eq 'b') { $j++ }
            $j++                                  # 跳过 'r'
            $hashes = 0
            while ($j -lt $n -and $Text[$j] -eq '#') { $hashes++; $j++ }
            if ($j -lt $n -and $Text[$j] -eq '"') {
                $term = '"' + ('#' * $hashes)
                $endIdx = $Text.IndexOf($term, $j + 1, [System.StringComparison]::Ordinal)
                if ($endIdx -lt 0) { $endIdx = $n - $term.Length }
                $stop = $endIdx + $term.Length
                for ($k = $rStart; $k -lt $stop; $k++) {
                    if ($Text[$k] -eq "`n") { [void]$sb.Append("`n") } else { [void]$sb.Append(' ') }
                }
                $i = $stop
                continue
            }
        }

        # 普通字符串
        if ($c -eq '"') {
            [void]$sb.Append(' '); $i++
            while ($i -lt $n) {
                if ($Text[$i] -eq '\') { [void]$sb.Append('  '); $i += 2; continue }
                if ($Text[$i] -eq '"') { [void]$sb.Append(' '); $i++; break }
                if ($Text[$i] -eq "`n") { [void]$sb.Append("`n") } else { [void]$sb.Append(' ') }
                $i++
            }
            continue
        }

        # 字符字面量 vs 生命周期：'x' / '\n' / '\'' 是字面量，'static 不是
        if ($c -eq "'") {
            $isCharLit = $false
            if ($next -eq '\') { $isCharLit = $true }
            elseif ($i + 2 -lt $n -and $Text[$i + 2] -eq "'") { $isCharLit = $true }
            if ($isCharLit) {
                [void]$sb.Append(' '); $i++
                while ($i -lt $n) {
                    if ($Text[$i] -eq '\') { [void]$sb.Append('  '); $i += 2; continue }
                    if ($Text[$i] -eq "'") { [void]$sb.Append(' '); $i++; break }
                    [void]$sb.Append(' '); $i++
                }
                continue
            }
        }

        [void]$sb.Append($c)
        $i++
    }
    return $sb.ToString()
}

# 抽取一个 item（fn / const / struct / static / type），连同它上方连续的 doc 注释与属性行。
# 找不到就抛错 —— 上游重命名或删掉这些函数时，devcheck 必须炸出来，而不是静默少测。
function Get-RustItem {
    param(
        [Parameter(Mandatory)][string]$Text,
        [Parameter(Mandatory)][string]$Masked,
        [Parameter(Mandatory)][ValidateSet('fn', 'const', 'struct', 'static', 'type')][string]$Kind,
        [Parameter(Mandatory)][string]$Name,
        [Parameter(Mandatory)][string]$SourceName
    )

    $vis = '(?:pub(?:\([^)]*\))?\s+)?'
    $pattern = switch ($Kind) {
        'fn'     { "(?m)^${vis}(?:async\s+)?fn\s+$Name\b" }
        'const'  { "(?m)^${vis}const\s+$Name\b" }
        'static' { "(?m)^${vis}static\s+$Name\b" }
        'struct' { "(?m)^${vis}struct\s+$Name\b" }
        'type'   { "(?m)^${vis}type\s+$Name\b" }
    }

    $m = [regex]::Match($Masked, $pattern)
    if (-not $m.Success) {
        throw "在 $SourceName 里找不到 $Kind $Name —— 上游改名/删除了？请同步 tools/devcheck 的清单"
    }

    # 1) 结尾：从签名开始做括号配对；顶层 ';' 或配平的 '}' 即结束
    $n = $Masked.Length
    $depth = 0
    $seenBrace = $false
    $end = -1
    for ($i = $m.Index; $i -lt $n; $i++) {
        $c = $Masked[$i]
        if ($c -eq '{' -or $c -eq '(' -or $c -eq '[') {
            $depth++
            if ($c -eq '{') { $seenBrace = $true }
        }
        elseif ($c -eq '}' -or $c -eq ')' -or $c -eq ']') {
            $depth--
            if ($depth -lt 0) { throw "$SourceName 里 $Name 的括号不配平" }
            if ($depth -eq 0 -and $seenBrace -and $c -eq '}') { $end = $i + 1; break }
        }
        elseif ($c -eq ';' -and $depth -eq 0) { $end = $i + 1; break }
    }
    if ($end -lt 0) { throw "在 $SourceName 里无法确定 $Name 的结束位置" }

    # 2) 起点：先退到本行行首，再往上吃掉连续的 doc 注释 / 普通注释 / 属性行
    #    （#[derive(..)] 这类属性必须一起带走，否则 serde 的 helper 属性会解析不到）
    $lineStart = $Text.LastIndexOf("`n", [Math]::Max(0, $m.Index - 1))
    $start = if ($lineStart -lt 0) { 0 } else { $lineStart + 1 }
    while ($start -gt 0) {
        $prevEnd = $start - 1                       # 上一行结尾的换行符
        $prevLineStart = $Text.LastIndexOf("`n", [Math]::Max(0, $prevEnd - 1))
        $prevBegin = if ($prevLineStart -lt 0) { 0 } else { $prevLineStart + 1 }
        $prev = $Text.Substring($prevBegin, $prevEnd - $prevBegin).Trim()
        if ($prev -match '^(///|//!|//|#\[)') { $start = $prevBegin } else { break }
    }

    return $Text.Substring($start, $end - $start).TrimEnd()
}

# 读一个源文件，返回 @{ Text; Masked; Name }
function Read-RustSource {
    param([Parameter(Mandatory)][string]$Path)
    if (-not (Test-Path -LiteralPath $Path)) { throw "缺少源文件: $Path" }
    $text = [System.IO.File]::ReadAllText($Path)
    return @{
        Text   = $text
        Masked = Get-RustMaskedText -Text $text
        Name   = Split-Path -Leaf $Path
    }
}

# 去掉整行的 #[tauri::command]（typecheck crate 不依赖 tauri）
function Remove-TauriCommandAttr {
    param([Parameter(Mandatory)][string]$Text)
    $lines = $Text -split "(`r?`n)"
    $out = for ($i = 0; $i -lt $lines.Count; $i++) {
        if ($i % 2 -eq 1) { $lines[$i]; continue }   # 分隔符（换行）原样保留
        if ($lines[$i].Trim() -eq '#[tauri::command]') { '// [devcheck] 去掉 #[tauri::command]' }
        else { $lines[$i] }
    }
    return ($out -join '')
}

function Write-GeneratedFile {
    param([Parameter(Mandatory)][string]$Path, [Parameter(Mandatory)][string]$Content)
    $dir = Split-Path -Parent $Path
    if (-not (Test-Path -LiteralPath $dir)) { New-Item -ItemType Directory -Path $dir -Force | Out-Null }
    # 统一 LF，避免 Windows / Linux 之间来回改行尾
    $normalized = $Content -replace "`r`n", "`n"
    [System.IO.File]::WriteAllText($Path, $normalized, [System.Text.UTF8Encoding]::new($false))
}
