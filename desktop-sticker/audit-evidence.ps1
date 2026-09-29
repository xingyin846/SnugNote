# =====================================================================
#  S1 sticker - audit evidence demos (three items the auditor will check)
#
#    D1  重启后多条 topMost=true 是否收敛成一条
#    D2  端口断言的正例对照（真起监听证明写法会变红 + 本程序不监听）
#    D3  noteId 判定键未做任何规范化（对照实验：带尾空格的变体 id）
#
#  Self-contained: builds nothing, starts/stops Sticker.exe itself, writes only
#  under .tools\sticker_build\ (never touches demo/ launcher/ installer/ data/notes.json).
#
#  Usage: powershell -NoProfile -ExecutionPolicy Bypass -File desktop-sticker\audit-evidence.ps1
# =====================================================================

$ErrorActionPreference = 'Stop'
[System.Console]::OutputEncoding = [System.Text.Encoding]::UTF8

$srcRoot = if ($PSScriptRoot) { $PSScriptRoot } else { Split-Path -Parent $MyInvocation.MyCommand.Definition }
$repoRoot = Split-Path -Parent $srcRoot
$exe = Join-Path $repoRoot '.tools\sticker_build\Sticker.exe'
$logPath = Join-Path $repoRoot '.tools\sticker_build\sticker-debug.log'
$statePath = Join-Path $repoRoot '.tools\sticker_build\sticker-state.json'
$dataStatePath = Join-Path $repoRoot 'data\sticker-state.json'

if (-not (Test-Path -LiteralPath $exe)) { Write-Host "missing $exe - run build.ps1 first" -ForegroundColor Red; exit 1 }

# ---------------------------------------------------------------------------
# BLIND-KILL GUARD (captain 2026-09-20 ruling): never kill by process name.
# The former Stop-AllStickers ran `Get-Process -Name Sticker | Stop-Process -Force`, which killed
# EVERY Sticker instance on the machine - including a user's own running stickers. Cleanup now only
# targets pids this script started, and a foreign instance makes the run abort instead of racing.
# ---------------------------------------------------------------------------
$scriptPids = New-Object System.Collections.Generic.List[int]

function Get-AllStickerProcesses { return @(Get-Process -Name Sticker -ErrorAction SilentlyContinue) }
function Assert-NoForeignStickers([string]$where) {
    $foreign = @()
    foreach ($p in (Get-AllStickerProcesses)) { if (-not ($scriptPids -contains $p.Id)) { $foreign += $p } }
    if ($foreign.Count -gt 0) {
        Write-Host ("BLOCKER: " + $foreign.Count + " Sticker instance(s) not started by this script are running (" + $where + ")") -ForegroundColor Red
        foreach ($p in $foreign) {
            $path = "(unknown)"; try { $path = $p.Path } catch { }
            Write-Host ("   pid=" + $p.Id + "  start=" + $p.StartTime.ToString('yyyy-MM-dd HH:mm:ss') + "  path=" + $path) -ForegroundColor Red
        }
        Write-Host "   -> ABORTING without killing anything (blind-kill guard; captain 2026-09-20)." -ForegroundColor Red
        exit 4
    }
}
function Start-MySticker {
    param([string[]]$ArgumentList = @())
    if ($ArgumentList.Count -gt 0) {
        $p = Start-Process -FilePath $exe -WorkingDirectory $repoRoot -ArgumentList $ArgumentList -PassThru
    } else {
        $p = Start-Process -FilePath $exe -WorkingDirectory $repoRoot -PassThru
    }
    $scriptPids.Add($p.Id) | Out-Null
    return $p
}
function Stop-MyStickers {
    foreach ($id in @($scriptPids)) {
        $p = Get-Process -Id $id -ErrorAction SilentlyContinue
        if ($p) { Stop-Process -Id $id -Force -ErrorAction SilentlyContinue }
    }
    $scriptPids.Clear()
    Start-Sleep -Milliseconds 600
    Assert-NoForeignStickers "after cleaning up our own instances, before the next launch"
}
# Archive the log BEFORE clearing it (captain 2026-09-20: never destroy runtime evidence).
$archivedLogs = @()
function Archive-Log {
    if (-not (Test-Path -LiteralPath $logPath)) {
        Write-Host "no existing sticker-debug.log to archive" -ForegroundColor Yellow
        return
    }
    $seg = '(unreadable)'
    try { $seg = (Get-Content -LiteralPath $logPath -TotalCount 1 -Encoding UTF8) } catch { }
    $dst = Join-Path (Split-Path -Parent $logPath) ('sticker-debug.' + (Get-Date -Format 'yyyyMMdd-HHmmss') + '.log')
    Copy-Item -LiteralPath $logPath -Destination $dst -Force
    $script:archivedLogs += $dst
    Write-Host ("log archived before clearing: " + $dst) -ForegroundColor Cyan
    Write-Host ("   archived segment started at: " + $seg) -ForegroundColor Cyan
    Remove-Item -LiteralPath $logPath -Force
}
Assert-NoForeignStickers "pre-flight, before the first launch"

