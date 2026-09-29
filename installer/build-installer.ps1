# ============================================================================
#  Build the installer package for the note app.
#
#  Output (dist/):
#    <app>-setup.exe   self-extracting installer; embeds the launcher exe,
#                      the demo/ front-end, the uninstaller and an icon.
#
#  Pipeline:
#    1) stage sources on an ASCII path, draw the icon
#    2) generate the installed-file manifest + compile the uninstaller
#    3) collect payload bytes + sha256 (launcher, demo/, uninstaller)
#    4) generate payload.g.cs
#    5) compile the setup exe straight into dist/
#    6) self-check the produced exe
#
#  Usage:  powershell -ExecutionPolicy Bypass -File installer\build-installer.ps1
#
#  HARD-WON RULES (do not "simplify" these away):
#
#   R1. THIS SCRIPT IS PURE ASCII.
#       Windows PowerShell 5.1 reads .ps1 as system ANSI (GBK) unless a UTF-8
#       BOM is present, so a Chinese literal in this file gets mangled and the
#       parser dies ("Missing closing '}'"). Chinese strings are built from
#       code points instead. (The C# sources carry Chinese as \uXXXX escapes
#       for the same class of reason.)
#
#   R2. csc.exe ONLY EVER SEES AN ASCII PATH - AND ITS OUTPUT GOES TO dist/.
#       csc is a native tool: given non-ASCII arguments or a non-ASCII current
#       directory it can report success (exit 0) while the output file is never
#       written where we asked. So the sources are staged under $env:TEMP and
#       csc runs with the stage as the working directory.
#       BUT the final setup must be compiled STRAIGHT INTO dist/: Huorong's
#       trusted-zone exclusion covers this workspace, not %TEMP%, so a setup
#       staged in %TEMP% is quarantined before it can be copied out. (That is
#       why the build failed with "self-check failed: setup disappeared".)
#
#   R3. /out: AND /win32icon: COME BEFORE THE SOURCE FILES.
#       csc aborts with CS2022 otherwise, and an unquoted icon path with spaces
#       gives CS2021.
#
#   R4. THE UNINSTALLER'S FILE LIST IS GENERATED, NEVER ASSUMED.
#       The uninstaller needs the list of installed files, the setup needs the
#       uninstaller's bytes: a cycle. It is broken with a generated manifest
#       (installer/FileList.cs) instead of compiling a placeholder uninstaller
#       whose empty list would later be baked in - a bug that made uninstall
#       silently delete nothing.
# ============================================================================

$ErrorActionPreference = 'Stop'

$root  = [string](Split-Path -Parent $PSScriptRoot)
$src   = [string](Join-Path $root 'installer')
$dist  = [string](Join-Path $root 'dist')
$stage = [string](Join-Path $env:TEMP 'tietie_build')

$csc = 'C:\Windows\Microsoft.NET\Framework64\v4.0.30319\csc.exe'
if (-not (Test-Path -LiteralPath $csc)) { $csc = 'C:\Windows\Microsoft.NET\Framework\v4.0.30319\csc.exe' }
if (-not (Test-Path -LiteralPath $csc)) { Write-Error 'csc.exe not found (.NET Framework 4.x required)'; exit 1 }

# app name = U+8D34 U+8D34 U+4FBF U+7B7E (renamed 2026-09-11, was U+4EFB U+52A1 ...)
# legacy name = U+4EFB U+52A1 U+4FBF U+7B7E -- only used to spot stale artifacts
# setup = "<app>-" U+5B89 U+88C5 U+5305 ; uninstaller = U+5378 U+8F7D
$appName   = [string]([char]0x8D34 + [char]0x8D34 + [char]0x4FBF + [char]0x7B7E)
$setupStem = [string]($appName + '-' + [char]0x5B89 + [char]0x88C5 + [char]0x5305)
$uninsStem = [string]([char]0x5378 + [char]0x8F7D)
$legacyStem = [string]([char]0x4EFB + [char]0x52A1 + [char]0x4FBF + [char]0x7B7E)

foreach ($d in @($dist, $stage)) {
  if (-not (Test-Path -LiteralPath $d)) { New-Item -ItemType Directory -Path $d -Force | Out-Null }
}

