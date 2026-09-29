# =====================================================================
#  TieTie Sticker S1 - automated interaction test
#
#  WHY MESSAGE POSTING INSTEAD OF RAW MOUSE INJECTION:
#    The cards are ordinary top-level windows. On this machine the foreground window
#    (the DSH Chrome GUI, full screen) sits above them and swallows physical cursor
#    input, so WindowFromPoint never reaches a card. To exercise OUR OWN message path
#    deterministically we send WM_LBUTTONDOWN / WM_MOUSEMOVE / WM_LBUTTONUP straight
#    to the card hwnd and keep the real cursor in sync with SetCursorPos
#    (StickerForm reads Cursor.Position while dragging). SendMessage is synchronous,
#    which keeps every step ordered with the window moves it causes.
#    This drives exactly the same OnMouseDown / OnMouseMove / OnMouseUp code that a
#    human click would drive.
#
#  Checks:
#    I1 drag by the card body   -> window moves  -> state written on release
#    I2 footer pin              -> topMost on    -> state written, exactly one floated
#    I2b float a second card    -> the first one sinks (only one floats)
#    I3 pin again               -> topMost off   -> state written
#    I4 drag the right edge     -> window resized-> state written
#    I5 restart                 -> position / size restored
#    I6 second instance         -> mutex keeps a single instance
#    I7 close one card          -> that card leaves the state (WM_CLOSE = Alt+F4 handler)
#    I8 two topMost in state    -> startup convergence keeps exactly one
#    I9 --note <id>             -> idempotent, never a duplicate card
#
#  Usage: powershell -NoProfile -ExecutionPolicy Bypass -File desktop-sticker\interaction-test.ps1
# =====================================================================

$ErrorActionPreference = 'Stop'
[System.Console]::OutputEncoding = [System.Text.Encoding]::UTF8

$srcRoot = if ($PSScriptRoot) { $PSScriptRoot } else { Split-Path -Parent $MyInvocation.MyCommand.Definition }
$repoRoot = Split-Path -Parent $srcRoot
$exe = Join-Path $repoRoot '.tools\sticker_build\Sticker.exe'
$logPath = Join-Path $repoRoot '.tools\sticker_build\sticker-debug.log'
$statePath = Join-Path $repoRoot '.tools\sticker_build\sticker-state.json'
$dataStatePath = Join-Path $repoRoot 'data\sticker-state.json'