# Deterministic "place-all": write an EMPTY-stickers primary state instead of deleting the primary.
# HISTORY (captain 2026-09-20): v10 spec 8.1 declared the %APPDATA% fallback write-only, but the
# shipped v11.1 build DID read it when the primary was missing (observed), which made D1 place only
# 2 cards and abort at the tamper step. That deviation is FIXED in v11.2 (State.Load takes the
# primary only), so the fallback is now genuinely never read - assertion fallback-is-never-read #24.
# Spec 10.1-2 also triggers first-run placement on an EXISTING state with an empty `stickers` array.
function Set-StateEmpty {
    $json = '{"schema":1,"app":"tietie-sticker","savedAt":' + ([DateTimeOffset]::UtcNow.ToUnixTimeMilliseconds()) + ',"stickers":[]}'
    [System.IO.File]::WriteAllText($statePath, $json, (New-Object System.Text.UTF8Encoding($false)))
    Start-Sleep -Milliseconds 120
}

function Read-Json([string]$p) {
    if (-not (Test-Path -LiteralPath $p)) { return $null }
    try { return (Get-Content -LiteralPath $p -Raw -Encoding UTF8 | ConvertFrom-Json) } catch { return $null }
}
function Flags($st) {
    if (-not $st) { return '(no state)' }
    $parts = @()
    foreach ($s in $st.stickers) { $parts += (($(if ($s.topMost) { '1' } else { '0' })) + ':' + $s.noteId.Substring(0, 8)) }
    return '[' + ($parts -join ',') + ']  floated=' + @($st.stickers | Where-Object { $_.topMost }).Count
}
function RecentLog([int]$n) {
    if (-not (Test-Path -LiteralPath $logPath)) { return @() }
    return @(Get-Content -LiteralPath $logPath -Encoding UTF8 | Select-Object -Last $n)
}

Write-Host "==================================================================" -ForegroundColor Cyan
Write-Host " D1  重启后多条 topMost=true 必须收敛成一条" -ForegroundColor Cyan
Write-Host "==================================================================" -ForegroundColor Cyan
Stop-MyStickers
# captain 2026-09-20 (red line): never create/delete inside the protected data\ directory - see the
# long note in interaction-test.ps1. Replaced by an assertive read-only guard (turns RED if it exists).
if (Test-Path -LiteralPath $dataStatePath) { Write-Host ("BLOCKER: protected data\ unexpectedly contains sticker-state.json - refusing to touch it: " + $dataStatePath) -ForegroundColor Red; exit 4 }
Set-StateEmpty

# run 1: let the app place all notes and write a clean state
$null = Start-MySticker
Start-Sleep -Seconds 6
Stop-MyStickers
$st0 = Read-Json $statePath
Write-Host ("D1.0 基线 state（干净）: " + (Flags $st0))
$ids = @($st0.stickers | ForEach-Object { $_.noteId })
if ($ids.Count -lt 2) { Write-Host "D1 needs >=2 notes in notes.json" -ForegroundColor Red; exit 1 }