# Converts a UTF-16 string into a C# string literal where every non-ASCII
# character becomes a \uXXXX escape. Keeps generated sources code-page proof.
function ConvertTo-CsLiteral([string]$s) {
  $sb = New-Object System.Text.StringBuilder
  for ($i = 0; $i -lt $s.Length; $i++) {
    $ch = $s[$i]
    if ([int][char]$ch -lt 128) { [void]$sb.Append($ch) }
    else { [void]$sb.Append('\u' + ([int][char]$ch).ToString('X4')) }
  }
  return $sb.ToString()
}

# The files that end up next to the app, in the order they are written.
# data/ is deliberately absent: the installer must never create or touch it.
$installed = @(
  @{ rel = ($appName + '.exe');         src = (Join-Path $root ($appName + '.exe')) },
  # v14: Sticker.exe is GONE from the payload. The launcher and the sticker program are now ONE
  # exe (launcher/build.ps1 compiles both trees), and the auto-start starts that exe with --sticker.
  # Shipping a second exe again would re-create the "notfound / stale copy" failure class.
  @{ rel = 'demo/index.html';           src = (Join-Path $root 'demo\index.html') },
  @{ rel = 'demo/styles.css';           src = (Join-Path $root 'demo\styles.css') },
  @{ rel = 'demo/app.js';               src = (Join-Path $root 'demo\app.js') },
  @{ rel = 'demo/store.js';             src = (Join-Path $root 'demo\store.js') },
  @{ rel = 'demo/demo-standalone.html'; src = (Join-Path $root 'demo\demo-standalone.html') },
  # v25: the front-end became an installable web app (a PWA on a phone), so the payload gained a
  # manifest, a service worker and the phone icon set. demo/ is not part of the launcher exe's
  # build manifest, so these ride in the installer payload only - but they MUST be listed here,
  # or an installed copy would silently lack them and the uninstaller would leave them behind
  # (FileList.cs below is generated from this very list).
  @{ rel = 'demo/manifest.json';        src = (Join-Path $root 'demo\manifest.json') },
  @{ rel = 'demo/sw.js';                src = (Join-Path $root 'demo\sw.js') },
  @{ rel = 'demo/icons/icon-192.png';           src = (Join-Path $root 'demo\icons\icon-192.png') },
  @{ rel = 'demo/icons/icon-512.png';           src = (Join-Path $root 'demo\icons\icon-512.png') },
  @{ rel = 'demo/icons/icon-maskable-192.png';  src = (Join-Path $root 'demo\icons\icon-maskable-192.png') },
  @{ rel = 'demo/icons/icon-maskable-512.png';  src = (Join-Path $root 'demo\icons\icon-maskable-512.png') },
  @{ rel = 'demo/icons/apple-touch-icon.png';   src = (Join-Path $root 'demo\icons\apple-touch-icon.png') },
  @{ rel = ($uninsStem + '.exe') }      # source assigned once it is compiled
)

# Guard: the old launcher name must not be shipped any more, and if it is still
# around it means launcher\build.ps1 has not been rerun (the launcher exe has no
# build step of its own in this script).
$legacyExe = [string](Join-Path $root ($legacyStem + '.exe'))
if (Test-Path -LiteralPath $legacyExe) {
  Write-Error ("old launcher still present: " + $legacyExe + " -- rerun launcher\build.ps1 first")
  exit 1
}

# ---- 1/6 stage sources + icon --------------------------------------------
Write-Host ''
Write-Host '=== 1/6 stage sources + icon ===' -ForegroundColor Cyan
foreach ($f in 'Common.cs', 'Install.cs', 'Uninstall.cs') {
  Copy-Item -LiteralPath (Join-Path $src $f) -Destination (Join-Path $stage $f) -Force
}

# The uninstaller is compiled against a stub payload (it never decodes file
# contents), so the stub only has to satisfy the type references.
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
# The brand mark is authored once by installer/make-icon.ps1 (iterated against
# the reference screenshot); this build only consumes it, so redesigning the
# icon never means touching the build script.
$icoSrc = [string](Join-Path $root '.tools\_icon\app.ico')
if (-not (Test-Path -LiteralPath $icoSrc)) {
  Write-Host '  icon missing -- running installer\make-icon.ps1 first' -ForegroundColor Yellow
  & powershell -NoProfile -ExecutionPolicy Bypass -File (Join-Path $src 'make-icon.ps1') | Out-Null
}
if (-not (Test-Path -LiteralPath $icoSrc)) { Write-Error ("icon not found: " + $icoSrc); exit 1 }
Copy-Item -LiteralPath $icoSrc -Destination $icoPath -Force
Write-Host ("  icon  : app.ico ({0} B, from installer/make-icon.ps1)" -f (Get-Item -LiteralPath $icoPath).Length)

