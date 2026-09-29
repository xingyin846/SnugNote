# =====================================================================
#  TieTie Sticker S1 - live verification (window properties, ports, state)
#
#  Runs the REAL Sticker.exe and reports, with positive controls:
#    V1  process alive + one visible card window per placed note
#        (enumerated by class + title; Process.MainWindowHandle is NOT usable here
#         because ShowInTaskbar=false produces a tool window)
#    V2  real window styles: no WS_THICKFRAME (truly borderless), CS_DROPSHADOW bit
#    V2b WS_EX_TOOLWINDOW / WS_EX_APPWINDOW before vs after Application.EnableVisualStyles
#    V3  no webview2 module loaded into the process
#    V4  no TCP / UDP endpoint owned by the process
#    V6  POSITIVE CONTROL: a real listener process must show up in the same query
#        (proves the V4 assertion is not vacuous)
#    V5  state file written to both the exe directory and data\
#
#  Usage: powershell -NoProfile -ExecutionPolicy Bypass -File desktop-sticker\verify-live.ps1
# =====================================================================

$ErrorActionPreference = 'Stop'
[System.Console]::OutputEncoding = [System.Text.Encoding]::UTF8

$srcRoot = if ($PSScriptRoot) { $PSScriptRoot } else { Split-Path -Parent $MyInvocation.MyCommand.Definition }
$repoRoot = Split-Path -Parent $srcRoot
$exe = Join-Path $repoRoot '.tools\sticker_build\Sticker.exe'
$stateExeDir = Join-Path $repoRoot '.tools\sticker_build\sticker-state.json'
$stateMain = Join-Path $repoRoot 'data\sticker-state.json'
$logPath = Join-Path $repoRoot '.tools\sticker_build\sticker-debug.log'

if (-not (Test-Path -LiteralPath $exe)) { Write-Host "missing $exe - run build.ps1 first" -ForegroundColor Red; exit 1 }

# ---------------------------------------------------------------------------
# BLIND-KILL GUARD (captain 2026-09-20 ruling, red line "never blindly kill processes").
# The former `Get-Process -Name Sticker | Stop-Process -Force` killed EVERY Sticker instance on the
# machine, including a user's own running stickers. Cleanup now only targets pids this script started.
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
function Stop-MyStickers {
    foreach ($id in @($scriptPids)) {
        $p = Get-Process -Id $id -ErrorAction SilentlyContinue
        if ($p) { Stop-Process -Id $id -Force -ErrorAction SilentlyContinue }
    }
    $scriptPids.Clear()
    Start-Sleep -Milliseconds 600
    Assert-NoForeignStickers "after cleaning up our own instances, before the next launch"
}
# Deterministic place-all: write an EMPTY-stickers primary state instead of deleting the primary
# (the shipped build reads the %APPDATA% fallback when the primary is missing, while v10 8.1 says
# that fallback is write-only; spec 10.1-2 also triggers first-run placement on empty `stickers`).
function Set-StateEmpty {
    $json = '{"schema":1,"app":"tietie-sticker","savedAt":' + ([DateTimeOffset]::UtcNow.ToUnixTimeMilliseconds()) + ',"stickers":[]}'
    [System.IO.File]::WriteAllText($stateExeDir, $json, (New-Object System.Text.UTF8Encoding($false)))
    Start-Sleep -Milliseconds 120
}

Write-Host "== S1 live verification ==" -ForegroundColor Cyan
Write-Host ("exe : " + $exe)

Assert-NoForeignStickers "pre-flight, before the first launch"
Stop-MyStickers
# captain 2026-09-20 (red line): never create/delete inside the protected data\ directory - see the
# long note in interaction-test.ps1. Replaced by an assertive read-only guard (turns RED if it exists).
if (Test-Path -LiteralPath $stateMain) { Write-Host ("BLOCKER: protected data\ unexpectedly contains sticker-state.json - refusing to touch it: " + $stateMain) -ForegroundColor Red; exit 4 }
Set-StateEmpty