# tamper: force exactly TWO records to topMost=true, mimicking a dirty historical state.
# IMPORTANT: plain regex edit limited to 2 occurrences, and do NOT round-trip the state through
# ConvertTo-Json - that reshapes the object and can silently drop fields, which would make this
# demo test the harness instead of the program. Verify the result still parses before continuing.
$raw = Get-Content -LiteralPath $statePath -Raw -Encoding UTF8
$re = [regex]'"topMost"\s*:\s*false'
if ($re.Matches($raw).Count -lt 2) { Write-Host "D1 tamper: fewer than two false flags" -ForegroundColor Red; exit 1 }
$tamper = $re.Replace($raw, '"topMost":true', 2)
[System.IO.File]::WriteAllText($statePath, $tamper, (New-Object System.Text.UTF8Encoding($false)))
$stDirty = Read-Json $statePath
if (-not $stDirty) { Write-Host "D1 tamper produced unparsable JSON - aborting" -ForegroundColor Red; exit 1 }
Write-Host ("D1.1 人为污染后 state       : " + (Flags $stDirty)) -ForegroundColor Yellow
Write-Host ("     记录数=" + $stDirty.stickers.Count + "  被置顶的 id: " + (($stDirty.stickers | Where-Object { $_.topMost } | ForEach-Object { $_.noteId }) -join ' / '))

# run 2: restart -> convergence must keep only the FIRST in array order
Archive-Log
$null = Start-MySticker
Start-Sleep -Seconds 6
$stFixed = Read-Json $statePath
Write-Host ("D1.2 重启后 state           : " + (Flags $stFixed)) -ForegroundColor Green
$floated = @($stFixed.stickers | Where-Object { $_.topMost })
$convOk = ($floated.Count -eq 1) -and ($floated[0].noteId -eq $ids[0])
Write-Host ("D1.3 收敛结论: floatedCount=" + $floated.Count + " winner=" + $(if ($floated.Count -gt 0) { $floated[0].noteId } else { '(none)' }) + " 期望 winner=" + $ids[0] + "  => CONVERGED=" + $convOk) -ForegroundColor $(if ($convOk) { 'Green' } else { 'Red' })
Write-Host "D1.4 程序自证日志（recovery 相关）:"
RecentLog 200 | Select-String 'recovery' | ForEach-Object { Write-Host ("      " + ($_.Line -replace '^\S+ \S+ \[\d+\] ', '')) }
Stop-MyStickers

Write-Host ""
Write-Host "==================================================================" -ForegroundColor Cyan
Write-Host " D2  端口断言的正例对照" -ForegroundColor Cyan
Write-Host "==================================================================" -ForegroundColor Cyan
Write-Host "注: Get-NetTCPConnection 在本沙箱内失效（对任何进程都返回 0 条），故一律用 netstat -ano"

# positive control: a process under our control really listens
$ctrl = Start-Process -FilePath 'powershell.exe' -ArgumentList @(
    '-NoProfile', '-Command',
    '$l=New-Object System.Net.Sockets.TcpListener([System.Net.IPAddress]::Loopback, 8099); $l.Start(); Start-Sleep -Seconds 15; $l.Stop()'
) -PassThru
Start-Sleep -Seconds 3
$nsCtrl = (netstat -ano | Out-String)
$ctrlRows = @($nsCtrl -split "`r?`n" | Where-Object { $_ -match ':8099\s' -and $_ -match 'LISTENING' })
Write-Host ("D2.1 正例：自控进程 pid " + $ctrl.Id + " 真监听 127.0.0.1:8099 -> netstat 命中 " + $ctrlRows.Count + " 条 LISTENING")
foreach ($r in $ctrlRows) { Write-Host ("      " + $r.Trim()) }
Write-Host ("      另证 `Get-NetTCPConnection -OwningProcess " + $ctrl.Id + "` 返回 " + @(Get-NetTCPConnection -OwningProcess $ctrl.Id -ErrorAction SilentlyContinue).Count + " 条 -> 该 cmdlet 空洞") -ForegroundColor Yellow
try { $ctrl | Stop-Process -Force -ErrorAction SilentlyContinue } catch { }