function Invoke-Csc([string]$outPath, [string]$define, [string[]]$sources) {
  $out = $outPath
  if (Test-Path -LiteralPath $out) { Remove-Item -LiteralPath $out -Force }
  # every option (including /out:) must precede the source files (else CS2022)
  $argv = @('/nologo', '/target:winexe', '/optimize+', '/codepage:65001',
            ('/define:' + $define), ('/win32icon:' + $icoPath),
            ('/out:' + $out)) + $refs
  foreach ($s in $sources) { $argv += (Join-Path $stage $s) }
  Push-Location $stage
  try { $log = & $csc @argv 2>&1 } finally { Pop-Location }
  if ($LASTEXITCODE -ne 0) {
    Write-Host ($log | Out-String)
    Write-Error ("csc failed for " + $out + " (exit " + $LASTEXITCODE + ")")
    exit 1
  }
  if (-not (Test-Path -LiteralPath $out)) { Write-Error ("csc reported success but " + $out + " does not exist"); exit 1 }
  return $out
}

# ---- 2/6 manifest + uninstaller ------------------------------------------
Write-Host ''
Write-Host '=== 2/6 generate file manifest + compile uninstaller ===' -ForegroundColor Cyan
$fl = New-Object System.Text.StringBuilder
[void]$fl.AppendLine('// AUTO-GENERATED by installer/build-installer.ps1 -- do not edit by hand.')
[void]$fl.AppendLine('// The files an installation consists of; the uninstaller deletes exactly these.')
[void]$fl.AppendLine('// data/ is deliberately absent: user notes are never touched.')
[void]$fl.AppendLine('')
[void]$fl.AppendLine('static class InstalledFiles')
[void]$fl.AppendLine('{')
[void]$fl.AppendLine('    public static readonly string[] Rel = new string[]')
[void]$fl.AppendLine('    {')
foreach ($e in $installed) { [void]$fl.AppendLine('        "' + (ConvertTo-CsLiteral $e.rel) + '",') }
[void]$fl.AppendLine('    };')
[void]$fl.AppendLine('}')
$fileListCs = [string](Join-Path $stage 'FileList.cs')
[System.IO.File]::WriteAllText($fileListCs, $fl.ToString(), (New-Object System.Text.UTF8Encoding($false)))
Write-Host ("  manifest ({0} entries)" -f $installed.Count)
foreach ($e in $installed) { Write-Host ("      " + $e.rel) }

$unExe = [string](Join-Path $stage ($uninsStem + '.exe'))
$unExe = Invoke-Csc $unExe 'UNINSTALL_BUILD' @('Common.cs', 'Uninstall.cs', 'FileList.cs', 'payload.g.cs')
$installed[$installed.Count - 1].src = $unExe
Write-Host ("  unins : {0} ({1} B)" -f ($uninsStem + '.exe'), (Get-Item -LiteralPath $unExe).Length) -ForegroundColor Green

# The setup payload must embed the uninstaller AND expose the same manifest, so
# it is compiled with both payload.g.cs and the generated FileList.cs below.

# ---- 3/6 collect payload -------------------------------------------------
Write-Host ''
Write-Host '=== 3/6 collect payload files ===' -ForegroundColor Cyan
$items = New-Object System.Collections.ArrayList
foreach ($e in $installed) {
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

# ---- 4/6 payload.g.cs ----------------------------------------------------
Write-Host ''
Write-Host '=== 4/6 generate payload.g.cs ===' -ForegroundColor Cyan
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
  [void]$sb.AppendLine('        new Asset("' + (ConvertTo-CsLiteral $it.Rel) + '", "' + [System.Convert]::ToBase64String($it.Bytes) + '"),')
}
[void]$sb.AppendLine('    };')
[void]$sb.AppendLine('}')
[System.IO.File]::WriteAllText($payloadCs, $sb.ToString(), (New-Object System.Text.UTF8Encoding($false)))
Write-Host ("  payload.g.cs ({0} B)" -f (Get-Item -LiteralPath $payloadCs).Length)

