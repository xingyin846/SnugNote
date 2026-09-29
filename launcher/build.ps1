# ============================================================================
#  Build the MERGED app exe (v14): ONE exe that is BOTH the launcher and the
#  desktop sticker program.
#
#  Why one exe (user request, 2026-09-21): fold Sticker.exe into <app>.exe.
#  The two source trees stay separate on disk and are compiled together here:
#     launcher/Program.cs          -> TietieLauncher (entry point, selected by /main:)
#     launcher/LauncherWindow.cs   -> the real window that replaces the old console
#     desktop-sticker/src/*.cs     -> TieTieSticker.Program (reached via --sticker)
#
#  THIS SCRIPT IS PURE ASCII (same hard rule as installer/build-installer.ps1):
#  Windows PowerShell 5.1 reads .ps1 as system ANSI (GBK) unless a UTF-8 BOM is
#  present, so a Chinese literal here would be mangled and the parser could die.
#  The Chinese exe name is therefore assembled from code points.
#
#  Output:
#     <repo>\<app>.exe                     the delivered program
#     <repo>\<app>.build-manifest.json     binds that exe to the sources it came from
#  (This framework csc has no /deterministic, so the exe hash alone can never
#   prove "the sources did not change" - the manifest digests are the authority.)
#
#  Usage: powershell -ExecutionPolicy Bypass -File launcher\build.ps1
# ============================================================================

$ErrorActionPreference = 'Stop'

function Fail([string]$msg) {
  Write-Host ("BUILD FAILED: " + $msg) -ForegroundColor Red
  exit 1
}

# ---------- locate csc.exe ----------
$csc = 'C:\Windows\Microsoft.NET\Framework64\v4.0.30319\csc.exe'
if (-not (Test-Path -LiteralPath $csc)) { $csc = 'C:\Windows\Microsoft.NET\Framework\v4.0.30319\csc.exe' }
if (-not (Test-Path -LiteralPath $csc)) { Fail 'csc.exe (.NET Framework 4.x) not found' }

# ---------- locate repo root + sources ----------
$srcRoot = if ($PSScriptRoot) { $PSScriptRoot } else { Split-Path -Parent $MyInvocation.MyCommand.Definition }
if (-not $srcRoot) { Fail 'cannot determine the launcher directory' }
$srcRoot = (Resolve-Path -LiteralPath $srcRoot).Path
$repo    = Split-Path -Parent $srcRoot

# app name = U+8D34 U+8D34 U+4FBF U+7B7E ("tietie note")
$appName = [string]([char]0x8D34 + [char]0x8D34 + [char]0x4FBF + [char]0x7B7E)
$outExe  = Join-Path $repo ($appName + '.exe')
$manifestPath = Join-Path $repo ($appName + '.build-manifest.json')

$stickerDir = Join-Path $repo 'desktop-sticker\src'
if (-not (Test-Path -LiteralPath $stickerDir)) { Fail ("sticker sources not found: " + $stickerDir) }

# every source becomes its OWN array element (joining them first would pass one
# giant argument to csc, which then "succeeds" without producing an exe).
$entries = New-Object System.Collections.ArrayList
foreach ($f in @('Program.cs', 'LauncherWindow.cs')) {
  $p = Join-Path $srcRoot $f
  if (-not (Test-Path -LiteralPath $p)) { Fail ("launcher source missing: " + $p) }
  [void]$entries.Add((Get-Item -LiteralPath $p))
}
foreach ($f in (Get-ChildItem -LiteralPath $stickerDir -Filter '*.cs' -File | Sort-Object Name)) {
  [void]$entries.Add($f)
}

# ---------- reference assemblies (same framework directory as csc) ----------
$fwDir = Split-Path -Parent $csc
$refs = @()
foreach ($name in @('System.dll', 'System.Core.dll', 'System.Drawing.dll', 'System.Windows.Forms.dll')) {
  $p = Join-Path $fwDir $name
  if (-not (Test-Path -LiteralPath $p)) { Fail ("reference assembly missing: " + $p) }
  $refs += ('/reference:"' + $p + '"')
}

# ---------- do not fight a running copy ----------
# Windows lets a running exe be RENAMED but not overwritten, so a running
# launcher makes csc fail with CS0016. Say so in plain words instead.
if (Test-Path -LiteralPath $outExe) {
  try {
    $fs = [System.IO.File]::Open($outExe, [System.IO.FileMode]::Open, [System.IO.FileAccess]::ReadWrite, [System.IO.FileShare]::None)
    $fs.Close()
  } catch {
    Fail ("the output exe is in use - close the running app, or rename it away first (Move-Item), then rerun. " + $outExe)
  }
}

$ico = Join-Path $srcRoot 'app.ico'
$iconArg = @()
if (Test-Path -LiteralPath $ico) { $iconArg = @('/win32icon:' + $ico) }

$cscArgs = @()
$cscArgs += '/nologo'
$cscArgs += '/target:winexe'          # no console window: the app owns a real taskbar window
$cscArgs += '/platform:anycpu'
$cscArgs += '/optimize+'
$cscArgs += '/warn:4'
$cscArgs += '/codepage:65001'
$cscArgs += '/main:TietieLauncher'    # two Main()s exist (launcher + sticker): pick the launcher
$cscArgs += ('/out:"' + $outExe + '"')
$cscArgs += $iconArg
$cscArgs += $refs
foreach ($e in $entries) { $cscArgs += ('"' + $e.FullName + '"') }

Write-Host ("repo     : " + $repo)
Write-Host ("sources  : " + $entries.Count + " (" + ((($entries | ForEach-Object { $_.Name }) -join ', ')) + ")")
Write-Host ("output   : " + $outExe)