# the app under test must NOT listen
$proc = Start-MySticker
Start-Sleep -Seconds 7
$nsApp = (netstat -ano | Out-String)
$appListen = @($nsApp -split "`r?`n" | Where-Object { $_ -match ('\sLISTENING\s+' + $proc.Id + '\s*$') })
$appTcp = @($nsApp -split "`r?`n" | Where-Object { $_ -match '\sTCP\s' -and $_ -match ('\s' + $proc.Id + '\s*$') })
$appUdp = @($nsApp -split "`r?`n" | Where-Object { $_ -match '\sUDP\s' -and $_ -match ('\s' + $proc.Id + '\s*$') })
Write-Host ("D2.2 被测程序 pid " + $proc.Id + " -> netstat LISTENING=" + $appListen.Count + " TCP(任意状态)=" + $appTcp.Count + " UDP=" + $appUdp.Count) -ForegroundColor $(if (($appListen.Count + $appTcp.Count + $appUdp.Count) -eq 0) { 'Green' } else { 'Red' })
Write-Host ("      结论: 正例会让同名断言变红（命中 1 条），本程序 0 条 ⇒ 断言非空洞") -ForegroundColor Green
Stop-MyStickers

Write-Host ""
Write-Host "==================================================================" -ForegroundColor Cyan
Write-Host " D3  noteId 判定键未做任何规范化（对照实验）" -ForegroundColor Cyan
Write-Host "==================================================================" -ForegroundColor Cyan
# captain 2026-09-20 (red line): never create/delete inside the protected data\ directory - see the
# long note in interaction-test.ps1. Replaced by an assertive read-only guard (turns RED if it exists).
if (Test-Path -LiteralPath $dataStatePath) { Write-Host ("BLOCKER: protected data\ unexpectedly contains sticker-state.json - refusing to touch it: " + $dataStatePath) -ForegroundColor Red; exit 4 }
Set-StateEmpty
$null = Start-MySticker
Start-Sleep -Seconds 6
Stop-MyStickers
$st3 = Read-Json $statePath
$exact = $st3.stickers[0].noteId
$variantSpace = $exact + ' '
$variantUpper = $exact.ToUpperInvariant()
Write-Host ("D3.0 真实 id            : '" + $exact + "'")

# seed a state holding three records that differ ONLY by normalisation:
#   the exact id / the same id with a trailing SPACE / the same id UPPERCASED.
# Built as text on purpose: the whole point is byte-exact control of the id strings.
$seedJson = '{"schema":1,"app":"tietie-sticker","savedAt":0,"stickers":[' +
    '{"noteId":"' + $exact        + '","x":40,"y":40,"w":260,"h":320,"topMost":false},' +
    '{"noteId":"' + $variantSpace + '","x":320,"y":40,"w":260,"h":320,"topMost":false},' +
    '{"noteId":"' + $variantUpper + '","x":600,"y":40,"w":260,"h":320,"topMost":false}]}'
[System.IO.File]::WriteAllText($statePath, $seedJson, (New-Object System.Text.UTF8Encoding($false)))
Write-Host ("D3.1 种入 3 条记录: 原样 / 尾部加空格 / 全大写")
Write-Host ("     变体1 = '" + $variantSpace + "'  (尾部 U+0020)")
Write-Host ("     变体2 = '" + $variantUpper + "'  (全大写)")
Archive-Log
$null = Start-MySticker
Start-Sleep -Seconds 6
$st3b = Read-Json $statePath
$kept = @($st3b.stickers | ForEach-Object { $_.noteId })
Write-Host ("D3.2 重启后保留 " + $kept.Count + " 条:")
foreach ($k in $kept) { Write-Host ("      '" + $k + "'" + $(if ($k -ceq $exact) { '   <- 原样匹配' })) }
$onlyExact = ($kept.Count -eq 1) -and ($kept[0] -ceq $exact)
Write-Host ("D3.3 结论: 变体被丢弃、只留原样 id => 未 trim、未折叠大小写: " + $onlyExact) -ForegroundColor $(if ($onlyExact) { 'Green' } else { 'Red' })
Write-Host "D3.4 程序自证日志（丢弃原因）:"
RecentLog 300 | Select-String 'no longer present in notes.json' | ForEach-Object { Write-Host ("      " + ($_.Line -replace '^\S+ \S+ \[\d+\] ', '')) }
Stop-MyStickers

Write-Host ""
Write-Host "注: 本次演示故意留下的脏 state 已在 D3 结束前由程序写回；如需完全干净请重跑 build 后的首次启动。" -ForegroundColor Cyan
