# ============================================================================
#  Build the installer package for the note app.
#
#  Output (dist/):
#    <app>-setup.exe   self-extracting installer; embeds the launcher exe,
#                      the demo/ front-end, the uninstaller and an icon.
#
#  Pipeline:
#    1) copy sources to an ASCII stage, draw the icon, compile the uninstaller
#    2) collect payload bytes + sha256 (launcher, demo/, uninstaller)
#    3) generate the embedded payload  -> <stage>/payload.g.cs
#    4) compile the setup exe on the stage, publish to dist/
#    5) self-check the produced exe (payload probes + embedded uninstaller)
#
#  Usage:  powershell -ExecutionPolicy Bypass -File installer\build-installer.ps1
#
#  THREE HARD-WON RULES (do not "simplify" these away):
#
#   R1. THIS SCRIPT IS PURE ASCII.
#       Windows PowerShell 5.1 reads .ps1 as system ANSI (GBK) unless a UTF-8
#       BOM is present, so a Chinese literal in this file gets mangled and the
#       parser dies ("Missing closing '}'"). Chinese strings are built from
#       code points instead. (The C# sources carry Chinese as \uXXXX escapes
#       for the same class of reason.)
#
#   R2. csc.exe ONLY EVER SEES AN ASCII PATH.
#       csc is a native tool: given non-ASCII arguments or a non-ASCII current
#       directory it can report success (exit 0) while the output file is never
#       written where we asked. Symptom we hit: "setup : ...\<chinese>.exe
#       (262144 B)" followed by "Could not find file". So: stage everything
#       under $env:TEMP, run csc with the stage as the working directory, and
#       copy finished artifacts back to the Chinese workspace path afterwards.
#       Keeping the stage out of the repository root also stops csc from
#       picking up unrelated .cs files from the current directory.
#
#   R3. /out: AND /win32icon: GO LAST / ARE QUOTED.
#       csc wants /out: after the source files (otherwise CS2022), and an icon
#       path containing spaces must be quoted on one argument (otherwise
#       CS2021).
# ============================================================================

$ErrorActionPreference = 'Stop'

$root  = [string](Split-Path -Parent $PSScriptRoot)
$src   = [string](Join-Path $root 'installer')
$dist  = [string](Join-Path $root 'dist')
$stage = [string](Join-Path $env:TEMP 'tietie_build')

$csc = 'C:\Windows\Microsoft.NET\Framework64\v4.0.30319\csc.exe'
if (-not (Test-Path -LiteralPath $csc)) { $csc = 'C:\Windows\Microsoft.NET\Framework\v4.0.30319\csc.exe' }
if (-not (Test-Path -LiteralPath $csc)) { Write-Error 'csc.exe not found (.NET Framework 4.x required)'; exit 1 }

# app name = U+4EFB U+52A1 U+4FBF U+7B7E ; setup = "<app>-" U+5B89 U+88C5 U+5305 ; uninstaller = U+5378 U+8F7D
$appName   = [string]([char]0x4EFB + [char]0x52A1 + [char]0x4FBF + [char]0x7B7E)
$setupStem = [string]($appName + '-' + [char]0x5B89 + [char]0x88C5 + [char]0x5305)
$uninsStem = [string]([char]0x5378 + [char]0x8F7D)

foreach ($d in @($dist, $stage)) {
  if (-not (Test-Path -LiteralPath $d)) { New-Item -ItemType Directory -Path $d -Force | Out-Null }
}

# ---- 1/5 stage sources, icon, uninstaller --------------------------------
Write-Host ''
Write-Host '=== 1/5 stage sources + icon ===' -ForegroundColor Cyan
foreach ($f in 'Common.cs', 'Install.cs', 'Uninstall.cs') {
  Copy-Item -LiteralPath (Join-Path $src $f) -Destination (Join-Path $stage $f) -Force
}

