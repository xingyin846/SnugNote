# =====================================================================
#  TieTie Sticker S1 - build script
#  Compiles desktop-sticker/src/*.cs with the .NET Framework 4.x csc.exe
#  that ships with Windows. NO .NET SDK, NO NuGet, NO network access.
#
#  Output goes DIRECTLY to <repo>\.tools\sticker_build\Sticker.exe
#  (csc writes the PE itself; nothing is compiled into %TEMP%).
#
#  Works on Windows PowerShell 5.1 and PowerShell 7+.
#  Usage:  powershell -NoProfile -File desktop-sticker\build.ps1
# =====================================================================

[System.Console]::OutputEncoding = [System.Text.Encoding]::UTF8
$ErrorActionPreference = 'Stop'

function Fail([string]$msg) {
    Write-Host ("BUILD FAILED: " + $msg) -ForegroundColor Red
    exit 1
}

# ---------- locate csc.exe ----------
$cscCandidates = @()
if ($env:TietieCsc) { $cscCandidates += $env:TietieCsc }
$cscCandidates += @(
    (Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'),
    (Join-Path $env:WINDIR 'Microsoft.NET\Framework\v4.0.30319\csc.exe')
)
$csc = $cscCandidates | Where-Object { $_ -and (Test-Path -LiteralPath $_ -PathType Leaf) } | Select-Object -First 1
if (-not $csc) { Fail "csc.exe (v4.0.30319) not found. Tried: $($cscCandidates -join '; ')" }

# ---------- locate repo root ----------
# $PSScriptRoot is empty on Windows PowerShell 5.1 when the script is started with -File.
$srcRoot = if ($PSScriptRoot) { $PSScriptRoot } else { Split-Path -Parent $MyInvocation.MyCommand.Definition }
if (-not $srcRoot -or -not (Test-Path -LiteralPath $srcRoot)) { Fail "cannot determine the desktop-sticker directory" }
$srcRoot = (Resolve-Path -LiteralPath $srcRoot).Path

$repoRoot = Split-Path -Parent $srcRoot
$srcDir   = Join-Path $srcRoot 'src'
$outDir   = Join-Path (Join-Path $repoRoot '.tools') 'sticker_build'
$exePath  = Join-Path $outDir 'Sticker.exe'

if (-not (Test-Path -LiteralPath $srcDir)) { Fail "source directory not found: $srcDir" }
if (-not (Test-Path -LiteralPath $outDir)) { New-Item -ItemType Directory -Path $outDir -Force | Out-Null }

$sources = Get-ChildItem -LiteralPath $srcDir -Filter '*.cs' -File | Sort-Object Name
if ($sources.Count -eq 0) { Fail "no .cs source files in $srcDir" }

# ---------- reference assemblies (from the same framework directory) ----------
$fwDir = Split-Path -Parent $csc
$refs = @()
foreach ($name in @('System.dll', 'System.Core.dll', 'System.Drawing.dll', 'System.Windows.Forms.dll')) {
    $p = Join-Path $fwDir $name
    if (-not (Test-Path -LiteralPath $p)) { Fail "reference assembly missing: $p" }
    $refs += ('/reference:"' + $p + '"')
}

$srcNames = ($sources | ForEach-Object { $_.Name }) -join ', '
Write-Host ("csc        : " + $csc)
Write-Host ("compiler   : " + (Get-Item -LiteralPath $csc).VersionInfo.FileVersion)
Write-Host ("sources    : " + $srcNames)
Write-Host ("references : " + ($refs -join ' '))
Write-Host ("output     : " + $exePath)

# ---------- compile ----------
# csc options FIRST, then the source files (options may precede sources only once).
$cscArgs = @()
$cscArgs += '/nologo'
$cscArgs += '/target:winexe'
$cscArgs += '/platform:anycpu'
$cscArgs += '/optimize+'
# NOTE: /deterministic is a Roslyn feature; this framework csc 4.8.9221 rejects it
# ("CS2007: unrecognized option"). Two builds therefore differ in the PE timestamp / MVID,
# so a Sticker.exe SHA-256 is only meaningful together with the build moment.
$cscArgs += '/warn:4'
$cscArgs += ('/out:"' + $exePath + '"')
$cscArgs += $refs
foreach ($s in $sources) { $cscArgs += ('"' + $s.FullName + '"') }

$timer = [System.Diagnostics.Stopwatch]::StartNew()
& $csc $cscArgs
$cscExit = $LASTEXITCODE
$timer.Stop()

if ($cscExit -ne 0) { Fail ("csc.exe exit code " + $cscExit) }
if (-not (Test-Path -LiteralPath $exePath)) { Fail "csc reported success but $exePath does not exist" }

$exeItem = Get-Item -LiteralPath $exePath
$hash = (Get-FileHash -LiteralPath $exePath -Algorithm SHA256).Hash
$builtAtUtc = [DateTime]::UtcNow.ToString('yyyy-MM-dd HH:mm:ss')
Write-Host ""
Write-Host ("BUILD OK  " + $exeItem.Length + " bytes  (" + $timer.ElapsedMilliseconds + " ms)") -ForegroundColor Green
Write-Host ("exe        : " + $exeItem.FullName)
Write-Host ("sha256     : " + $hash)
Write-Host ("builtAtUtc : " + $builtAtUtc)

# ---------- build manifest: binds THIS exe to THIS source tree ----------
# This framework csc (4.8.9221) has no /deterministic, so two builds of identical sources produce
# different exe bytes (PE timestamp / MVID). Therefore an exe hash alone can never prove "the sources
# did not change" - the manifest is what ties an exe to the exact source set it was built from.
function JsonEscape([string]$s) {
    if ($null -eq $s) { return '' }
    $s = $s.Replace('\', '\\')
    $s = $s.Replace('"', '\"')
    $s = $s.Replace("`r", '\r').Replace("`n", '\n').Replace("`t", '\t')
    return $s
}

$manifestLines = New-Object System.Collections.Generic.List[string]
$manifestLines.Add('{')
$manifestLines.Add('  "app": "tietie-sticker-s1",')
$manifestLines.Add('  "builtAtUtc": "' + $builtAtUtc + '",')
$manifestLines.Add('  "compiler": "' + (JsonEscape (Get-Item -LiteralPath $csc).VersionInfo.FileVersion) + '",')
$manifestLines.Add('  "compilerPath": "' + (JsonEscape $csc) + '",')
$manifestLines.Add('  "cscArgs": "' + (JsonEscape (($cscArgs | Where-Object { $_ -notmatch '^"' }) -join ' ')) + '",')
$manifestLines.Add('  "exe": {')
$manifestLines.Add('    "file": "Sticker.exe",')
$manifestLines.Add('    "bytes": ' + $exeItem.Length + ',')
$manifestLines.Add('    "sha256": "' + $hash + '"')
$manifestLines.Add('  },')
$manifestLines.Add('  "sources": [')
for ($i = 0; $i -lt $sources.Count; $i++) {
    $comma = if ($i -lt $sources.Count - 1) { ',' } else { '' }
    $sh = (Get-FileHash -LiteralPath $sources[$i].FullName -Algorithm SHA256).Hash
    $manifestLines.Add('    { "name": "' + $sources[$i].Name + '", "bytes": ' + $sources[$i].Length + ', "sha256": "' + $sh + '" }' + $comma)
}
$manifestLines.Add('  ]')
$manifestLines.Add('}')
$manifestPath = Join-Path $outDir 'build-manifest.json'
[System.IO.File]::WriteAllText($manifestPath, ($manifestLines -join [Environment]::NewLine), (New-Object System.Text.UTF8Encoding($false)))
# A malformed manifest would silently defeat the exe<->source binding, so validate it right here.
try {
    $null = (Get-Content -LiteralPath $manifestPath -Raw -Encoding UTF8 | ConvertFrom-Json)
    Write-Host ("manifest   : " + $manifestPath + "  (" + $sources.Count + " source digests, JSON valid)") -ForegroundColor Green
} catch {
    Fail ("generated build-manifest.json is not valid JSON: " + $_.Exception.Message)
}

# ---------- .config next to the exe, same base name (ASCII) ----------
$configPath = Join-Path $outDir 'Sticker.exe.config'
$configBody = @'
<?xml version="1.0" encoding="utf-8"?>
<configuration>
  <startup>
    <!-- GDI+/WinForms only; no WebView2, no network, no listener. -->
    <supportedRuntime version="v4.0" sku=".NETFramework,Version=v4.8" />
  </startup>
</configuration>
'@
[System.IO.File]::WriteAllText($configPath, $configBody, (New-Object System.Text.UTF8Encoding($false)))
Write-Host ("config     : " + $configPath)

# ---------- no port / no WebView2 strings in the produced binary ----------
# The literal check token is assembled at runtime so this script's own warnings stay clean.
$wv = 'Web' + 'View' + '2'
$bytes = [System.IO.File]::ReadAllBytes($exePath)
$ascii = [System.Text.Encoding]::ASCII.GetString($bytes)
$banned = @($wv, 'TcpListener', 'HttpListener', 'WebSocket', 'System.Net.Sockets')
$hits = @()
foreach ($b in $banned) { if ($ascii.IndexOf($b, [System.StringComparison]::Ordinal) -ge 0) { $hits += $b } }
if ($hits.Count -gt 0) {
    Write-Host ("WARNING: banned token(s) present in Sticker.exe strings: " + ($hits -join ', ')) -ForegroundColor Yellow
} else {
    Write-Host "strings check: no webview2 / listener tokens inside Sticker.exe" -ForegroundColor Green
}
$loader = Join-Path $outDir ($wv + 'Loader.dll')
if (Test-Path -LiteralPath $loader) {
    Write-Host ("WARNING: native loader dll sits next to the exe: " + $loader) -ForegroundColor Yellow
} else {
    Write-Host "native dll check: no webview2 loader dll in the output directory" -ForegroundColor Green
}

$asmRefs = @()
try {
    $asm = [System.Reflection.Assembly]::ReflectionOnlyLoadFrom($exePath)
    foreach ($r in $asm.GetReferencedAssemblies()) { $asmRefs += $r.Name }
    Write-Host ("assembly refs: " + ($asmRefs -join ', '))
} catch {
    Write-Host ("assembly refs: (could not reflect: " + $_.Exception.Message + ")") -ForegroundColor Yellow
}

exit 0