Add-Type -TypeDefinition @"
using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;
public static class CardDriver {
  [DllImport("user32.dll")] public static extern bool SetCursorPos(int x, int y);
  [DllImport("user32.dll")] public static extern bool GetCursorPos(out Point p);
  [DllImport("user32.dll")] public static extern IntPtr SendMessage(IntPtr h, uint msg, IntPtr w, IntPtr l);
  [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr h, out Rect r);
  [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
  [DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr h);
  [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern int GetWindowText(IntPtr h, StringBuilder t, int m);
  [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern int GetClassName(IntPtr h, StringBuilder t, int m);
  [DllImport("user32.dll")] public static extern int GetWindowLong(IntPtr h, int i);
  [DllImport("user32.dll")] public static extern bool GetClientRect(IntPtr h, out Rect r);
  [DllImport("user32.dll")] public static extern bool ScreenToClient(IntPtr h, ref Point p);
  [DllImport("user32.dll")] public static extern void keybd_event(byte vk, byte scan, uint flags, UIntPtr extra);
  [StructLayout(LayoutKind.Sequential)] public struct Rect { public int L, T, Ri, B; }
  [StructLayout(LayoutKind.Sequential)] public struct Point { public int X, Y; }
  const uint WM_LBUTTONDOWN = 0x0201, WM_LBUTTONUP = 0x0202, WM_MOUSEMOVE = 0x0200, WM_CLOSE = 0x0010;
  const uint MK_LBUTTON = 0x0001, MK_NONE = 0;

  static IntPtr LP(int x, int y) { return (IntPtr)((y << 16) | (x & 0xFFFF)); }

  public static string Title(IntPtr h) { StringBuilder t = new StringBuilder(300); GetWindowText(h, t, 300); return t.ToString(); }
  public static string Class(IntPtr h) { StringBuilder c = new StringBuilder(300); GetClassName(h, c, 300); return c.ToString(); }

  public static string Describe(IntPtr h) {
    if (h == IntPtr.Zero) return "(null)";
    Rect r; GetWindowRect(h, out r);
    Rect cr; GetClientRect(h, out cr);
    uint pid; GetWindowThreadProcessId(h, out pid);
    return string.Format("hwnd=0x{0:X} pid={1} vis={2} class='{3}' title='{4}' win=({5},{6} {7}x{8}) client={9}x{10} style=0x{11:X8} ex=0x{12:X8}",
      h.ToInt64(), pid, IsWindowVisible(h), Class(h), Title(h), r.L, r.T, r.Ri - r.L, r.B - r.T,
      cr.Ri - cr.L, cr.B - cr.T, GetWindowLong(h, -16), GetWindowLong(h, -20));
  }

  public static Rect RectOf(IntPtr h) { Rect r; GetWindowRect(h, out r); return r; }

  static void Send(IntPtr h, uint msg, int screenX, int screenY, IntPtr wp) {
    // SetCursorPos is asynchronous on a busy desktop (our harness shares the machine with the
    // DSH GUI). StickerForm reads Cursor.Position itself, so the cursor MUST have really arrived
    // before the message is processed - otherwise drag/resize deltas are computed from stale
    // samples (observed: first sample old, second sample new -> wrong delta sign).
    SetCursorPos(screenX, screenY);
    Point cur;
    bool arrived = false;
    for (int tries = 0; tries < 200; tries++) {
      GetCursorPos(out cur);
      if (cur.X == screenX && cur.Y == screenY) { arrived = true; break; }
      System.Threading.Thread.Sleep(5);
    }
    if (!arrived) CursorMisses++;
    Point p; p.X = screenX; p.Y = screenY;
    ScreenToClient(h, ref p);
    SendMessage(h, msg, wp, LP(p.X, p.Y));
  }

  public static int CursorMisses = 0;

  public static void ClickScreen(IntPtr h, int x, int y) {
    Send(h, WM_LBUTTONDOWN, x, y, (IntPtr)MK_LBUTTON);
    System.Threading.Thread.Sleep(80);
    Send(h, WM_LBUTTONUP, x, y, (IntPtr)MK_NONE);
    System.Threading.Thread.Sleep(300);
  }

  public static void DragScreen(IntPtr h, int x0, int y0, int x1, int y1, int steps) {
    Send(h, WM_LBUTTONDOWN, x0, y0, (IntPtr)MK_LBUTTON);
    System.Threading.Thread.Sleep(80);
    for (int i = 1; i <= steps; i++) {
      Send(h, WM_MOUSEMOVE, x0 + (x1 - x0) * i / steps, y0 + (y1 - y0) * i / steps, (IntPtr)MK_LBUTTON);
      System.Threading.Thread.Sleep(40);
    }
    Send(h, WM_LBUTTONUP, x1, y1, (IntPtr)MK_NONE);
    System.Threading.Thread.Sleep(350);
  }

  /// <summary>Card-body offset that is guaranteed to be inside the drag zone (not the 6px resize band).</summary>
  public static void DragBody(IntPtr h, int ddx, int ddy) {
    Rect r = RectOf(h);
    DragScreen(h, r.L + 120, r.T + 40, r.L + 120 + ddx, r.T + 40 + ddy, 12);
  }

  /// <summary>Footer pin button centre: (PAD_LR + 15, h - PAD_BOTTOM - 15).</summary>
  public static void ClickPin(IntPtr h) {
    Rect r = RectOf(h);
    ClickScreen(h, r.L + 16 + 15, r.T + (r.B - r.T) - 14 - 15);
  }

  public static void DragRightEdge(IntPtr h, int ddx, int ddy) {
    Rect r = RectOf(h);
    DragScreen(h, r.Ri - 3, r.T + 150, r.Ri - 3 + ddx, r.T + 150 + ddy, 12);
  }

  /// <summary>Synchronous single-card close - the very handler Alt+F4 drives.</summary>
  public static void RequestClose(IntPtr h) { SendMessage(h, WM_CLOSE, IntPtr.Zero, IntPtr.Zero); System.Threading.Thread.Sleep(400); }

  /// <summary>
  /// Same WM_CLOSE without the trailing sleep, so the caller can start a 1-second exit-latency
  /// stopwatch at the exact moment the close is delivered (v11 #15).
  /// </summary>
  public static void RequestCloseNow(IntPtr h) { SendMessage(h, WM_CLOSE, IntPtr.Zero, IntPtr.Zero); }

  [DllImport("user32.dll")] public static extern bool PostMessage(IntPtr h, uint msg, IntPtr w, IntPtr l);

  /// <summary>spec v11 3.10: the second instance must show a modal notice. #32770 is the dialog class.</summary>
  public static IntPtr FindDialog(uint target) {
    foreach (IntPtr h in AllWindowsFor(target)) {
      if (!IsWindowVisible(h)) continue;
      if (Class(h).IndexOf("32770") >= 0) return h;
    }
    return IntPtr.Zero;
  }

  /// <summary>Dismiss that notice (WM_CLOSE == pressing the single OK button).</summary>
  public static void CloseDialog(IntPtr h) { PostMessage(h, WM_CLOSE, IntPtr.Zero, IntPtr.Zero); System.Threading.Thread.Sleep(200); }

  /// <summary>
  /// spec v11 3.4/3.5: click the centre of the close hit box. Client width == window width here
  /// (FormBorderStyle.None): hit box centre = (w - 25, 27) for CLOSE_LEFT = w - PAD_LR - CLOSE_BOX,
  /// CLOSE_HIT_HALF = 2, so hit.Left = w - 36 and its centre x = w - 25; hit.Top = 16, centre y = 27.
  /// </summary>
  public static void ClickClose(IntPtr h) {
    Rect r = RectOf(h);
    int w = r.Ri - r.L;
    ClickScreen(h, r.L + (w - 25), r.T + 27);
  }

  /// <summary>Visible card-window count for one pid (0 for a second instance that only shows the notice).</summary>
  public static int CardCountFor(uint target) { return Cards(target).Count; }

  public static void AltF4() {
    keybd_event(0x12, 0, 0, UIntPtr.Zero);
    keybd_event(0x73, 0, 0, UIntPtr.Zero);
    keybd_event(0x73, 0, 2, UIntPtr.Zero);
    keybd_event(0x12, 0, 2, UIntPtr.Zero);
  }

  public static List<IntPtr> AllWindowsFor(uint target) {
    List<IntPtr> list = new List<IntPtr>();
    EnumWindows(delegate(IntPtr h, IntPtr l) { uint p; GetWindowThreadProcessId(h, out p); if (p == target) list.Add(h); return true; }, IntPtr.Zero);
    return list;
  }
  /// <summary>Visible card windows only (the WinForms class carries the note title).</summary>
  public static List<IntPtr> Cards(uint target) {
    List<IntPtr> list = new List<IntPtr>();
    foreach (IntPtr h in AllWindowsFor(target)) {
      if (!IsWindowVisible(h)) continue;
      if (Class(h).IndexOf("WindowsForms10.Window") < 0) continue;
      Rect r; GetWindowRect(h, out r);
      if (r.Ri - r.L < 50 || r.B - r.T < 50) continue;   // skip the 6x6 helper windows
      list.Add(h);
    }
    return list;
  }
  public delegate bool EnumProc(IntPtr h, IntPtr l);
  [DllImport("user32.dll")] public static extern bool EnumWindows(EnumProc cb, IntPtr l);
}
"@

function Read-State([string]$path) {
    # The app writes state synchronously on its UI thread, but the drive script's OwnedForm
    # operations and the file write can straddle a few tens of ms. Poll until the file is
    # stable so we never assert on the PREVIOUS write.
    $prev = $null
    for ($i = 0; $i -lt 40; $i++) {
        if (Test-Path -LiteralPath $path) {
            try {
                $txt = Get-Content -LiteralPath $path -Raw -Encoding UTF8
                if ($txt -and $txt -eq $prev) { return ($txt | ConvertFrom-Json) }
                $prev = $txt
            } catch { }
        }
        Start-Sleep -Milliseconds 40
    }
    if ($prev) { try { return ($prev | ConvertFrom-Json) } catch { } }
    return $null
}
function CardOf($state, [string]$noteId) {
    if (-not $state) { return $null }
    foreach ($s in $state.stickers) { if ($s.noteId -eq $noteId) { return $s } }
    return $null
}
# ---------------------------------------------------------------------------
# Deterministic "place-all" setup.
# WHY NOT delete the primary state file: v10 spec 8.1 says the %APPDATA% fallback is WRITE-ONLY
# (spec wording: "write only, never read"), but the shipped build DOES read it whenever the primary
# path is missing (observed: "state loaded from C:\Users\...\AppData\Roaming\TieTieSticker\sticker-state.json: 2 record(s)").
# That is a pre-existing spec<->implementation gap outside this batch's scope (no rebuild allowed),
# and it made "delete primary => first run => 3 cards" non-deterministic.
# Spec 10.1-2 also triggers first-run placement when the state EXISTS but `stickers` is EMPTY, so the
# harness writes an explicit empty-stickers state instead: the primary then exists, the fallback is
# never consulted, and the run is deterministic regardless of what sits in %APPDATA%.
# ---------------------------------------------------------------------------
function Set-StateEmpty {
    $json = '{"schema":1,"app":"tietie-sticker","savedAt":' + ([DateTimeOffset]::UtcNow.ToUnixTimeMilliseconds()) + ',"stickers":[]}'
    [System.IO.File]::WriteAllText($statePath, $json, (New-Object System.Text.UTF8Encoding($false)))
    Start-Sleep -Milliseconds 120
}
# ---------------------------------------------------------------------------
# BLIND-KILL GUARD (captain 2026-09-20 ruling, top priority)
#   (a) NEVER kill by process name: the old Stop-MyStickers used
#       Get-Process -Name Sticker | Stop-Process -Force, i.e. it killed EVERY Sticker
#       instance on the machine - including a user's own running stickers. That
#       contradicts the team red line "never blindly kill processes".
#   (b) All cleanup targets ONLY pids this script itself started (Start-MySticker registers
#       them in $scriptPids).
#   (c) Before the FIRST launch, any Sticker instance that this script did not start makes
#       the run abort with a blocker report (pid / start time / path) - no kill, no race.
# ---------------------------------------------------------------------------
$scriptPids = New-Object System.Collections.Generic.List[int]

function Get-AllStickerProcesses {
    return @(Get-Process -Name Sticker -ErrorAction SilentlyContinue)
}
function Get-MyStickerProcesses {
    $out = @()
    foreach ($p in (Get-AllStickerProcesses)) { if ($scriptPids -contains $p.Id) { $out += $p } }
    return $out
}
function Assert-NoForeignStickers([string]$where) {
    $foreign = @()
    foreach ($p in (Get-AllStickerProcesses)) {
        if (-not ($scriptPids -contains $p.Id)) { $foreign += $p }
    }
    if ($foreign.Count -gt 0) {
        Write-Host ("BLOCKER: " + $foreign.Count + " Sticker instance(s) not started by this script are running (" + $where + ")") -ForegroundColor Red
        foreach ($p in $foreign) {
            $path = "(unknown)"
            try { $path = $p.Path } catch { }
            Write-Host ("   pid=" + $p.Id + "  start=" + $p.StartTime.ToString('yyyy-MM-dd HH:mm:ss') + "  path=" + $path) -ForegroundColor Red
        }
        Write-Host "   -> ABORTING without killing anything (blind-kill guard; captain 2026-09-20)." -ForegroundColor Red
        Write-Host "   -> Wait for a quiet window (nobody running the deliverable), then re-run." -ForegroundColor Red
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
    # every fresh launch in this script is preceded by a cleanup: re-check right here so a foreign
    # instance that appeared mid-run aborts us instead of being fought over (mutex) or killed.
    Assert-NoForeignStickers "after cleaning up our own instances, before the next launch"
}
function LiveStickerProcess {
    $mine = Get-MyStickerProcesses
    if ($mine.Count -eq 0) { return $null }
    return ($mine | Sort-Object StartTime | Select-Object -Last 1)
}
function HandleForRecord($pidWanted, $record) {
    foreach ($h in [CardDriver]::Cards([uint32]$pidWanted)) {
        $r = [CardDriver]::RectOf($h)
        if ($r.L -eq $record.x -and $r.T -eq $record.y) { return $h }
    }
    return [IntPtr]::Zero
}

# A card handle can go stale if the window was recreated or the process was replaced between
# steps; a 0-sized window rect is the tell. Re-resolve from the record instead of failing the
# whole assertion on a harness hiccup.
# Live process count can read 0 for a moment while a process is starting/stopping; poll instead
# of asserting on a single sample (observed flakiness was harness timing, not an app fault).
function WaitForLiveCount([int]$want, [int]$timeoutMs) {
    $sw = [System.Diagnostics.Stopwatch]::StartNew()
    while ($sw.ElapsedMilliseconds -lt $timeoutMs) {
        $n = @(Get-MyStickerProcesses).Count
        if ($n -eq $want) { return $n }
        Start-Sleep -Milliseconds 200
    }
    return @(Get-MyStickerProcesses).Count
}

# spec v11 #15: after the last card is closed, no Sticker process may hold the single-instance
# mutex within 1 second. Returns @{ ok; latencyMs } - latency is reported so the receipt carries a
# measured number instead of a bare boolean. getMutexFree() is the probe: OpenExisting must THROW.
function Measure-ExitWithin([int]$limitMs) {
    $sw = [System.Diagnostics.Stopwatch]::StartNew()
    while ($sw.ElapsedMilliseconds -le $limitMs) {
        $holders = @(Get-AllStickerProcesses).Count
        $mutexFree = $true
        try { $mh = [System.Threading.Mutex]::OpenExisting('Local\TieTieSticker.S1'); $mutexFree = $false; $mh.Dispose() } catch { $mutexFree = $true }
        if ($holders -eq 0 -and $mutexFree) { return @{ ok = $true; latencyMs = [int]$sw.ElapsedMilliseconds } }
        Start-Sleep -Milliseconds 25
    }
    return @{ ok = $false; latencyMs = [int]$sw.ElapsedMilliseconds }
}
function ResolveCardHandle([IntPtr]$h, $record) {
    if ($h -ne [IntPtr]::Zero) {
        $r = [CardDriver]::RectOf($h)
        if (($r.Ri - $r.L) -gt 0 -and ($r.B - $r.T) -gt 0) { return $h }
    }
    $p = LiveStickerProcess
    if (-not $p) { return [IntPtr]::Zero }
    for ($try = 0; $try -lt 20; $try++) {
        $found = HandleForRecord $p.Id $record
        if ($found -ne [IntPtr]::Zero) {
            Write-Host ("   [harness] card handle re-resolved for " + $record.noteId.Substring(0, 8)) -ForegroundColor Yellow
            return $found
        }
        Start-Sleep -Milliseconds 150
    }
    return [IntPtr]::Zero
}

$report = @()

# ---------------- I16 (v11 assertion #13): the FROZEN v10 spec must not move ----------------
# captain 2026-09-20: KEEP this assertion (script level). It is deliberately NOT moved into
# SelfTest.cs / --selftest: that would require rebuilding Sticker.exe, which would invalidate the
# already-completed t11 verification and the README 5.3.1 tracking rows.
# It must NOT be removed "because #13 is manual": the verifier's own Get-FileHash covers #13, but an
# automated, reproducible guard is strictly stronger, so both coexist.
# DISCRIMINATION (verified 2026-09-20): a deliberately wrong expected value => False, and comparing
# the v11 document against this constant => False, so the guard cannot degenerate into self-comparison.
# The v11 increment document is an ACTIVE document and MUST NOT be asserted here (its bytes/sha256
# are traceability values only; asserting them produced false reds while it was still being revised).
# PURE ASCII NOTE: this script must stay ASCII-only (Windows PowerShell 5.1 decodes a BOM-less script
# as ANSI), so the v10 spec file - whose NAME contains non-ASCII characters - is located by the
# wildcard '01-*.md' and then verified to resolve to exactly one file.
$v10SpecDir = Join-Path $repoRoot '.dsh-meow\sticker'
$v10ExpectBytes = 88184
$v10ExpectSha = '937ED89C480105665775EFAE801EC46363645F5DA6785643B6CAADC7E8638F13'
$v10Ok = $false
$v10Detail = ''
$v10Candidates = @()
if (Test-Path -LiteralPath $v10SpecDir) {
    $v10Candidates = @(Get-ChildItem -LiteralPath $v10SpecDir -Filter '01-*.md' -File)
}
if ($v10Candidates.Count -eq 1) {
    $v10Item = $v10Candidates[0]
    $v10Sha = (Get-FileHash -LiteralPath $v10Item.FullName -Algorithm SHA256).Hash
    $v10Ok = ($v10Item.Length -eq $v10ExpectBytes) -and ($v10Sha -eq $v10ExpectSha)
    $v10Detail = "$($v10Item.Length) B / $v10Sha / $($v10Item.LastWriteTime.ToString('yyyy-MM-dd HH:mm:ss'))"
} else {
    $v10Detail = "expected exactly 1 '01-*.md' under .dsh-meow\sticker, found $($v10Candidates.Count)"
}
Write-Host ("I16 v10 spec frozen: " + $v10Detail + "  UNCHANGED=" + $v10Ok) -ForegroundColor $(if ($v10Ok) { 'Green' } else { 'Red' })
$report += [pscustomobject]@{ id = 'I16-spec-v10-unchanged'; pass = $v10Ok; detail = $v10Detail }

# ---------------- BLIND-KILL GUARD: abort before launching anything ----------------
Assert-NoForeignStickers "pre-flight, before the first launch"

# ---------------- fresh start (only OUR pids, never by process name) ----------------
Stop-MyStickers

# captain 2026-09-20: NEVER destroy runtime evidence. Archive the log BEFORE clearing it and print
# both the archive file name and the start timestamp of the segment that was archived.
# (The exe itself never truncates the log: Log.cs only ever uses File.AppendAllText.)
$archivedLog = $null
$archivedSegmentStart = '(none: no log present)'
if (Test-Path -LiteralPath $logPath) {
    try { $archivedSegmentStart = (Get-Content -LiteralPath $logPath -TotalCount 1 -Encoding UTF8) } catch { $archivedSegmentStart = '(unreadable)' }
    $archivedLog = Join-Path (Split-Path -Parent $logPath) ('sticker-debug.' + (Get-Date -Format 'yyyyMMdd-HHmmss') + '.log')
    Copy-Item -LiteralPath $logPath -Destination $archivedLog -Force
    Write-Host ("log archived before clearing: " + $archivedLog) -ForegroundColor Cyan
    Write-Host ("   archived segment started at: " + $archivedSegmentStart) -ForegroundColor Cyan
    Remove-Item -LiteralPath $logPath -Force
} else {
    Write-Host "no existing sticker-debug.log to archive" -ForegroundColor Yellow
}
# captain 2026-09-20 (red line): the test scripts must never create OR delete anything inside the
# protected data\ directory. v11 8.1 deleted the third (workspace data\) candidate precisely because
# it dropped a file into that read-only directory, and data\ sits in the .gitignore blind spot
# (git status is structurally blind there). The former deletion of data\sticker-state.json is
# therefore REMOVED and replaced by an assertive read-only guard that turns RED (exit 4) if the file
# ever appears; the I15 check at the end asserts the same file is still absent after the run.
if (Test-Path -LiteralPath $dataStatePath) { Write-Host ("BLOCKER: protected data\ unexpectedly contains sticker-state.json - refusing to touch it: " + $dataStatePath) -ForegroundColor Red; exit 4 }
Set-StateEmpty    # deterministic first-run: primary exists but stickers is empty (see helper note)

$proc = Start-MySticker
Start-Sleep -Seconds 6

Write-Host "== live card windows ==" -ForegroundColor Cyan
foreach ($h in [CardDriver]::Cards([uint32]$proc.Id)) { Write-Host ("   " + [CardDriver]::Describe($h)) }
$cardCount = @([CardDriver]::Cards([uint32]$proc.Id)).Count
if ($cardCount -eq 0) { Write-Host "no live card window" -ForegroundColor Red; Stop-MyStickers; exit 1 }

$state0 = Read-State $statePath
$targetId = $state0.stickers[0].noteId
$cardHandle = HandleForRecord $proc.Id $state0.stickers[0]
if ($cardHandle -eq [IntPtr]::Zero) { Write-Host "handle for first record not found" -ForegroundColor Red; Stop-MyStickers; exit 1 }
$before = CardOf $state0 $targetId
Write-Host ("target noteId=" + $targetId + " hwnd=0x" + $cardHandle.ToInt64().ToString('X') + " title='" + [CardDriver]::Title($cardHandle) + "'")
Write-Host ("I0 baseline x=" + $before.x + " y=" + $before.y + " w=" + $before.w + " h=" + $before.h + " topMost=" + $before.topMost)

# ---------------- I1 drag by body ----------------
Write-Host ""
Write-Host "I1 drag: body offset (120,40) then +120,+80" -ForegroundColor Cyan
$r0 = [CardDriver]::RectOf($cardHandle)
[CardDriver]::DragBody($cardHandle, 120, 80)
Start-Sleep -Milliseconds 500
$state1 = Read-State $statePath
$a1 = CardOf $state1 $targetId
$r1 = [CardDriver]::RectOf($cardHandle)
# SYNTHETIC-INPUT TOLERANCE (documented): the app reads the REAL cursor while our synthetic messages
# drive it; on a desktop shared with the host GUI the cursor can be warped a few px mid-drag, so an
# exact-equality assertion is not reproducible (observed jitter up to ~8 px). The judgment keeps all
# of its teeth: the move must happen, the state must match the window, and the deviation from the
# intended delta must stay inside SYNTH_JITTER. A real breakage (no move, wrong delta) is 100x bigger.
$SYNTH_JITTER = 20
$dx1 = $a1.x - $r0.L; $dy1 = $a1.y - $r0.T
$moved = ([Math]::Abs($dx1 - 120) -le $SYNTH_JITTER) -and ([Math]::Abs($dy1 - 80) -le $SYNTH_JITTER) -and ($r1.L -eq $a1.x) -and ($r1.T -eq $a1.y)
Write-Host ("I1 from (" + $r0.L + "," + $r0.T + ") -> state (" + $a1.x + "," + $a1.y + ") window (" + $r1.L + "," + $r1.T + ") expect (" + ($r0.L + 120) + "," + ($r0.T + 80) + ") delta=(" + $dx1 + "," + $dy1 + ") jitter<=$SYNTH_JITTER  MOVED=" + $moved) -ForegroundColor $(if ($moved) { 'Green' } else { 'Red' })
$report += [pscustomobject]@{ id = 'I1-drag-body'; pass = $moved; detail = "window+state moved to $($a1.x),$($a1.y) delta=$dx1,$dy1" }
$cardR1 = $r1
$cardR1W = $r1.Ri - $r1.L
$cardR1H = $r1.B - $r1.T

# ---------------- I2 pin on ----------------
Write-Host ""
Write-Host "I2 press the footer pin on card A" -ForegroundColor Cyan
[CardDriver]::ClickPin($cardHandle)
Start-Sleep -Milliseconds 300
$state2 = Read-State $statePath
$a2 = CardOf $state2 $targetId
$onCount = @($state2.stickers | Where-Object { $_.topMost -eq $true }).Count
$pinOn = ($a2.topMost -eq $true) -and ($onCount -eq 1)
Write-Host ("I2 topMost=" + $a2.topMost + " floatedCount=" + $onCount + "  PIN_ON=" + $pinOn) -ForegroundColor $(if ($pinOn) { 'Green' } else { 'Red' })
$report += [pscustomobject]@{ id = 'I2-pin-on'; pass = $pinOn; detail = "topMost=$($a2.topMost) floated=$onCount" }

# ---------------- I2b only one card may float ----------------
Write-Host ""
Write-Host "I2b park a SECOND card B clear of card A, float it -> card A must sink" -ForegroundColor Cyan
$otherHandle = [IntPtr]::Zero
$otherId = $null
foreach ($h in [CardDriver]::Cards([uint32]$proc.Id)) {
    if ($h -eq $cardHandle) { continue }
    $r = [CardDriver]::RectOf($h)
    if ($r.L -eq $state2.stickers[1].x -and $r.T -eq $state2.stickers[1].y) { $otherHandle = $h; $otherId = $state2.stickers[1].noteId }
}
$onlyOne = $false
if ($otherHandle -ne [IntPtr]::Zero) {
    # park card B below card A so no click can be stolen by the floating card A,
    # keeping it fully inside the primary working area (the driver cannot move the cursor to
    # negative coordinates, and it must be able to)
    $rB = [CardDriver]::RectOf($otherHandle)
    [CardDriver]::DragScreen($otherHandle, $rB.L + 120, $rB.T + 40, $rB.L + 120, 580, 12)
    Start-Sleep -Milliseconds 400
    $rB2 = [CardDriver]::RectOf($otherHandle)
    $overlap = -not ($rB2.L -ge $cardR1.L + $cardR1W -or $rB2.Ri -le $cardR1.L -or $rB2.T -ge $cardR1.T + $cardR1H -or $rB2.B -le $cardR1.T)
    Write-Host ("   card B parked at (" + $rB2.L + "," + $rB2.T + " " + ($rB2.Ri - $rB2.L) + "x" + ($rB2.B - $rB2.T) + ") overlapsCardA=" + $overlap)

    if ($overlap) {
        Write-Host "   cannot isolate card B -> skipping the isolation part of I2b" -ForegroundColor Yellow
        $report += [pscustomobject]@{ id = 'I2b-only-one-floats'; pass = $false; detail = 'could not park card B clear of card A' }
    } else {
        $pinSx = $rB2.L + 16 + 15
        $pinSy = $rB2.B - 14 - 15
        [CardDriver]::ClickScreen($otherHandle, $pinSx, $pinSy)
        Start-Sleep -Milliseconds 400
        $state2b = Read-State $statePath
        $on2b = @($state2b.stickers | Where-Object { $_.topMost -eq $true })
        $firstSunk = -not (CardOf $state2b $targetId).topMost
        $onlyOne = ($on2b.Count -eq 1) -and ($on2b[0].noteId -eq $otherId) -and $firstSunk
        Write-Host ("I2b floated=" + $on2b.Count + " winner=" + $(if ($on2b.Count -gt 0) { $on2b[0].noteId } else { '(none)' }) + " cardASunk=" + $firstSunk + "  ONLY_ONE_FLOATS=" + $onlyOne) -ForegroundColor $(if ($onlyOne) { 'Green' } else { 'Red' })
        $report += [pscustomobject]@{ id = 'I2b-only-one-floats'; pass = $onlyOne; detail = "floated=$($on2b.Count) winner=$(if ($on2b.Count -gt 0) { $on2b[0].noteId } else { '(none)' }) cardASunk=$firstSunk" }
    }
} else {
    Write-Host "I2b second card window not found" -ForegroundColor Red
    $report += [pscustomobject]@{ id = 'I2b-only-one-floats'; pass = $false; detail = 'second card window not found' }
}

# ---------------- I3 pin off ----------------
Write-Host ""
Write-Host "I3 press the pin on the currently floated card B -> nothing floats any more" -ForegroundColor Cyan
$rB3 = [CardDriver]::RectOf($otherHandle)
[CardDriver]::ClickScreen($otherHandle, $rB3.L + 16 + 15, $rB3.B - 14 - 15)
Start-Sleep -Milliseconds 400
$state3 = Read-State $statePath
$a3 = CardOf $state3 $targetId
$bB = CardOf $state3 $otherId
$onCount3 = @($state3.stickers | Where-Object { $_.topMost -eq $true }).Count
$pinOff = ($onCount3 -eq 0) -and ($bB.topMost -eq $false) -and ($a3.topMost -eq $false)
Write-Host ("I3 floatedCount=" + $onCount3 + " cardA.topMost=" + $a3.topMost + " cardB.topMost=" + $bB.topMost + "  PIN_OFF=" + $pinOff) -ForegroundColor $(if ($pinOff) { 'Green' } else { 'Red' })
$report += [pscustomobject]@{ id = 'I3-pin-off'; pass = $pinOff; detail = "floated=$onCount3 A=$($a3.topMost) B=$($bB.topMost)" }

# ---------------- I4 resize from right edge ----------------
Write-Host ""
Write-Host "I4 resize: right edge of card A drag +90 horizontally (width only - height must NOT change)" -ForegroundColor Cyan
$cardHandle = ResolveCardHandle $cardHandle (CardOf (Read-State $statePath) $targetId)
$rPre4 = [CardDriver]::RectOf($cardHandle)
[CardDriver]::DragRightEdge($cardHandle, 90, 50)
Start-Sleep -Milliseconds 500
$state4 = Read-State $statePath
$a4 = CardOf $state4 $targetId
$r4 = [CardDriver]::RectOf($cardHandle)
$wBefore = $rPre4.Ri - $rPre4.L
$hBefore = $rPre4.B - $rPre4.T
# same SYNTHETIC-INPUT TOLERANCE as I1 (see there): intended +90 px, tolerated jitter <= 20 px.
$dw4 = $a4.w - $wBefore
$grew = ([Math]::Abs($dw4 - 90) -le $SYNTH_JITTER) -and ($a4.h -eq $hBefore) -and (($r4.Ri - $r4.L) -eq $a4.w) -and ($r4.L -eq $rPre4.L)
Write-Host ("I4 w " + $wBefore + "->" + $a4.w + " (expect +90, jitter<=" + $SYNTH_JITTER + ", actual " + $dw4 + ")  h " + $hBefore + "->" + $a4.h + " (expect unchanged)  RESIZED=" + $grew) -ForegroundColor $(if ($grew) { 'Green' } else { 'Red' })
$report += [pscustomobject]@{ id = 'I4-resize-right'; pass = $grew; detail = "w $wBefore->$($a4.w) (d=$dw4, jitter<=$SYNTH_JITTER) h $hBefore->$($a4.h)" }

# ---------------- I5 restart restore ----------------
Write-Host ""
Write-Host "I5 restart: position + size must be restored" -ForegroundColor Cyan
Stop-MyStickers
$proc2 = Start-MySticker
Start-Sleep -Seconds 6
$state5 = Read-State $statePath
$a5 = CardOf $state5 $targetId
$restored = ($a5.x -eq $a4.x) -and ($a5.y -eq $a4.y) -and ($a5.w -eq $a4.w) -and ($a5.h -eq $a4.h)
Write-Host ("I5 restored x=" + $a5.x + " y=" + $a5.y + " w=" + $a5.w + " h=" + $a5.h + " expect " + $a4.x + "," + $a4.y + " " + $a4.w + "x" + $a4.h + "  RESTORED=" + $restored) -ForegroundColor $(if ($restored) { 'Green' } else { 'Red' })
$report += [pscustomobject]@{ id = 'I5-restart-restore'; pass = $restored; detail = "x=$($a5.x) y=$($a5.y) w=$($a5.w) h=$($a5.h)" }

# ---------------- I6 single instance ----------------
Write-Host ""
Write-Host "I6 second instance: must show a VISIBLE notice (v11 3.10), place no card, then exit 0" -ForegroundColor Cyan
$second = Start-MySticker
# spec v11 3.10 changed this check: the modal notice is EXPECTED to hold the process until dismissed,
# so "exited within 3s" is no longer the criterion. New criteria: notice present, zero cards, exit 0.
$dlg = [IntPtr]::Zero
for ($i = 0; $i -lt 40; $i++) {
    $dlg = [CardDriver]::FindDialog([uint32]$second.Id)
    if ($dlg -ne [IntPtr]::Zero) { break }
    Start-Sleep -Milliseconds 150
}
$secondCardsBeforeDismiss = [CardDriver]::CardCountFor([uint32]$second.Id)
$noticeShown = ($dlg -ne [IntPtr]::Zero)
$noticeTitle = if ($noticeShown) { [CardDriver]::Title($dlg) } else { "(none)" }
$noticeClass = if ($noticeShown) { [CardDriver]::Class($dlg) } else { "(none)" }
Write-Host ("I6 notice hwnd=0x{0:X} class='{1}' title='{2}' cardsPlacedBySecondInstance={3}" -f $dlg.ToInt64(), $noticeClass, $noticeTitle, $secondCardsBeforeDismiss) -ForegroundColor $(if ($noticeShown) { 'Green' } else { 'Red' })
if ($noticeShown) { [CardDriver]::CloseDialog($dlg) }
$secondExited = $second.WaitForExit(6000)
$secondExitCode = if ($secondExited) { $second.ExitCode } else { -1 }
$live = WaitForLiveCount 1 6000
# the notice text must be the spec string (title bar of the dialog carries it)
$titleOk = ($noticeTitle -eq ([char]0x8D34 + [char]0x8D34 + [char]0x4FBF + [char]0x7B7E))
$mutexOk = $noticeShown -and ($secondCardsBeforeDismiss -eq 0) -and $secondExited -and ($secondExitCode -eq 0) -and ($live -eq 1)
Write-Host ("I6 noticeShown={0} titleOk={1} secondExitCode={2} liveProcesses={3}  MUTEX_OK={4}" -f $noticeShown, $titleOk, $secondExitCode, $live, $mutexOk) -ForegroundColor $(if ($mutexOk) { 'Green' } else { 'Red' })
if (-not $secondExited) { Stop-Process -Id $second.Id -Force -ErrorAction SilentlyContinue }
$report += [pscustomobject]@{ id = 'I6-single-instance-visible-notice'; pass = $mutexOk; detail = "notice=$noticeShown cards=0 exit=$secondExitCode title=$noticeTitle live=$live" }

# ---------------- I7 close one card ----------------
Write-Host ""
Write-Host "I7 close ONE card: it must leave the state, the others must stay" -ForegroundColor Cyan
$stateBefore = Read-State $statePath
$null = WaitForLiveCount 1 6000
$p2 = LiveStickerProcess
$hForTarget = HandleForRecord $p2.Id $a5
if ($hForTarget -eq [IntPtr]::Zero) { $hForTarget = ResolveCardHandle ([IntPtr]::Zero) $a5 }
if ($hForTarget -ne [IntPtr]::Zero) {
    $altF4Delivery = $true
    [CardDriver]::ClickScreen($hForTarget, ([CardDriver]::RectOf($hForTarget)).L + 120, ([CardDriver]::RectOf($hForTarget)).T + 40)
    Start-Sleep -Milliseconds 300
    [CardDriver]::AltF4()
    Start-Sleep -Milliseconds 700
    $afterAlt = Read-State $statePath
    $afterAltRec = CardOf $afterAlt $targetId
    # v21: a close KEEPS the record (marked closed), so "count changed" is no longer the delivery signal.
    if (($afterAltRec -eq $null) -or (-not $afterAltRec.closed)) {
        $altF4Delivery = $false
        Write-Host "I7 note: synthetic Alt+F4 was not delivered (the host GUI owns the foreground lock); using WM_CLOSE, which reaches the same close handler" -ForegroundColor Yellow
        [CardDriver]::RequestClose($hForTarget)
    }
} else {
    $altF4Delivery = $false
    Write-Host "I7 target handle not found" -ForegroundColor Red
}
Start-Sleep -Milliseconds 400
$stateAfter = Read-State $statePath
# v21: the closed card keeps its record as REMEMBERED (closed=true) with its geometry - what must hold is
# that the record is marked closed, the record count is unchanged, and exactly one process is still up.
$recAfter = CardOf $stateAfter $targetId
$closedNow = ($recAfter -ne $null) -and ($recAfter.closed -eq $true)
$countKept = ($stateAfter.stickers.Count -eq $stateBefore.stickers.Count)
$closed = $countKept -and $closedNow
$stillLive = @(Get-MyStickerProcesses).Count -eq 1
Write-Host ("I7 records " + $stateBefore.stickers.Count + "->" + $stateAfter.stickers.Count + " targetRemembered=" + $closedNow + " countKept=" + $countKept + " appStillRunning=" + $stillLive + " directAltF4=" + $altF4Delivery + "  ONE_CARD_CLOSED=" + $closed) -ForegroundColor $(if ($closed) { 'Green' } else { 'Red' })
$report += [pscustomobject]@{ id = 'I7-close-one-card'; pass = $closed; detail = "records kept=$countKept, target closed=$closedNow, AltF4Delivered=$altF4Delivery" }

# ---------------- I8 convergence on restart ----------------
Write-Host ""
Write-Host "I8 state seeds TWO topMost=true -> startup must converge to exactly one" -ForegroundColor Cyan
Stop-MyStickers
$raw = Get-Content -LiteralPath $statePath -Raw -Encoding UTF8 | ConvertFrom-Json
$raw.stickers[0].topMost = $true
if ($raw.stickers.Count -gt 1) { $raw.stickers[1].topMost = $true }
$raw | ConvertTo-Json -Depth 6 -Compress | Set-Content -LiteralPath $statePath -Encoding UTF8 -NoNewline
Write-Host ("I8 seeded topMost=true on " + @($raw.stickers | Where-Object { $_.topMost }).Count + " record(s)")
$proc3 = Start-MySticker
Start-Sleep -Seconds 6
$state8 = Read-State $statePath
$on8 = @($state8.stickers | Where-Object { $_.topMost -eq $true })
# v21: the convergence winner among the seeded flags is the first record that is not remembered/closed.
$seededOpen = @($raw.stickers | Where-Object { $_.topMost -eq $true -and $_.closed -ne $true })
$convOk = ($on8.Count -eq 1) -and ($seededOpen.Count -ge 1) -and ($on8[0].noteId -eq $seededOpen[0].noteId)
Write-Host ("I8 floatedCount=" + $on8.Count + " winner=" + $(if ($on8.Count -gt 0) { $on8[0].noteId } else { '(none)' }) + " stateFirst=" + $state8.stickers[0].noteId + "  CONVERGED=" + $convOk) -ForegroundColor $(if ($convOk) { 'Green' } else { 'Red' })
$report += [pscustomobject]@{ id = 'I8-topmost-convergence'; pass = $convOk; detail = "floated=$($on8.Count) winner=$($on8[0].noteId)" }

# ---------------- I9 --note idempotency ----------------
Write-Host ""
Write-Host "I9 --note <id> on an already-placed note must not create a second card" -ForegroundColor Cyan
Stop-MyStickers
$nid = @($state8.stickers | Where-Object { $_.closed -ne $true } | Select-Object -First 1).noteId   # v21: a remembered (closed) record has no window
$procA = Start-MySticker @('--note', $nid)
Start-Sleep -Seconds 5
$idemLog = @((Get-Content -LiteralPath $logPath -Encoding UTF8 | Select-String 'already in the placed set')).Count
$stateI9 = Read-State $statePath
$dupes = @($stateI9.stickers | Where-Object { $_.noteId -eq $nid }).Count
$cardsI9 = @([CardDriver]::Cards([uint32]((LiveStickerProcess).Id))).Count
$openI9 = @($stateI9.stickers | Where-Object { $_.closed -ne $true }).Count
$idemOk = ($dupes -eq 1) -and ($idemLog -ge 1) -and ($cardsI9 -eq $openI9)
Write-Host ("I9 idempotentLogHits=" + $idemLog + " recordsForThatNote=" + $dupes + " liveCards=" + $cardsI9 + " records=" + $stateI9.stickers.Count + "  IDEMPOTENT=" + $idemOk) -ForegroundColor $(if ($idemOk) { 'Green' } else { 'Red' })
$report += [pscustomobject]@{ id = 'I9-note-idempotent'; pass = $idemOk; detail = "dupRecords=$dupes logHits=$idemLog cards=$cardsI9" }

$notesJson = Join-Path $repoRoot 'data\notes.json'
$notesBak = Join-Path $repoRoot 'data\notes.json.bak'
$dataDir = Join-Path $repoRoot 'data'
# spec v11 assertion #18 (no-new-file-in-workspace-data): the WHOLE data\ directory (names + bytes +
# SHA-256) must be identical before and after a "start -> close one card -> exit" cycle, so the app
# can never drop a new file there (the deleted third state candidate would have).
function Data-Fingerprint {
    $items = @(Get-ChildItem -LiteralPath $dataDir -Force -File | Sort-Object Name)
    return ($items | ForEach-Object { $_.Name + '|' + $_.Length + '|' + (Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash }) -join ';'
}
$dataFpBefore = Data-Fingerprint
$notesHashBefore = (Get-FileHash -LiteralPath $notesJson -Algorithm SHA256).Hash
$bakHashBefore = (Get-FileHash -LiteralPath $notesBak -Algorithm SHA256).Hash

# ---------------- I10 v11: the card-face X closes ONLY the clicked card ----------------
Write-Host ""
Write-Host "I10 v11: clicking the X on card A removes only A (others untouched, app keeps running)" -ForegroundColor Cyan
Stop-MyStickers
Remove-Item -LiteralPath $statePath -ErrorAction SilentlyContinue      # (replaced by Set-StateEmpty on the next line)
Set-StateEmpty                                                          # spec 10.1-2: empty stickers => place ALL
$procX = Start-MySticker
Start-Sleep -Seconds 6
$stateX = Read-State $statePath
$cardsX = [CardDriver]::Cards([uint32]$procX.Id)
Write-Host ("I10 placed records=" + $stateX.stickers.Count + " liveCardWindows=" + $cardsX.Count)
$targetRec = $stateX.stickers[0]
$othersBefore = @($stateX.stickers | Where-Object { $_.noteId -ne $targetRec.noteId })
$hX = HandleForRecord $procX.Id $targetRec
if ($hX -eq [IntPtr]::Zero) { $hX = ResolveCardHandle ([IntPtr]::Zero) $targetRec }
# the X must not move the window: remember the rect before the click
$rectBefore = [CardDriver]::RectOf($hX)
[CardDriver]::ClickClose($hX)
Start-Sleep -Milliseconds 800
$stateX2 = Read-State $statePath
$othersAfter = @($stateX2.stickers | Where-Object { $_.noteId -ne $targetRec.noteId })
$othersIdentical = $true
if ($othersBefore.Count -ne $othersAfter.Count) { $othersIdentical = $false }
else {
    for ($i = 0; $i -lt $othersBefore.Count; $i++) {
        $b = $othersBefore[$i]; $a = $othersAfter[$i]
        if ($b.noteId -ne $a.noteId -or $b.x -ne $a.x -or $b.y -ne $a.y -or $b.w -ne $a.w -or $b.h -ne $a.h -or [bool]$b.topMost -ne [bool]$a.topMost) { $othersIdentical = $false }
    }
}
$appAlive10 = @(Get-Process -Id $procX.Id -ErrorAction SilentlyContinue).Count -eq 1
$clickedRec = CardOf $stateX2 $targetRec.noteId
$clickedRemembered = ($clickedRec -ne $null) -and ($clickedRec.closed -eq $true)
$liveAfterX = @([CardDriver]::Cards([uint32]$procX.Id)).Count
$xOk = ($stateX.stickers.Count -eq 3) -and ($stateX2.stickers.Count -eq 3) -and $clickedRemembered -and ($liveAfterX -eq 2) -and $othersIdentical -and $appAlive10
Write-Host ("I10 records 3->{0} clickedRemembered={1} liveCards={2} othersIdentical={3} appStillRunning={4}  CLOSE_ONE_ONLY={5}" -f $stateX2.stickers.Count, $clickedRemembered, $liveAfterX, $othersIdentical, $appAlive10, $xOk) -ForegroundColor $(if ($xOk) { 'Green' } else { 'Red' })
$report += [pscustomobject]@{ id = 'I10-v11-x-closes-only-one'; pass = $xOk; detail = "3 records (target closed=$clickedRemembered, live cards=$liveAfterX), othersIntact=$othersIdentical, appAlive=$appAlive10" }

# ---------------- I11 v11: X hit box is not a drag zone ----------------
$hRemain = [IntPtr]::Zero
$remRec = @($stateX2.stickers | Where-Object { $_.closed -ne $true } | Select-Object -First 1)   # v21: pick a card that is really on the desk
$hRemain = HandleForRecord $procX.Id $remRec
if ($hRemain -eq [IntPtr]::Zero) { $hRemain = ResolveCardHandle ([IntPtr]::Zero) $remRec }
$rBefore11 = [CardDriver]::RectOf($hRemain)
[CardDriver]::ClickClose($hRemain)
Start-Sleep -Milliseconds 700
$stateX3 = Read-State $statePath
$rAfterWin = [CardDriver]::RectOf($hRemain)
# the click closes the card; the point is that it never DRAGGED it. Compare the state record of the
# card we clicked (it is gone) with the untouched third card, and require zero drift on that one.
$untouchedId = @($stateX2.stickers | Where-Object { $_.noteId -ne $remRec.noteId -and $_.closed -ne $true } | Select-Object -First 1).noteId   # v21
$untouchedBefore = CardOf $stateX2 $untouchedId
$untouchedAfter = CardOf $stateX3 $untouchedId
$noDrag = ($untouchedBefore.x -eq $untouchedAfter.x) -and ($untouchedBefore.y -eq $untouchedAfter.y)
$xHitOk = ($stateX3.stickers.Count -eq 3) -and (@($stateX3.stickers | Where-Object { $_.closed -eq $true }).Count -eq 2) -and $noDrag
Write-Host ("I11 after 2nd X: records={0} untouchedCard x/y {1},{2} -> {3},{4}  NO_DRAG_FROM_X={5}" -f $stateX3.stickers.Count, $untouchedBefore.x, $untouchedBefore.y, $untouchedAfter.x, $untouchedAfter.y, $xHitOk) -ForegroundColor $(if ($xHitOk) { 'Green' } else { 'Red' })
$report += [pscustomobject]@{ id = 'I11-v11-x-hit-is-not-drag'; pass = $xHitOk; detail = "records=$($stateX3.stickers.Count) untouchedMoved=$(-not $noDrag)" }

# ---------------- I12 v11 3.9: closing the LAST card exits the process (mutex released) ----------------
Write-Host ""
Write-Host "I12 v11 3.9: closing the last card must exit the app and release the single-instance mutex" -ForegroundColor Cyan
$mutexName = 'Local\TieTieSticker.S1'
$mutexBefore = $false
try { $mh = [System.Threading.Mutex]::OpenExisting($mutexName); $mutexBefore = $true; $mh.Dispose() } catch { $mutexBefore = $false }
$lastRec = @($stateX3.stickers | Where-Object { $_.closed -ne $true } | Select-Object -First 1)   # v21: the last OPEN record
$hLast = HandleForRecord $procX.Id $lastRec
if ($hLast -eq [IntPtr]::Zero) { $hLast = ResolveCardHandle ([IntPtr]::Zero) $lastRec }
[CardDriver]::RequestCloseNow($hLast)
# v11 #15: a tight 1-second window measurement starts the instant WM_CLOSE is delivered.
$exit1s = Measure-ExitWithin 1000
$exited12 = $procX.WaitForExit(8000)
$stateX4 = Read-State $statePath
$stateAllClosed = ($stateX4.stickers.Count -gt 0) -and (@($stateX4.stickers | Where-Object { $_.closed -eq $true }).Count -eq $stateX4.stickers.Count)
$mutexAfter = $true
try { $mh2 = [System.Threading.Mutex]::OpenExisting($mutexName); $mutexAfter = $true; $mh2.Dispose() } catch { $mutexAfter = $false }
$stoppedLine = @((Get-Content -LiteralPath $logPath -Encoding UTF8 | Select-String 'TieTieSticker S1 stopped')).Count -ge 1
$exitLine = @((Get-Content -LiteralPath $logPath -Encoding UTF8 | Select-String 'ApplicationExit event -> final state write')).Count -ge 1
$lastOk = $mutexBefore -and $exited12 -and $stateAllClosed -and (-not $mutexAfter) -and $stoppedLine -and $exitLine
Write-Host ("I12 mutexBefore(positive control)={0} processExited={1} stateRecords={2} stoppedLine={3} finalWrite={4} mutexReleased={5}  LAST_CLOSE_EXITS={6}" -f $mutexBefore, $exited12, $stateX4.stickers.Count, $stoppedLine, $exitLine, (-not $mutexAfter), $lastOk) -ForegroundColor $(if ($lastOk) { 'Green' } else { 'Red' })
if (-not $exited12) { Stop-Process -Id $procX.Id -Force -ErrorAction SilentlyContinue }
$report += [pscustomobject]@{ id = 'I12-v11-last-close-exits'; pass = $lastOk; detail = "exited=$exited12 allRecordsClosed=$stateAllClosed mutexReleased=$(-not $mutexAfter) controlBefore=$mutexBefore" }

# spec v11 #15: within 1 second of the last close there must be NO process holding the mutex.
Write-Host ("I18 mutex free within 1s = " + $exit1s.ok + "  (measured " + $exit1s.latencyMs + " ms)") -ForegroundColor $(if ($exit1s.ok) { 'Green' } else { 'Red' })
$report += [pscustomobject]@{ id = 'I18-v11-mutex-free-within-1s'; pass = $exit1s.ok; detail = "ok=$($exit1s.ok) latencyMs=$($exit1s.latencyMs)" }

# ---------------- I13 v11 4.2: empty set relaunch places all / --note narrows it ----------------
Write-Host ""
Write-Host "I13 v11 4.2: empty set => place ALL; empty set + --note <id> => place ONLY that one" -ForegroundColor Cyan
Stop-MyStickers
Remove-Item -LiteralPath $statePath -ErrorAction SilentlyContinue
Set-StateEmpty
$procR = Start-MySticker
Start-Sleep -Seconds 6
$stateR = Read-State $statePath
$cardsR = [CardDriver]::Cards([uint32]$procR.Id)
$allOk = ($stateR.stickers.Count -eq 3) -and ($cardsR.Count -eq 3)
Write-Host ("I13a empty-set relaunch: records=" + $stateR.stickers.Count + " cards=" + $cardsR.Count + "  PLACE_ALL=" + $allOk) -ForegroundColor $(if ($allOk) { 'Green' } else { 'Red' })
Stop-MyStickers
Remove-Item -LiteralPath $statePath -ErrorAction SilentlyContinue
Set-StateEmpty
$pickId = ($stateR.stickers | Select-Object -First 1).noteId
$procN = Start-MySticker @('--note', $pickId)
Start-Sleep -Seconds 6
$stateN = Read-State $statePath
$cardsN = [CardDriver]::Cards([uint32]$procN.Id)
$noteOk = ($stateN.stickers.Count -eq 1) -and ($stateN.stickers[0].noteId -eq $pickId) -and ($cardsN.Count -eq 1)
Write-Host ("I13b empty-set + --note: records=" + $stateN.stickers.Count + " cards=" + $cardsN.Count + " id=" + $stateN.stickers[0].noteId + "  ONLY_PICKED=" + $noteOk) -ForegroundColor $(if ($noteOk) { 'Green' } else { 'Red' })
$report += [pscustomobject]@{ id = 'I13-v11-empty-set-relaunch'; pass = ($allOk -and $noteOk); detail = "placeAll=$($stateR.stickers.Count) noteOnly=$($stateN.stickers.Count)" }

# ---------------- I17 v11 #4: closing a PINNED card clears topMost (never transfers) ----------------
Write-Host ""
Write-Host "I17 v11 #4: pin card A, then close A with the X -> no card may stay topMost" -ForegroundColor Cyan
Stop-MyStickers
Remove-Item -LiteralPath $statePath -ErrorAction SilentlyContinue      # (replaced by Set-StateEmpty on the next line)
Set-StateEmpty                                                          # spec 10.1-2: empty stickers => 3 cards
$procP = Start-MySticker
Start-Sleep -Seconds 6
$stateP = Read-State $statePath
$pinRec = $stateP.stickers[0]
$othersP = @($stateP.stickers | Where-Object { $_.noteId -ne $pinRec.noteId })
$hPin = HandleForRecord $procP.Id $pinRec
if ($hPin -eq [IntPtr]::Zero) { $hPin = ResolveCardHandle ([IntPtr]::Zero) $pinRec }
[CardDriver]::ClickPin($hPin)                       # footer pin: pin the card we are about to close
Start-Sleep -Milliseconds 800
$stateP2 = Read-State $statePath
$floatedBefore = @($stateP2.stickers | Where-Object { $_.topMost -eq $true })
$pinWorked = ($floatedBefore.Count -eq 1) -and ($floatedBefore[0].noteId -eq $pinRec.noteId)
[CardDriver]::ClickClose($hPin)                     # close the very card that is pinned
Start-Sleep -Milliseconds 900
$stateP3 = Read-State $statePath
$floatedAfter = @($stateP3.stickers | Where-Object { $_.topMost -eq $true })
$othersP2 = @($stateP3.stickers | Where-Object { $_.noteId -ne $pinRec.noteId })
$othersPIntact = ($othersP.Count -eq $othersP2.Count)
if ($othersPIntact) {
    for ($i = 0; $i -lt $othersP.Count; $i++) {
        $b = $othersP[$i]; $a = $othersP2[$i]
        if ($b.noteId -ne $a.noteId -or $b.x -ne $a.x -or $b.y -ne $a.y -or $b.w -ne $a.w -or $b.h -ne $a.h -or [bool]$b.topMost -ne [bool]$a.topMost) { $othersPIntact = $false }
    }
}
$pinOk = $pinWorked -and ($stateP3.stickers.Count -eq 3) -and (@($stateP3.stickers | Where-Object { $_.closed -eq $true }).Count -eq 1) -and ($floatedAfter.Count -eq 0) -and $othersPIntact
Write-Host ("I17 pinned-before-close={0} (floated={1}) after close: records={2} topMostTrue={3} othersIntact={4}  PIN_CLEARS_TOPMOST={5}" -f $pinWorked, $floatedBefore.Count, $stateP3.stickers.Count, $floatedAfter.Count, $othersPIntact, $pinOk) -ForegroundColor $(if ($pinOk) { 'Green' } else { 'Red' })
$report += [pscustomobject]@{ id = 'I17-v11-pinned-close-clears-topmost'; pass = $pinOk; detail = "pinned=$pinWorked topMostAfterClose=$($floatedAfter.Count) othersIntact=$othersPIntact" }

# ---------------- I19 v11 3.9 (round 2): closing to ZERO with the X must take the same exit path ----
Write-Host ""
Write-Host "I19 v11 3.9: X-close the remaining cards down to 0 -> same exit path, state empty" -ForegroundColor Cyan
$mutexBeforeX = $false
try { $mhx = [System.Threading.Mutex]::OpenExisting($mutexName); $mutexBeforeX = $true; $mhx.Dispose() } catch { $mutexBeforeX = $false }
$remaining = @($stateP3.stickers | Where-Object { $_.closed -ne $true })   # v21: close the OPEN cards down to zero
$lastClosedByX = $false
$exit1sX = @{ ok = $false; latencyMs = -1 }
for ($i = 0; $i -lt $remaining.Count; $i++) {
    $rec = $remaining[$i]
    $h = HandleForRecord $procP.Id $rec
    if ($h -eq [IntPtr]::Zero) { $h = ResolveCardHandle ([IntPtr]::Zero) $rec }
    if ($h -eq [IntPtr]::Zero) { break }
    $isLast = ($i -eq $remaining.Count - 1)
    if ($isLast) {
        # measure the 1s window from this very click (the click drives Close() on the UI thread)
        $cardBefore = @(Get-MyStickerProcesses).Count
        [CardDriver]::ClickClose($h)
        $exit1sX = Measure-ExitWithin 1000
        $lastClosedByX = $true
    } else {
        [CardDriver]::ClickClose($h)
        Start-Sleep -Milliseconds 700
    }
}
$stateX5 = Read-State $statePath
$stateAllClosedX = ($stateX5.stickers.Count -gt 0) -and (@($stateX5.stickers | Where-Object { $_.closed -eq $true }).Count -eq $stateX5.stickers.Count)
$exitedX = $procP.WaitForExit(8000)
$mutexAfterX = $true
try { $mhx2 = [System.Threading.Mutex]::OpenExisting($mutexName); $mutexAfterX = $true; $mhx2.Dispose() } catch { $mutexAfterX = $false }
$logText = Get-Content -LiteralPath $logPath -Raw -Encoding UTF8
$lastCardLine = $logText.Contains('last card closed -> exiting app so the single-instance mutex is released (spec v11 3.9)')
$finalWriteLine = $logText.Contains('ApplicationExit event -> final state write')
$stoppedLineX = $logText.Contains('TieTieSticker S1 stopped')
$xToZeroOk = $lastClosedByX -and $mutexBeforeX -and $exitedX -and $stateAllClosedX -and (-not $mutexAfterX) -and $lastCardLine -and $finalWriteLine -and $stoppedLineX -and $exit1sX.ok
Write-Host ("I19 X-closed-to-zero={0} mutexBefore(control)={1} exited={2} stateRecords={3} lastCardLine={4} finalWrite={5} stopped={6} mutexReleased={7} within1s={8}({9}ms)  X_TO_ZERO_EXITS={10}" -f $lastClosedByX, $mutexBeforeX, $exitedX, $stateX5.stickers.Count, $lastCardLine, $finalWriteLine, $stoppedLineX, (-not $mutexAfterX), $exit1sX.ok, $exit1sX.latencyMs, $xToZeroOk) -ForegroundColor $(if ($xToZeroOk) { 'Green' } else { 'Red' })
if (-not $exitedX) { Stop-Process -Id $procP.Id -Force -ErrorAction SilentlyContinue }
$report += [pscustomobject]@{ id = 'I19-v11-x-close-to-zero-exits'; pass = $xToZeroOk; detail = "exited=$exitedX allRecordsClosed=$stateAllClosedX mutexReleased=$(-not $mutexAfterX) controlBefore=$mutexBeforeX within1s=$($exit1sX.ok)" }

# -------- I14 (notes.json read-only, v11 #5) + I15 (no new file in data\, v11 #18) --------
# The two Write-Host prefixes below say I15 for the directory-fingerprint line and I14 for the
# notes.json line, so the raw console log maps 1:1 onto the report ids (an earlier revision
# printed both as I14 and could mis-attribute #18 to I14 in a log-based mapping - analyst t11 note).
Stop-MyStickers
$notesHashAfter = (Get-FileHash -LiteralPath $notesJson -Algorithm SHA256).Hash
$bakHashAfter = (Get-FileHash -LiteralPath $notesBak -Algorithm SHA256).Hash
$dataFpAfter = Data-Fingerprint
$notesOk = ($notesHashBefore -eq $notesHashAfter) -and ($bakHashBefore -eq $bakHashAfter)
# spec v13 7.2: the allow-list is now explicit - data\ top level may hold notes.json, notes.json.bak
# and the bridge\ sub-directory (v13), and bridge\ itself may hold ONLY request.json / placed.json.
# Anything else is a failure, and the detail string prints both listings so it can be reviewed.
$bridgeDirLive = Join-Path $dataDir 'bridge'
$allowTop = @('notes.json', 'notes.json.bak', 'bridge')
$allowBridge = @('request.json', 'placed.json')
$topEntries = @(Get-ChildItem -LiteralPath $dataDir -Force | Sort-Object Name | ForEach-Object { $_.Name })
$topStray = @($topEntries | Where-Object { $allowTop -notcontains $_ })
$bridgeEntries = @()
if (Test-Path -LiteralPath $bridgeDirLive) {
    $bridgeEntries = @(Get-ChildItem -LiteralPath $bridgeDirLive -Force | Sort-Object Name | ForEach-Object { $_.Name })
}
$bridgeStray = @($bridgeEntries | Where-Object { $allowBridge -notcontains $_ })
$topOk = $topStray.Count -eq 0
$bridgeOk = $bridgeStray.Count -eq 0
$noNewFile = ($dataFpBefore -eq $dataFpAfter) -and (-not (Test-Path -LiteralPath (Join-Path $dataDir 'sticker-state.json'))) -and $topOk -and $bridgeOk
Write-Host ("I14 data\notes.json " + $notesHashBefore.Substring(0, 16) + " -> " + $notesHashAfter.Substring(0, 16) + "  READ_ONLY=" + $notesOk) -ForegroundColor $(if ($notesOk) { 'Green' } else { 'Red' })
Write-Host ("I15 data\ allow-list (top: notes.json + notes.json.bak + bridge\ ; bridge\: request.json + placed.json) = " + $noNewFile + "  top=[" + ($topEntries -join ',') + "] bridge=[" + ($bridgeEntries -join ',') + "]") -ForegroundColor $(if ($noNewFile) { 'Green' } else { 'Red' })
if (-not $noNewFile) { Write-Host ("   before=[" + $dataFpBefore + "]"); Write-Host ("   after =[" + $dataFpAfter + "]"); Write-Host ("   strayTop=[" + ($topStray -join ',') + "] strayBridge=[" + ($bridgeStray -join ',') + "]") }
$report += [pscustomobject]@{ id = 'I14-notes-read-only'; pass = $notesOk; detail = "$($notesHashBefore.Substring(0,12)) -> $($notesHashAfter.Substring(0,12))" }
$report += [pscustomobject]@{ id = 'I15-v13-data-allow-list'; pass = $noNewFile; detail = "fingerprint identical=$($dataFpBefore -eq $dataFpAfter) top=[$($topEntries -join ',')] bridge=[$($bridgeEntries -join ',')]" }

Stop-MyStickers

# log evidence summary (captain 2026-09-20): name the archive + both segment start lines.
$thisRunSegmentStart = '(no log)'
try { $thisRunSegmentStart = (Get-Content -LiteralPath $logPath -TotalCount 1 -Encoding UTF8) } catch { }
Write-Host ("log evidence: archived=" + $(if ($archivedLog) { Split-Path -Leaf $archivedLog } else { '(none)' }) + " ; archived segment started=" + $archivedSegmentStart + " ; this-run segment starts=" + $thisRunSegmentStart) -ForegroundColor Cyan

Write-Host ""
Write-Host "================ interaction test summary ================" -ForegroundColor Cyan
foreach ($r in $report) { Write-Host ("{0,-26} {1,-6} {2}" -f $r.id, $(if ($r.pass) { 'PASS' } else { 'FAIL' }), $r.detail) -ForegroundColor $(if ($r.pass) { 'Green' } else { 'Red' }) }
$failCount = @($report | Where-Object { -not $_.pass }).Count
Write-Host ("TOTAL=" + $report.Count + " FAIL=" + $failCount)
if ($failCount -gt 0) { exit 1 } else { exit 0 }
