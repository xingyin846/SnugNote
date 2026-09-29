# PWA / phone app-icon generator (pure ASCII source).
#
# v25 (2026-09-29): the web front-end becomes installable on a phone, so it needs real PNG app
# icons in addition to the .ico that installer\make-icon.ps1 produces for the Windows exe.
#
# Why the phone icon is NOT the same artwork as the exe icon:
#   * iOS composites apple-touch-icon over BLACK, so the "pin only, transparent" mark the user
#     chose for the exe icon would appear as a pin on a black square;
#   * Android's "maskable" icon requires a full-bleed background, with the mark kept inside the
#     central 80% circle so the launcher may crop it to any shape;
#   * the user's own original 55x65 tile was designed as exactly that: an orange->pink gradient
#     square with the cartoon pin on it.
# So the default flavour re-creates that tile: the gradient is SAMPLED from the original and then
# re-drawn as a bilinear gradient at every size (stretching a 55x65 bitmap to 512 would be mush),
# and the pin is the same proven cut-out the Windows icon uses.
#
# Why this file does not reuse the resampler inside installer\make-icon.ps1: that one only ever
# SHRANK the mark (25x24 -> 16/24/32/48/64/128/256), so its up-scale branch had never run and was
# wrong - it widened the kernel to 3*scale, which averages the whole 25x24 source into every
# output pixel. At 7x/11x that erased the pin completely. Resize() below uses the standard filter
# scaling where up-scaling keeps the kernel 3 SOURCE pixels wide.
#
# Usage:
#   powershell -ExecutionPolicy Bypass -File installer\make-pwa-icons.ps1
#   powershell -ExecutionPolicy Bypass -File installer\make-pwa-icons.ps1 -Flavour pin
# Output goes to demo\icons\ (which the installer payload and the uninstaller list must both know
# about - see installer\build-installer.ps1 and installer\Uninstall.cs).
param(
    [ValidateSet('tile', 'pin')]
    [string]$Flavour = 'tile'
)
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing

$root    = Split-Path -Parent $PSScriptRoot
$srcPin  = Join-Path $root '.tools\_icon\_icon_source_v2.png'
$srcTile = Join-Path $root '.tools\_icon\_icon_source_v2_tile.png'
$outDir  = Join-Path $root 'demo\icons'
if (-not (Test-Path -LiteralPath $outDir)) { New-Item -ItemType Directory -Path $outDir -Force | Out-Null }
if (-not (Test-Path -LiteralPath $srcPin)) { Write-Error ("pin source not found: " + $srcPin); exit 1 }
if ($Flavour -eq 'tile' -and -not (Test-Path -LiteralPath $srcTile)) {
    Write-Error ("tile source not found: " + $srcTile); exit 1
}

Add-Type -ReferencedAssemblies 'System.Drawing' -TypeDefinition @'
using System;
using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;

public static class PwaIcon
{
    static byte ClampByte(double v)
    {
        int i = (int)Math.Round(v);
        if (i < 0) i = 0; if (i > 255) i = 255;
        return (byte)i;
    }

    static byte ClampUnit(double v)
    {
        int i = (int)Math.Round(v * 255.0);
        if (i < 0) i = 0; if (i > 255) i = 255;
        return (byte)i;
    }

    static double Kernel(double x)
    {
        if (x < 0) x = -x;
        if (x < 1e-9) return 1.0;
        if (x >= 3.0) return 0.0;
        double px = Math.PI * x;
        return 3.0 * Math.Sin(px) * Math.Sin(px / 3.0) / (px * px);
    }