$timer = [System.Diagnostics.Stopwatch]::StartNew()
& $csc $cscArgs
$cscExit = $LASTEXITCODE
$timer.Stop()
if ($cscExit -ne 0) { Fail ("csc.exe exit code " + $cscExit) }
if (-not (Test-Path -LiteralPath $outExe)) { Fail ("csc reported success but " + $outExe + " does not exist") }

# ---------- the merge must really be in the binary ----------
# A build that silently compiled only one of the two trees would still "succeed";
# so assert the types of BOTH halves are present in the produced assembly.
# Reflection is the strong form (real type enumeration) but it needs every
# referenced assembly to resolve, which ReflectionOnlyLoadFrom cannot always do -
# so a raw metadata string scan is the fallback (and it never locks the file).
$needTypes = @('TietieLauncher', 'LauncherWindow', 'StickerStarter',
               'TieTieSticker.Program', 'TieTieSticker.StickerManager', 'TieTieSticker.SelfTest')
$needTokens = @('TietieLauncher', 'LauncherWindow', 'StickerStarter', 'TieTieSticker', 'StickerManager', 'SelfTest')
$verified = $null
try {
  $asm = [System.Reflection.Assembly]::LoadFrom($outExe)
  $have = @()
  foreach ($t in $asm.GetTypes()) { $have += $t.FullName }
  $missing = @()
  foreach ($t in $needTypes) { if ($have -notcontains $t) { $missing += $t } }
  if ($missing.Count -gt 0) { Fail ("merged exe is missing types: " + ($missing -join ', ')) }
  $verified = "reflection: both halves present (" + $needTypes.Count + " types checked)"
} catch {
  Write-Host ("reflection unavailable (" + $_.Exception.Message + ") - falling back to a metadata string scan") -ForegroundColor Yellow
  $ascii = [System.Text.Encoding]::ASCII.GetString([System.IO.File]::ReadAllBytes($outExe))
  $missing = @()
  foreach ($t in $needTokens) { if ($ascii.IndexOf($t, [System.StringComparison]::Ordinal) -lt 0) { $missing += $t } }
  if ($missing.Count -gt 0) { Fail ("merged exe is missing metadata tokens: " + ($missing -join ', ')) }
  $verified = "metadata scan: both halves present (" + $needTokens.Count + " tokens found)"
}
Write-Host ("merge    : " + $verified) -ForegroundColor Green

$exeItem = Get-Item -LiteralPath $outExe
$hash = (Get-FileHash -LiteralPath $outExe -Algorithm SHA256).Hash

# ---------- manifest: bind THIS exe to THESE sources ----------
function JsonEscape([string]$s) {
  if ($null -eq $s) { return '' }
  $s = $s.Replace('\', '\\')
  $s = $s.Replace('"', '\"')
  $s = $s.Replace("`r", '\r').Replace("`n", '\n').Replace("`t", '\t')
  return $s
}

$builtAtUtc = [DateTime]::UtcNow.ToString('yyyy-MM-dd HH:mm:ss')
$lines = New-Object System.Collections.Generic.List[string]
$lines.Add('{')
$lines.Add('  "app": "tietie-merged",')
$lines.Add('  "builtAtUtc": "' + $builtAtUtc + '",')
$lines.Add('  "compiler": "' + (JsonEscape (Get-Item -LiteralPath $csc).VersionInfo.FileVersion) + '",')
$lines.Add('  "compilerPath": "' + (JsonEscape $csc) + '",')
$lines.Add('  "sourcesRoot": "' + (JsonEscape $repo) + '",')
$lines.Add('  "cscArgs": "' + (JsonEscape (($cscArgs | Where-Object { $_ -notmatch '^"' }) -join ' ')) + '",')
$lines.Add('  "exe": {')
$lines.Add('    "file": "' + (JsonEscape ($appName + '.exe')) + '",')
$lines.Add('    "bytes": ' + $exeItem.Length + ',')
$lines.Add('    "sha256": "' + $hash + '"')
$lines.Add('  },')
$lines.Add('  "sources": [')
for ($i = 0; $i -lt $entries.Count; $i++) {
  $comma = if ($i -lt $entries.Count - 1) { ',' } else { '' }
  $rel = $entries[$i].FullName.Substring($repo.Length + 1).Replace('\', '/')
  $sh = (Get-FileHash -LiteralPath $entries[$i].FullName -Algorithm SHA256).Hash
  $lines.Add('    { "name": "' + (JsonEscape $rel) + '", "bytes": ' + $entries[$i].Length + ', "sha256": "' + $sh + '" }' + $comma)
}
$lines.Add('  ]')
$lines.Add('}')
[System.IO.File]::WriteAllText($manifestPath, ($lines -join [Environment]::NewLine), (New-Object System.Text.UTF8Encoding($false)))
try {
  $null = (Get-Content -LiteralPath $manifestPath -Raw -Encoding UTF8 | ConvertFrom-Json)
  Write-Host ("manifest : " + $manifestPath + "  (" + $entries.Count + " source digests, JSON valid)") -ForegroundColor Green
} catch {
  Fail ("generated manifest is not valid JSON: " + $_.Exception.Message)
}

Write-Host ""
Write-Host ("BUILD OK  " + $exeItem.Length + " bytes  (" + $timer.ElapsedMilliseconds + " ms)") -ForegroundColor Green
Write-Host ("exe      : " + $exeItem.FullName)
Write-Host ("sha256   : " + $hash)
Write-Host ("mtime    : " + $exeItem.LastWriteTime.ToString('yyyy-MM-dd HH:mm:ss.fff'))
Write-Host ("builtAtUtc : " + $builtAtUtc)
exit 0