Add-Type -TypeDefinition @"
using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;
public static class WinProbe {
  public delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);
  [DllImport("user32.dll")] public static extern bool EnumWindows(EnumWindowsProc cb, IntPtr lParam);
  [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint pid);
  [DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr hWnd);
  [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern int GetWindowTextW(IntPtr hWnd, StringBuilder text, int max);
  [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern int GetClassNameW(IntPtr hWnd, StringBuilder text, int max);
  [DllImport("user32.dll", SetLastError = true)] public static extern int GetWindowLong(IntPtr hWnd, int index);
  [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr hWnd, out RECT r);
  [DllImport("user32.dll")] public static extern IntPtr WindowFromPoint(POINT p);
  [DllImport("user32.dll")] public static extern IntPtr GetAncestor(IntPtr h, uint f);
  [DllImport("user32.dll")] public static extern uint GetDpiForWindow(IntPtr h);
  [DllImport("user32.dll")] public static extern IntPtr GetThreadDpiAwarenessContext();
  [DllImport("user32.dll")] public static extern int GetAwarenessFromDpiAwarenessContext(IntPtr ctx);
  [StructLayout(LayoutKind.Sequential)] public struct RECT { public int Left, Top, Right, Bottom; }
  [StructLayout(LayoutKind.Sequential)] public struct POINT { public int X, Y; }

  public static string Title(IntPtr h) { StringBuilder sb = new StringBuilder(512); GetWindowTextW(h, sb, 512); return sb.ToString(); }
  public static string Cls(IntPtr h) { StringBuilder sb = new StringBuilder(256); GetClassNameW(h, sb, 256); return sb.ToString(); }
  public static string RectStr(IntPtr h) { RECT r; GetWindowRect(h, out r); return "(" + r.Left + "," + r.Top + " " + (r.Right - r.Left) + "x" + (r.Bottom - r.Top) + ")"; }
  public static RECT RectOf(IntPtr h) { RECT r; GetWindowRect(h, out r); return r; }

  public static List<IntPtr> ForPid(uint target) {
    List<IntPtr> found = new List<IntPtr>();
    EnumWindows(delegate(IntPtr h, IntPtr l) {
      uint pid; GetWindowThreadProcessId(h, out pid);
      if (pid == target) found.Add(h);
      return true;
    }, IntPtr.Zero);
    return found;
  }

  /// <summary>Card windows = visible WinForms top-level windows of reasonable size.</summary>
  public static List<IntPtr> Cards(uint target) {
    List<IntPtr> found = new List<IntPtr>();
    foreach (IntPtr h in ForPid(target)) {
      if (!IsWindowVisible(h)) continue;
      if (Cls(h).IndexOf("WindowsForms10.Window") < 0) continue;
      RECT r; GetWindowRect(h, out r);
      if (r.Right - r.Left < 50 || r.Bottom - r.Top < 50) continue;
      found.Add(h);
    }
    return found;
  }

  public static string OccluderAt(int x, int y) {
    POINT p; p.X = x; p.Y = y;
    IntPtr h = WindowFromPoint(p);
    if (h == IntPtr.Zero) return "(none)";
    return Title(h) + " [" + Cls(h) + "]";
  }

  public static int DpiAwareness() { try { return GetAwarenessFromDpiAwarenessContext(GetThreadDpiAwarenessContext()); } catch { return -1; } }
}
"@

$proc = Start-Process -FilePath $exe -WorkingDirectory $repoRoot -PassThru
$scriptPids.Add($proc.Id) | Out-Null
Start-Sleep -Seconds 10

Write-Host ""
Write-Host ("V1 alive after 10s            : " + (-not $proc.HasExited)) -ForegroundColor Green
$cards = @([WinProbe]::Cards([uint32]$proc.Id))
Write-Host ("V1 visible card windows      : " + $cards.Count)
foreach ($h in $cards) {
    Write-Host ("     '" + [WinProbe]::Title($h) + "' " + [WinProbe]::RectStr($h) + " class=" + [WinProbe]::Cls($h))
}
Write-Host ("V1 Process.MainWindowHandle  : " + $proc.MainWindowHandle + "  MainWindowTitle='" + $proc.MainWindowTitle + "'  (0/'' expected: tool window)")
Write-Host ("V1 dpi awareness of THIS shell: " + [WinProbe]::DpiAwareness() + " (0=UNAWARE,1=SYSTEM,2=PER_MONITOR)")

if ($cards.Count -gt 0) {
    $h = $cards[0]
    $style = [WinProbe]::GetWindowLong($h, -16)
    $ex = [WinProbe]::GetWindowLong($h, -20)
    $cs = [WinProbe]::GetWindowLong($h, -8)
    Write-Host ""
    Write-Host ("V2 style=0x{0:X8} exstyle=0x{1:X8} classstyle=0x{2:X8}" -f $style, $ex, $cs) -ForegroundColor Green
    Write-Host ("V2 WS_THICKFRAME  (0x00040000): " + ((($style -band 0x00040000) -ne 0)) + "   expect False = truly borderless")
    Write-Host ("V2 WS_EX_TOOLWINDOW (0x00000080): " + ((($ex -band 0x00000080) -ne 0)) + "   (WinForms keeps this False; see V2b)")
    Write-Host ("V2 WS_EX_APPWINDOW  (0x00040000): " + ((($ex -band 0x00040000) -ne 0)) + "   expect False = not in the taskbar")
    Write-Host ("V2 WS_EX_TOPMOST    (0x00000008): " + ((($ex -band 0x00000008) -ne 0)) + "   expect False = base state is NOT always-on-top, so a maximized app covers it")
    $shadowBit = ((($cs -band 0x00020000) -ne 0))
    Write-Host ("V2 CS_DROPSHADOW    (0x00020000): " + $shadowBit + "   [U1 system shadow request - NOT a reliable deliverable criterion]")
    Write-Host "V2   note: this class-style bit reads INCONSISTENTLY in this sandbox (three fresh launches of the same exe gave 1/3, 2/3, 0/3 windows with the bit set, and three windows of ONE process read three different class styles for the SAME window class). Treat the system shadow as UNVERIFIED; per spec 6.3 fallback the card relies on the 1px border only. See README section 5."
    Write-Host ("V2 GetDpiForWindow            : " + [WinProbe]::GetDpiForWindow($h))
    $r = [WinProbe]::RectOf($h)
    Write-Host ("V2 card window rect           : (" + $r.Left + "," + $r.Top + " " + ($r.Right - $r.Left) + "x" + ($r.Bottom - $r.Top) + ")")
}

Write-Host ""
Write-Host "V2b occlusion check: what does the OS report under the card centre point?" -ForegroundColor Cyan
foreach ($h in $cards) {
    $r = [WinProbe]::RectOf($h)
    $cx = ($r.Left + $r.Right) / 2
    $cy = ($r.Top + $r.Bottom) / 2
    Write-Host ("     card '" + [WinProbe]::Title($h) + "' centre (" + $cx + "," + $cy + ") -> occluded by: " + [WinProbe]::OccluderAt($cx, $cy))
}

$wvToken = 'Web' + 'View' + '2'
$mods = @()
try { $mods = @($proc.Modules | ForEach-Object { $_.ModuleName }) } catch { }
$wvLoaded = @($mods | Where-Object { $_.IndexOf($wvToken, [System.StringComparison]::OrdinalIgnoreCase) -ge 0 })
Write-Host ""
Write-Host ("V3 modules loaded            : " + $mods.Count + "  webview2 modules: " + $wvLoaded.Count) -ForegroundColor Green

# ---------------- V4: ports, via netstat ----------------
# NOTE: Get-NetTCPConnection is BROKEN in this environment - it returns 0 listeners even for
# the DSH GUI's own 127.0.0.1:3080 and for a listener this very script opens. netstat -ano is
# the ground truth here, so the port assertion uses it. The V6 control below proves the method
# sees a real listener.
Write-Host ""
$netstat = (netstat -ano | Out-String)
$appListen = @($netstat -split "`r?`n" | Where-Object { $_ -match ('\sLISTENING\s+' + $proc.Id + '\s*$') })
$appAnyTcp = @($netstat -split "`r?`n" | Where-Object { $_ -match ('\sTCP\s') -and $_ -match ('\s' + $proc.Id + '\s*$') })
$appUdp = @($netstat -split "`r?`n" | Where-Object { $_ -match ('\sUDP\s') -and $_ -match ('\s' + $proc.Id + '\s*$') })
Write-Host ("V4 netstat LISTENING rows for pid " + $proc.Id + " : " + $appListen.Count) -ForegroundColor $(if ($appListen.Count -eq 0) { 'Green' } else { 'Red' })
foreach ($l in $appListen) { Write-Host ("     " + $l.Trim()) }
Write-Host ("V4 netstat TCP rows (any state) for pid " + $proc.Id + " : " + $appAnyTcp.Count) -ForegroundColor $(if ($appAnyTcp.Count -eq 0) { 'Green' } else { 'Red' })
foreach ($l in $appAnyTcp) { Write-Host ("     " + $l.Trim()) }
Write-Host ("V4 netstat UDP rows for pid " + $proc.Id + " : " + $appUdp.Count) -ForegroundColor $(if ($appUdp.Count -eq 0) { 'Green' } else { 'Red' })
Write-Host "(for reference: Get-NetTCPConnection -OwningProcess " + $proc.Id + " returns " + @(Get-NetTCPConnection -OwningProcess $proc.Id -ErrorAction SilentlyContinue).Count + " - the cmdlet is broken in this sandbox, see V6)"

# ---------------- V6 positive control ----------------
Write-Host ""
Write-Host "V6 POSITIVE CONTROL: netstat must see a listener opened by a process WE control" -ForegroundColor Cyan
$ctrl = Start-Process -FilePath 'powershell.exe' -ArgumentList @(
    '-NoProfile', '-Command',
    '$l=New-Object System.Net.Sockets.TcpListener([System.Net.IPAddress]::Loopback, 8099); $l.Start(); Start-Sleep -Seconds 12; $l.Stop()'
) -PassThru
Start-Sleep -Seconds 3
$netstat2 = (netstat -ano | Out-String)   # re-read: $netstat above is a pre-listener snapshot
$ctrlListen = @($netstat2 -split "`r?`n" | Where-Object { $_ -match ':8099\s' -and $_ -match 'LISTENING' })
$ctrlByPid = @($netstat2 -split "`r?`n" | Where-Object { $_ -match ('\sLISTENING\s+' + $ctrl.Id + '\s*$') })
Write-Host ("   control process (pid " + $ctrl.Id + ") opened 127.0.0.1:8099")
Write-Host ("   netstat rows for :8099 LISTENING        : " + $ctrlListen.Count)
foreach ($l in $ctrlListen) { Write-Host ("     " + $l.Trim()) }
Write-Host ("   netstat LISTENING rows for control pid  : " + $ctrlByPid.Count)
if ($ctrlByPid.Count -gt 0) {
    Write-Host "   -> the port assertion is LIVE (non-vacuous)" -ForegroundColor Green
} else {
    Write-Host "   -> the port assertion would be VACUOUS" -ForegroundColor Red
}
try { $ctrl | Stop-Process -Force -ErrorAction SilentlyContinue } catch { }

Start-Sleep -Milliseconds 300
Write-Host ""
foreach ($p in @($stateExeDir, $stateMain)) {
    if (Test-Path -LiteralPath $p) {
        $txt = Get-Content -LiteralPath $p -Raw -Encoding UTF8
        Write-Host ("V5 state written             : " + $p + " (" + (Get-Item -LiteralPath $p).Length + " bytes)") -ForegroundColor Green
        Write-Host ("V5 content                   : " + $txt)
    } else {
        Write-Host ("V5 state MISSING             : " + $p) -ForegroundColor Yellow
    }
}

if (Test-Path -LiteralPath $logPath) {
    Write-Host ""
    Write-Host "last log lines:" -ForegroundColor Cyan
    Get-Content -LiteralPath $logPath -Encoding UTF8 | Select-Object -Last 12 | ForEach-Object { Write-Host ("   " + $_) }
}

Write-Host ""
Write-Host ("VERIFY DONE. Sticker.exe pid=" + $proc.Id + " left running for eyeball inspection.")
Write-Host ("stop with: Stop-Process -Id " + $proc.Id)