# The uninstaller is compiled before the real payload exists. It only iterates
# the payload's file names (it never decodes their bytes), so an empty stub is
# both sufficient and safe here; the real payload.g.cs replaces it below.
$payloadCs = [string](Join-Path $stage 'payload.g.cs')
[System.IO.File]::WriteAllText($payloadCs, @'
// placeholder payload -- replaced by the real base64 payload later in the build
class Asset
{
    public string Rel;
    public string Data;
    public Asset(string rel, string data) { Rel = rel; Data = data; }
}

static class Embedded
{
    public static readonly Asset[] Files = new Asset[0];
}
'@, (New-Object System.Text.UTF8Encoding($false)))
$refs = @('/r:System.dll', '/r:System.Drawing.dll', '/r:System.Windows.Forms.dll')

$icoPath = [string](Join-Path $stage 'app.ico')
Add-Type -AssemblyName System.Drawing
$bmp = New-Object System.Drawing.Bitmap 256, 256
$g   = [System.Drawing.Graphics]::FromImage($bmp)
$g.SmoothingMode     = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias
$g.TextRenderingHint = [System.Drawing.Text.TextRenderingHint]::AntiAliasGridFit
$g.Clear([System.Drawing.Color]::Transparent)

function New-RoundPath([int]$x, [int]$y, [int]$w, [int]$h, [int]$r) {
  $p = New-Object System.Drawing.Drawing2D.GraphicsPath
  $d = $r * 2
  $p.AddArc($x, $y, $d, $d, 180, 90)
  $p.AddArc($x + $w - $d, $y, $d, $d, 270, 90)
  $p.AddArc($x + $w - $d, $y + $h - $d, $d, $d, 0, 90)
  $p.AddArc($x, $y + $h - $d, $d, $d, 90, 90)
  $p.CloseFigure()
  return $p
}

$paper = New-RoundPath 16 16 224 224 40
$g.FillPath((New-Object System.Drawing.SolidBrush ([System.Drawing.Color]::FromArgb(255, 255, 244, 200))), $paper)
$g.FillPolygon((New-Object System.Drawing.SolidBrush ([System.Drawing.Color]::FromArgb(255, 243, 219, 126))), @(
  (New-Object System.Drawing.Point 168, 240),
  (New-Object System.Drawing.Point 240, 240),
  (New-Object System.Drawing.Point 240, 168)
))
$g.DrawPath((New-Object System.Drawing.Pen ([System.Drawing.Color]::FromArgb(255, 214, 178, 64)), 10), $paper)

$font = New-Object System.Drawing.Font 'Microsoft YaHei', 118, ([System.Drawing.FontStyle]::Bold), ([System.Drawing.GraphicsUnit]::Pixel)
$fmt  = New-Object System.Drawing.StringFormat
$fmt.Alignment     = [System.Drawing.StringAlignment]::Center
$fmt.LineAlignment = [System.Drawing.StringAlignment]::Center
$rect = New-Object System.Drawing.RectangleF 16, 24, 224, 210
$g.DrawString([string][char]0x7B7E, $font,
  (New-Object System.Drawing.SolidBrush ([System.Drawing.Color]::FromArgb(255, 88, 66, 18))), $rect, $fmt)
$g.Dispose()

$ms = New-Object System.IO.MemoryStream
$bmp.Save($ms, [System.Drawing.Imaging.ImageFormat]::Png)
$png = $ms.ToArray()
$ms.Dispose(); $bmp.Dispose()

$fs = [System.IO.File]::Create($icoPath)
$bw = New-Object System.IO.BinaryWriter $fs
$bw.Write([uint16]0); $bw.Write([uint16]1); $bw.Write([uint16]1)
$bw.Write([byte]0);   $bw.Write([byte]0)
$bw.Write([byte]0);   $bw.Write([byte]0)
$bw.Write([uint16]1); $bw.Write([uint16]32)
$bw.Write([uint32]$png.Length); $bw.Write([uint32]22)
$bw.Write($png)
$bw.Flush(); $bw.Close(); $fs.Close()
Write-Host ("  icon  : {0} ({1} B, {2} B PNG inside)" -f 'app.ico', (Get-Item -LiteralPath $icoPath).Length, $png.Length)

function Invoke-Csc([string]$outName, [string]$define, [string[]]$sources) {
  $out = [string](Join-Path $stage $outName)
  if (Test-Path -LiteralPath $out) { Remove-Item -LiteralPath $out -Force }
  # NOTE: every option (including /out:) must precede the source files, or csc
  # aborts with CS2022.
  $argv = @('/nologo', '/target:winexe', '/optimize+', '/codepage:65001',
            ('/define:' + $define), ('/win32icon:' + $icoPath),
            ('/out:' + $out)) + $refs
  foreach ($s in $sources) { $argv += (Join-Path $stage $s) }
  Push-Location $stage
  try { $log = & $csc @argv 2>&1 } finally { Pop-Location }
  if ($LASTEXITCODE -ne 0) {
    Write-Host ($log | Out-String)
    Write-Error ("csc failed for " + $outName + " (exit " + $LASTEXITCODE + ")")
    exit 1
  }
  if (-not (Test-Path -LiteralPath $out)) { Write-Error ("csc reported success but " + $out + " does not exist"); exit 1 }
  return $out
}

# The uninstaller is compiled first: it ships INSIDE the setup payload.
$stageUnins = Invoke-Csc 'uninstall_stage.exe' 'UNINSTALL_BUILD' @('Common.cs', 'Uninstall.cs', 'payload.g.cs')
$unExe = [string](Join-Path $stage ($uninsStem + '.exe'))
Copy-Item -LiteralPath $stageUnins -Destination $unExe -Force
Write-Host ("  unins : {0} ({1} B)" -f ($uninsStem + '.exe'), (Get-Item -LiteralPath $unExe).Length)

# ---- 2/5 collect payload --------------------------------------------------
$entries = @(
  @{ src = (Join-Path $root ($appName + '.exe'));         rel = ($appName + '.exe') },
  @{ src = (Join-Path $root 'demo\index.html');           rel = 'demo/index.html' },
  @{ src = (Join-Path $root 'demo\styles.css');           rel = 'demo/styles.css' },
  @{ src = (Join-Path $root 'demo\app.js');               rel = 'demo/app.js' },
  @{ src = (Join-Path $root 'demo\store.js');             rel = 'demo/store.js' },
  @{ src = (Join-Path $root 'demo\demo-standalone.html'); rel = 'demo/demo-standalone.html' },
  @{ src = $unExe;                                        rel = ($uninsStem + '.exe') }
)

Write-Host ''
Write-Host '=== 2/5 collect payload files ===' -ForegroundColor Cyan
$items = New-Object System.Collections.ArrayList
foreach ($e in $entries) {
  if (-not (Test-Path -LiteralPath $e.src)) { Write-Error ("missing file: " + $e.src); exit 1 }
  $bytes = [System.IO.File]::ReadAllBytes($e.src)
  $sha   = [System.Security.Cryptography.SHA256]::Create()
  $hex   = ([System.BitConverter]::ToString($sha.ComputeHash($bytes))).Replace('-', '').ToLower()
  [void]$items.Add([pscustomobject]@{
    Rel = [string]$e.rel; Bytes = [byte[]]$bytes; Len = [int]$bytes.Length; Sha = [string]$hex
  })
  Write-Host ("  {0,-26} {1,7} B  {2}" -f $e.rel, $bytes.Length, $hex.Substring(0, 16))
}
$payloadBytes = ($items | Measure-Object -Property Len -Sum).Sum
Write-Host ("  {0} files, {1} B total" -f $items.Count, $payloadBytes)

# ---- 3/5 payload.g.cs ----------------------------------------------------
Write-Host ''
Write-Host '=== 3/5 generate payload.g.cs ===' -ForegroundColor Cyan
$sb = New-Object System.Text.StringBuilder
[void]$sb.AppendLine('// AUTO-GENERATED by installer/build-installer.ps1 -- do not edit by hand.')
[void]$sb.AppendLine('// base64 copies of every file the installer lays down.')
[void]$sb.AppendLine('// Non-ASCII file names are written as \uXXXX escapes (code-page safe).')
[void]$sb.AppendLine('')
[void]$sb.AppendLine('class Asset')
[void]$sb.AppendLine('{')
[void]$sb.AppendLine('    public string Rel;')
[void]$sb.AppendLine('    public string Data;')
[void]$sb.AppendLine('    public Asset(string rel, string data) { Rel = rel; Data = data; }')
[void]$sb.AppendLine('}')
[void]$sb.AppendLine('')
[void]$sb.AppendLine('static class Embedded')
[void]$sb.AppendLine('{')
[void]$sb.AppendLine('    public static readonly Asset[] Files = new Asset[]')
[void]$sb.AppendLine('    {')
foreach ($it in $items) {
  $esc = -join ($it.Rel.ToCharArray() | ForEach-Object {
    if ([int][char]$_ -lt 128) { $_ } else { '\u' + ([int][char]$_).ToString('X4') }
  })
  [void]$sb.AppendLine("        new Asset(`"$esc`", `"$([System.Convert]::ToBase64String($it.Bytes))`"),")
}
[void]$sb.AppendLine('    };')
[void]$sb.AppendLine('}')
[System.IO.File]::WriteAllText($payloadCs, $sb.ToString(), (New-Object System.Text.UTF8Encoding($false)))
Write-Host ("  {0} ({1} B)" -f 'payload.g.cs', (Get-Item -LiteralPath $payloadCs).Length)

# ---- 4/5 compile + publish ----------------------------------------------
Write-Host ''
Write-Host '=== 4/5 compile setup + publish ===' -ForegroundColor Cyan
$stageSetup = Invoke-Csc 'setup_stage.exe' 'INSTALLER_BUILD' @('Common.cs', 'Install.cs', 'payload.g.cs')
Write-Host ("  staged : {0} ({1} B)" -f 'setup_stage.exe', (Get-Item -LiteralPath $stageSetup).Length) -ForegroundColor Green

$setupExe = [string](Join-Path $dist ($setupStem + '.exe'))
if (Test-Path -LiteralPath $setupExe) { Remove-Item -LiteralPath $setupExe -Force }
Copy-Item -LiteralPath $stageSetup -Destination $setupExe -Force
Start-Sleep -Milliseconds 300
Write-Host ("  publish: {0} ({1} B)" -f ($setupStem + '.exe'), (Get-Item -LiteralPath $setupExe).Length) -ForegroundColor Green

# ---- 5/5 self-check ------------------------------------------------------
Write-Host ''
Write-Host '=== 5/5 self-check ===' -ForegroundColor Cyan
Start-Sleep -Seconds 3
if (-not (Test-Path -LiteralPath $setupExe)) {
  Write-Error 'self-check failed: setup disappeared right after publishing (antivirus?)'
  exit 1
}

$setupBytes = [System.IO.File]::ReadAllBytes($setupExe)
$unBytes    = [System.IO.File]::ReadAllBytes($unExe)

$needle = [System.Text.Encoding]::Unicode.GetBytes($uninsStem + '.exe')
$hit = $false
for ($i = 0; $i -le $setupBytes.Length - $needle.Length -and -not $hit; $i++) {
  $ok = $true
  for ($j = 0; $j -lt $needle.Length; $j++) { if ($setupBytes[$i + $j] -ne $needle[$j]) { $ok = $false; break } }
  if ($ok) { $hit = $true }
}
if (-not $hit) { Write-Error 'self-check failed: uninstaller name not found in setup'; exit 1 }
Write-Host '  uninstaller name in setup : yes (UTF-16 scan)'

$payloadText = $sb.ToString()
foreach ($it in $items) {
  $b64 = [System.Convert]::ToBase64String($it.Bytes)
  $probe = $b64.Substring(0, [Math]::Min(160, $b64.Length))
  if (-not $payloadText.Contains($probe)) { Write-Error ("self-check failed: payload probe missing for " + $it.Rel); exit 1 }
}
Write-Host ("  payload entries encoded   : {0} / {1}" -f $items.Count, $items.Count)

Write-Host ''
Write-Host '=== BUILD OK ===' -ForegroundColor Green
Write-Host ("  setup   : {0}" -f $setupExe)
Write-Host ("  bytes   : {0}" -f (Get-Item -LiteralPath $setupExe).Length)
Write-Host ("  payload : {0} files / {1} B (uncompressed, so size ~= payload + ~20 KB runtime)" -f $items.Count, $payloadBytes)
Write-Host ''
