# Icon generator for the app (pure ASCII source).
# Reconstructs the brand mark from the reference screenshot:
#   rounded light tile + warm gradient pill + diagonal pink pushpin.
# Outputs a PNG preview and an ICO (single 256x256 PNG entry) so both can be
# eyeballed before the installer build embeds them.
#
# Usage: powershell -ExecutionPolicy Bypass -File installer\make-icon.ps1
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing

$root  = Split-Path -Parent $PSScriptRoot
$outDir = Join-Path $root '.tools\_icon'
if (-not (Test-Path -LiteralPath $outDir)) { New-Item -ItemType Directory -Path $outDir -Force | Out-Null }

$S = 256
$bmp = New-Object System.Drawing.Bitmap $S, $S
$g = [System.Drawing.Graphics]::FromImage($bmp)
$g.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias
$g.InterpolationMode = [System.Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
$g.PixelOffsetMode = [System.Drawing.Drawing2D.PixelOffsetMode]::HighQuality
$g.Clear([System.Drawing.Color]::Transparent)

function New-RoundedPath([double]$x, [double]$y, [double]$w, [double]$h, [double]$r) {
  $p = New-Object System.Drawing.Drawing2D.GraphicsPath
  $d = $r * 2
  $p.AddArc([float]$x, [float]$y, [float]$d, [float]$d, 180, 90)
  $p.AddArc([float]($x + $w - $d), [float]$y, [float]$d, [float]$d, 270, 90)
  $p.AddArc([float]($x + $w - $d), [float]($y + $h - $d), [float]$d, [float]$d, 0, 90)
  $p.AddArc([float]$x, [float]($y + $h - $d), [float]$d, [float]$d, 90, 90)
  $p.CloseFigure()
  return $p
}

# --- tile (soft light rounded square, matching the app's surface colour) ---
$tile = New-RoundedPath 6 6 244 244 56
$tileBrush = New-Object System.Drawing.SolidBrush ([System.Drawing.Color]::FromArgb(255, 247, 244, 238))
$g.FillPath($tileBrush, $tile)

# --- inner pill with the warm gradient (#FFC97C -> #FFA996) --------------
$pill = New-RoundedPath 42 38 172 180 74
$grad = New-Object System.Drawing.Drawing2D.LinearGradientBrush(
  (New-Object System.Drawing.PointF 42, 38),
  (New-Object System.Drawing.PointF 214, 218),
  [System.Drawing.Color]::FromArgb(255, 255, 201, 124),
  [System.Drawing.Color]::FromArgb(255, 255, 169, 150))
$grad.WrapMode = [System.Drawing.Drawing2D.WrapMode]::TileFlipXY
$g.FillPath($grad, $pill)

# --- pushpin: rotated pin (needle up-left, head down-right) --------------
$cx = 120.0; $cy = 132.0          # pin pivot
$ang = -38.0                      # needle points to the upper left

function Rot([double]$x, [double]$y, [double]$ox, [double]$oy, [double]$deg) {
  $rad = $deg * [Math]::PI / 180.0
  $dx = $x - $ox; $dy = $y - $oy
  return New-Object System.Drawing.PointF(
    [float]($ox + $dx * [Math]::Cos($rad) - $dy * [Math]::Sin($rad)),
    [float]($oy + $dx * [Math]::Sin($rad) + $dy * [Math]::Cos($rad)))
}

# needle: thin metallic rod from pivot toward upper-left
$needleTip = Rot ($cx - 58) ($cy - 45) $cx $cy $ang
$penShadow = New-Object System.Drawing.Pen ([System.Drawing.Color]::FromArgb(255, 153, 136, 160)), 8
$penShadow.StartCap = [System.Drawing.Drawing2D.LineCap]::Round
$penShadow.EndCap   = [System.Drawing.Drawing2D.LineCap]::Round
$g.DrawLine($penShadow, [float]$needleTip.X, [float]$needleTip.Y, [float]$cx, [float]$cy)

$penSteel = New-Object System.Drawing.Pen ([System.Drawing.Color]::FromArgb(255, 206, 198, 214)), 4
$penSteel.StartCap = [System.Drawing.Drawing2D.LineCap]::Round
$penSteel.EndCap   = [System.Drawing.Drawing2D.LineCap]::Round
$g.DrawLine($penSteel, [float]$needleTip.X, [float]$needleTip.Y, [float]$cx, [float]$cy)

# needle highlight
$hl = Rot ($cx - 56) ($cy - 44) $cx $cy $ang
$penHi = New-Object System.Drawing.Pen ([System.Drawing.Color]::FromArgb(220, 255, 255, 255)), 2
$penHi.StartCap = [System.Drawing.Drawing2D.LineCap]::Round
$penHi.EndCap   = [System.Drawing.Drawing2D.LineCap]::Round
$g.DrawLine($penHi, [float]$hl.X, [float]$hl.Y, [float]($cx - 6), [float]($cy - 5))

# pin head: rounded cap sitting on the pivot, pointing down-right
$headA = Rot ($cx - 24) $cy $cx $cy $ang
$headB = Rot ($cx + 30) $cy $cx $cy $ang
$headPen = New-Object System.Drawing.Pen ([System.Drawing.Color]::FromArgb(255, 230, 39, 109)), 34
$headPen.StartCap = [System.Drawing.Drawing2D.LineCap]::Round
$headPen.EndCap   = [System.Drawing.Drawing2D.LineCap]::Round
$g.DrawLine($headPen, $headA, $headB)

# head shading (darker magenta on the underside)
$shadePen = New-Object System.Drawing.Pen ([System.Drawing.Color]::FromArgb(150, 185, 30, 88)), 13
$shadePen.StartCap = [System.Drawing.Drawing2D.LineCap]::Round
$shadePen.EndCap   = [System.Drawing.Drawing2D.LineCap]::Round
$shA = Rot ($cx + 4) ($cy + 12) $cx $cy $ang
$shB = Rot ($cx + 34) ($cy + 12) $cx $cy $ang
$g.DrawLine($shadePen, $shA, $shB)

# head highlight
$hiPen = New-Object System.Drawing.Pen ([System.Drawing.Color]::FromArgb(200, 255, 150, 190)), 7
$hiPen.StartCap = [System.Drawing.Drawing2D.LineCap]::Round
$hiPen.EndCap   = [System.Drawing.Drawing2D.LineCap]::Round
$hiA = Rot ($cx - 24) ($cy - 10) $cx $cy $ang
$hiB = Rot ($cx + 12) ($cy - 10) $cx $cy $ang
$g.DrawLine($hiPen, $hiA, $hiB)

$g.Dispose()

# --- PNG preview --------------------------------------------------------
$pngPath = Join-Path $outDir 'icon-256.png'
$bmp.Save($pngPath, [System.Drawing.Imaging.ImageFormat]::Png)

# bigger preview on the app's own background, so it can be judged in context
$sheet = New-Object System.Drawing.Bitmap 420, 200
$sg = [System.Drawing.Graphics]::FromImage($sheet)
$sg.Clear([System.Drawing.Color]::FromArgb(255, 247, 244, 238))
$sg.InterpolationMode = [System.Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
$sg.DrawImage($bmp, 20, 20, 128, 128)
$sg.DrawImage($bmp, 168, 20, 64, 64)
$sg.DrawImage($bmp, 248, 20, 32, 32)
$sg.DrawImage($bmp, 296, 20, 16, 16)
$sg.DrawImage($bmp, 20, 156, 40, 40)
$sg.DrawImage($bmp, 76, 156, 24, 24)
$sheetPath = Join-Path $outDir 'icon-preview.png'
$sheet.Save($sheetPath, [System.Drawing.Imaging.ImageFormat]::Png)
$sg.Dispose(); $sheet.Dispose()

# --- ICO (single 256x256 PNG-compressed entry) ---------------------------
$ms = New-Object System.IO.MemoryStream
$bmp.Save($ms, [System.Drawing.Imaging.ImageFormat]::Png)
$png = $ms.ToArray()
$ms.Dispose()
$bmp.Dispose()

$icoPath = Join-Path $outDir 'app.ico'
$fs = [System.IO.File]::Create($icoPath)
$bw = New-Object System.IO.BinaryWriter $fs
$bw.Write([uint16]0); $bw.Write([uint16]1); $bw.Write([uint16]1)
$bw.Write([byte]0);   $bw.Write([byte]0)
$bw.Write([byte]0);   $bw.Write([byte]0)
$bw.Write([uint16]1); $bw.Write([uint16]32)
$bw.Write([uint32]$png.Length); $bw.Write([uint32]22)
$bw.Write($png)
$bw.Flush(); $bw.Close(); $fs.Close()

Write-Host ("icon-256.png     : {0} B" -f (Get-Item -LiteralPath $pngPath).Length)
Write-Host ("icon-preview.png : {0} B  (128/64/32/16 px in a row + small sizes)" -f (Get-Item -LiteralPath $sheetPath).Length)
Write-Host ("app.ico          : {0} B" -f (Get-Item -LiteralPath $icoPath).Length)