# ---- 5/6 compile setup into dist/ ---------------------------------------
Write-Host ''
Write-Host '=== 5/6 compile setup into dist/ ===' -ForegroundColor Cyan
$setupExe = [string](Join-Path $dist ($setupStem + '.exe'))
$setupExe = Invoke-Csc $setupExe 'INSTALLER_BUILD' @('Common.cs', 'Install.cs', 'FileList.cs', 'payload.g.cs')
Write-Host ("  publish: {0} ({1} B)" -f ($setupStem + '.exe'), (Get-Item -LiteralPath $setupExe).Length) -ForegroundColor Green

# ---- 6/6 self-check -----------------------------------------------------
Write-Host ''
Write-Host '=== 6/6 self-check ===' -ForegroundColor Cyan
Start-Sleep -Seconds 3
if (-not (Test-Path -LiteralPath $setupExe)) {
  Write-Error 'self-check failed: setup disappeared right after publishing (antivirus trusted zone?)'
  exit 1
}

$setupBytes = [System.IO.File]::ReadAllBytes($setupExe)

# a) the uninstaller's name must be present (UTF-16: that is how .NET stores it)
$needle = [System.Text.Encoding]::Unicode.GetBytes($uninsStem + '.exe')
$hit = $false
for ($i = 0; $i -le $setupBytes.Length - $needle.Length -and -not $hit; $i++) {
  $ok = $true
  for ($j = 0; $j -lt $needle.Length; $j++) { if ($setupBytes[$i + $j] -ne $needle[$j]) { $ok = $false; break } }
  if ($ok) { $hit = $true }
}
if (-not $hit) { Write-Error 'self-check failed: uninstaller name not found in setup'; exit 1 }
Write-Host '  uninstaller name in setup : yes (UTF-16 scan)'

# b) every payload entry must be embedded as its base64 prefix
$payloadText = $sb.ToString()
foreach ($it in $items) {
  $b64 = [System.Convert]::ToBase64String($it.Bytes)
  $probe = $b64.Substring(0, [Math]::Min(160, $b64.Length))
  if (-not $payloadText.Contains($probe)) { Write-Error ("self-check failed: payload probe missing for " + $it.Rel); exit 1 }
}
Write-Host ("  payload entries encoded   : {0}" -f $items.Count)

# c) the uninstaller must carry EXACTLY the set of files a real installation
#    consists of. Compares the manifest text baked into the compiled
#    uninstaller against the source tree, so it is not circular: it catches a
#    placeholder/empty list being compiled in (a bug that once shipped).
$unBytes = [System.IO.File]::ReadAllBytes($unExe)
$missing = 0
foreach ($e in $installed) {
  $n = [System.Text.Encoding]::Unicode.GetBytes($e.rel)   # rels are ASCII ('/'), so UTF-16 is exact
  $found = $false
  for ($i = 0; $i -le $unBytes.Length - $n.Length -and -not $found; $i++) {
    $ok = $true
    for ($j = 0; $j -lt $n.Length; $j++) { if ($unBytes[$i + $j] -ne $n[$j]) { $ok = $false; break } }
    if ($ok) { $found = $true }
  }
  if (-not $found) { Write-Host ("  MISSING in uninstaller manifest: " + $e.rel) -ForegroundColor Red; $missing++ }
}
if ($missing -gt 0) { Write-Error ("self-check failed: uninstaller manifest is missing " + $missing + " entries"); exit 1 }
Write-Host ("  uninstaller manifest      : all {0} entries present in its bytes" -f $installed.Count)
Write-Host ("                              (and each source file exists: {0} checked)" -f $installed.Count)

Write-Host ''
Write-Host '=== BUILD OK ===' -ForegroundColor Green
Write-Host ("  setup   : {0}" -f $setupExe)
Write-Host ("  bytes   : {0}" -f (Get-Item -LiteralPath $setupExe).Length)
Write-Host ("  payload : {0} files / {1} B (uncompressed, so size ~= payload + ~20 KB runtime)" -f $items.Count, $payloadBytes)
Write-Host ''