    // Separable Lanczos3 in premultiplied alpha (straight-alpha resampling would blend the
    // transparent surround into the mark's edge and leave a dark halo).
    //
    // Filter scaling is the standard one: when UP-scaling, one source pixel spans several output
    // pixels and the kernel must stay 3 SOURCE pixels wide (filter = 1); when DOWN-scaling the
    // kernel must widen to 3/scale so the source is low-passed and does not alias.
    public static Bitmap Resize(Bitmap src, int dstW, int dstH)
    {
        int sw = src.Width, sh = src.Height;
        float[] sr = new float[sw * sh], sg = new float[sw * sh], sb = new float[sw * sh], sa = new float[sw * sh];
        BitmapData sd = src.LockBits(new Rectangle(0, 0, sw, sh), ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
        try
        {
            int stride = sd.Stride;
            byte[] row = new byte[Math.Abs(stride)];
            for (int y = 0; y < sh; y++)
            {
                Marshal.Copy(IntPtr.Add(sd.Scan0, y * stride), row, 0, row.Length);
                for (int x = 0; x < sw; x++)
                {
                    int o = x * 4;
                    float b = row[o] / 255f, g = row[o + 1] / 255f, r = row[o + 2] / 255f, a = row[o + 3] / 255f;
                    int i = y * sw + x;
                    sr[i] = r * a; sg[i] = g * a; sb[i] = b * a; sa[i] = a;
                }
            }
        }
        finally { src.UnlockBits(sd); }

        float[] hr = new float[dstW * sh], hg = new float[dstW * sh], hb = new float[dstW * sh], ha = new float[dstW * sh];
        double scaleX = (double)dstW / sw;
        double filterX = scaleX > 1.0 ? 1.0 : 1.0 / scaleX;
        double supportX = 3.0 * filterX;
        for (int y = 0; y < sh; y++)
        {
            for (int x = 0; x < dstW; x++)
            {
                double center = (x + 0.5) / scaleX - 0.5;
                int left = (int)Math.Floor(center - supportX), right = (int)Math.Ceiling(center + supportX);
                double wr = 0, wg = 0, wb = 0, wa = 0, wsum = 0;
                for (int k = left; k <= right; k++)
                {
                    if (k < 0 || k >= sw) continue;
                    double w = Kernel((k - center) / filterX);
                    if (w == 0) continue;
                    int i = y * sw + k;
                    wr += sr[i] * w; wg += sg[i] * w; wb += sb[i] * w; wa += sa[i] * w; wsum += w;
                }
                if (wsum == 0) wsum = 1;
                int o = y * dstW + x;
                hr[o] = (float)(wr / wsum); hg[o] = (float)(wg / wsum); hb[o] = (float)(wb / wsum); ha[o] = (float)(wa / wsum);
            }
        }

        double scaleY = (double)dstH / sh;
        double filterY = scaleY > 1.0 ? 1.0 : 1.0 / scaleY;
        double supportY = 3.0 * filterY;
        Bitmap dst = new Bitmap(dstW, dstH, PixelFormat.Format32bppArgb);
        BitmapData dd = dst.LockBits(new Rectangle(0, 0, dstW, dstH), ImageLockMode.WriteOnly, PixelFormat.Format32bppArgb);
        try
        {
            byte[] orow = new byte[Math.Abs(dd.Stride)];
            for (int y = 0; y < dstH; y++)
            {
                double center = (y + 0.5) / scaleY - 0.5;
                int top = (int)Math.Floor(center - supportY), bottom = (int)Math.Ceiling(center + supportY);
                for (int x = 0; x < dstW; x++)
                {
                    double wr = 0, wg = 0, wb = 0, wa = 0, wsum = 0;
                    for (int k = top; k <= bottom; k++)
                    {
                        if (k < 0 || k >= sh) continue;
                        double w = Kernel((k - center) / filterY);
                        if (w == 0) continue;
                        int i = k * dstW + x;
                        wr += hr[i] * w; wg += hg[i] * w; wb += hb[i] * w; wa += ha[i] * w; wsum += w;
                    }
                    if (wsum == 0) wsum = 1;
                    float a = (float)(wa / wsum);
                    float r, g, b;
                    if (a > 1e-6f) { r = (float)(wr / wsum) / a; g = (float)(wg / wsum) / a; b = (float)(wb / wsum) / a; }
                    else { r = g = b = 0; }
                    int o = x * 4;
                    orow[o] = ClampUnit(b); orow[o + 1] = ClampUnit(g); orow[o + 2] = ClampUnit(r); orow[o + 3] = ClampUnit(a);
                }
                Marshal.Copy(orow, 0, IntPtr.Add(dd.Scan0, y * dd.Stride), orow.Length);
            }
        }
        finally { dst.UnlockBits(dd); }
        return dst;
    }

    // Bilinear gradient from four sampled corner colours, re-drawn per size. Opaque everywhere,
    // which is what both apple-touch-icon and maskable icons require.
    public static Bitmap Gradient(int size, Color tl, Color tr, Color bl, Color br)
    {
        Bitmap bmp = new Bitmap(size, size, PixelFormat.Format32bppArgb);
        BitmapData bd = bmp.LockBits(new Rectangle(0, 0, size, size), ImageLockMode.WriteOnly, PixelFormat.Format32bppArgb);
        try
        {
            byte[] row = new byte[Math.Abs(bd.Stride)];
            double den = (size <= 1) ? 1.0 : (size - 1);
            for (int y = 0; y < size; y++)
            {
                double fy = y / den;
                for (int x = 0; x < size; x++)
                {
                    double fx = x / den;
                    double w00 = (1 - fx) * (1 - fy), w10 = fx * (1 - fy);
                    double w01 = (1 - fx) * fy, w11 = fx * fy;
                    int o = x * 4;
                    row[o]     = ClampByte(tl.B * w00 + tr.B * w10 + bl.B * w01 + br.B * w11);
                    row[o + 1] = ClampByte(tl.G * w00 + tr.G * w10 + bl.G * w01 + br.G * w11);
                    row[o + 2] = ClampByte(tl.R * w00 + tr.R * w10 + bl.R * w01 + br.R * w11);
                    row[o + 3] = 255;
                }
                Marshal.Copy(row, 0, IntPtr.Add(bd.Scan0, y * bd.Stride), row.Length);
            }
        }
        finally { bmp.UnlockBits(bd); }
        return bmp;
    }

    // Mild unsharp mask, same shape and rationale as make-icon.ps1: upscaling a 25x24 mark many
    // times over is inherently soft, and this buys back edge definition without ringing.
    public static Bitmap Unsharp(Bitmap src, double amount, double alphaAmount)
    {
        int w = src.Width, h = src.Height;
        Bitmap copy = new Bitmap(src);
        Bitmap dst = new Bitmap(w, h, PixelFormat.Format32bppArgb);
        BitmapData sd = copy.LockBits(new Rectangle(0, 0, w, h), ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
        BitmapData dd = dst.LockBits(new Rectangle(0, 0, w, h), ImageLockMode.WriteOnly, PixelFormat.Format32bppArgb);
        try
        {
            int stride = sd.Stride;
            byte[] srcBytes = new byte[stride * h];
            Marshal.Copy(sd.Scan0, srcBytes, 0, srcBytes.Length);
            byte[] outBytes = new byte[dd.Stride * h];
            for (int y = 0; y < h; y++)
            {
                for (int x = 0; x < w; x++)
                {
                    for (int ch = 0; ch < 4; ch++)
                    {
                        double sum = 0; int cnt = 0;
                        for (int dy = -1; dy <= 1; dy++)
                        {
                            int yy = y + dy; if (yy < 0 || yy >= h) continue;
                            for (int dx = -1; dx <= 1; dx++)
                            {
                                int xx = x + dx; if (xx < 0 || xx >= w) continue;
                                sum += srcBytes[yy * stride + xx * 4 + ch]; cnt++;
                            }
                        }
                        double blur = sum / cnt;
                        double v = srcBytes[y * stride + x * 4 + ch];
                        double amt = (ch == 3) ? alphaAmount : amount;
                        outBytes[y * dd.Stride + x * 4 + ch] = ClampUnit((v + amt * (v - blur)) / 255.0);
                    }
                }
            }
            Marshal.Copy(outBytes, 0, dd.Scan0, outBytes.Length);
        }
        finally { copy.UnlockBits(sd); dst.UnlockBits(dd); copy.Dispose(); }
        return dst;
    }
}
'@

# ---------------------------------------------------------------- sample the tile gradient
$cTL = [System.Drawing.Color]::FromArgb(255, 255, 195, 129)
$cTR = [System.Drawing.Color]::FromArgb(255, 255, 174, 145)
$cBL = [System.Drawing.Color]::FromArgb(255, 255, 173, 146)
$cBR = [System.Drawing.Color]::FromArgb(255, 255, 152, 163)
if ($Flavour -eq 'tile') {
    $tile = [System.Drawing.Bitmap]::FromFile($srcTile)
    $bg = $tile.GetPixel(1, 1)
    $minX = $tile.Width; $maxX = -1; $minY = $tile.Height; $maxY = -1
    for ($y = 0; $y -lt $tile.Height; $y++) {
        for ($x = 0; $x -lt $tile.Width; $x++) {
            $c = $tile.GetPixel($x, $y)
            $d = [Math]::Abs($c.R - $bg.R) + [Math]::Abs($c.G - $bg.G) + [Math]::Abs($c.B - $bg.B)
            if ($d -gt 24) {
                if ($x -lt $minX) { $minX = $x }
                if ($x -gt $maxX) { $maxX = $x }
                if ($y -lt $minY) { $minY = $y }
                if ($y -gt $maxY) { $maxY = $y }
            }
        }
    }
    $bw = $maxX - $minX; $bh = $maxY - $minY
    $ix = [int][Math]::Round($bw * 0.22); $iy = [int][Math]::Round($bh * 0.22)
    $cTL = $tile.GetPixel($minX + $ix, $minY + $iy)
    $cTR = $tile.GetPixel($maxX - $ix, $minY + $iy)
    $cBL = $tile.GetPixel($minX + $ix, $maxY - $iy)
    $cBR = $tile.GetPixel($maxX - $ix, $maxY - $iy)
    $tile.Dispose()
    Write-Host ("  tile body x {0}..{1} y {2}..{3} -> gradient TL=#{4:X2}{5:X2}{6:X2} TR=#{7:X2}{8:X2}{9:X2} BL=#{10:X2}{11:X2}{12:X2} BR=#{13:X2}{14:X2}{15:X2}" -f `
        $minX, $maxX, $minY, $maxY, $cTL.R, $cTL.G, $cTL.B, $cTR.R, $cTR.G, $cTR.B, $cBL.R, $cBL.G, $cBL.B, $cBR.R, $cBR.G, $cBR.B)
}

$pinSrc = [System.Drawing.Bitmap]::FromFile($srcPin)
Write-Host ("  flavour={0}  pin source {1}x{2}" -f $Flavour, $pinSrc.Width, $pinSrc.Height)

function Save-Png {
    param([System.Drawing.Bitmap]$Bmp, [string]$Path)
    $Bmp.Save($Path, [System.Drawing.Imaging.ImageFormat]::Png)
}

function New-PwaIcon {
    param([int]$Size, [double]$Frac, [double]$Amount)
    $target = [int][Math]::Round($Size * $Frac)
    $scale = $target / [Math]::Max($pinSrc.Width, $pinSrc.Height)
    $rw = [int][Math]::Max(1, [Math]::Round($pinSrc.Width * $scale))
    $rh = [int][Math]::Max(1, [Math]::Round($pinSrc.Height * $scale))
    $scaled = [PwaIcon]::Resize($pinSrc, $rw, $rh)
    $layer = New-Object System.Drawing.Bitmap $Size, $Size
    $g = [System.Drawing.Graphics]::FromImage($layer)
    $g.InterpolationMode = [System.Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
    $g.PixelOffsetMode = [System.Drawing.Drawing2D.PixelOffsetMode]::HighQuality
    $g.Clear([System.Drawing.Color]::Transparent)
    $g.DrawImage($scaled, [int]((($Size - $rw) / 2)), [int]((($Size - $rh) / 2)), $rw, $rh)
    $g.Dispose(); $scaled.Dispose()
    $pin = [PwaIcon]::Unsharp($layer, $Amount, 0.15)
    $layer.Dispose()
    if ($Flavour -eq 'pin') { return $pin }
    $canvas = [PwaIcon]::Gradient($Size, $cTL, $cTR, $cBL, $cBR)
    $g2 = [System.Drawing.Graphics]::FromImage($canvas)
    $g2.DrawImage($pin, 0, 0, $Size, $Size)
    $g2.Dispose()
    $pin.Dispose()
    return $canvas
}

# Pin coverage differs by purpose. "any" leaves a comfortable margin; "maskable" must keep the
# whole mark inside the central 80% circle, because the launcher may crop the icon to a circle.
# 0.46 gives a half-diagonal of ~0.33 of the canvas, comfortably inside the 0.40 radius.
$frontFrac = 0.56
$maskFrac  = 0.46
if ($Flavour -eq 'pin') { $frontFrac = 0.80; $maskFrac = 0.62 }

$jobs = @(
    @{ name = 'icon-192.png';            size = 192; frac = $frontFrac },
    @{ name = 'icon-512.png';            size = 512; frac = $frontFrac },
    @{ name = 'icon-maskable-192.png';   size = 192; frac = $maskFrac  },
    @{ name = 'icon-maskable-512.png';   size = 512; frac = $maskFrac  },
    @{ name = 'apple-touch-icon.png';    size = 180; frac = $frontFrac }
)

foreach ($j in $jobs) {
    $amt = 0.45
    if ($j.size -le 192) { $amt = 0.60 }
    $bmp = New-PwaIcon -Size $j.size -Frac $j.frac -Amount $amt
    $dst = Join-Path $outDir $j.name
    Save-Png -Bmp $bmp -Path $dst
    $bmp.Dispose()
    Write-Host ("    {0,-24} {1,4}px  {2,8} B" -f $j.name, $j.size, (Get-Item -LiteralPath $dst).Length)
}
$pinSrc.Dispose()

Write-Host ''
Write-Host ("demo\icons\ written ({0} files, flavour={1})" -f $jobs.Count, $Flavour)
Write-Host 'next: rerun installer\build-installer.ps1 (demo/ is part of the payload and the'
Write-Host '      uninstaller list); the launcher exe does NOT need rebuilding.'
