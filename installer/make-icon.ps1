# Icon generator (pure ASCII source).
#
# The brand mark is the user's own image: .tools/_icon_source.png (49x50).
# This script resamples it to 256x256 with a separable Lanczos3 kernel in
# premultiplied alpha (so the rounded tile keeps clean edges instead of dark
# fringes), then packs it as an ICO with a single 256x256 PNG entry, plus a
# multi-size preview sheet on the app's own surface colour.
#
# Why not redraw it as vector art: an earlier attempt reconstructed the pushpin
# from sampled pixels, but the reference's 3D shading (disc flanges seen in
# perspective) is not reproducible that way - the drawn version looked like a
# different object. The user asked to derive the icon straight from this image,
# so we resample it faithfully instead of inventing geometry.
#
# Usage: powershell -ExecutionPolicy Bypass -File installer\make-icon.ps1
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing

$root = Split-Path -Parent $PSScriptRoot
$outDir = Join-Path $root '.tools\_icon'
if (-not (Test-Path -LiteralPath $outDir)) { New-Item -ItemType Directory -Path $outDir -Force | Out-Null }
$sourcePath = Join-Path $outDir '_icon_source.png'
if (-not (Test-Path -LiteralPath $sourcePath)) { Write-Error ("icon source not found: " + $sourcePath); exit 1 }

Add-Type -ReferencedAssemblies 'System.Drawing' -TypeDefinition @'
using System;
using System.Drawing;
using System.Drawing.Imaging;

public static class IconResampler
{
    // Separable Lanczos3 resize working in premultiplied alpha. Straight-alpha
    // resampling would blend the transparent surround into the tile edge and
    // produce a dark halo.
    static double Kernel(double x)
    {
        if (x < 0) x = -x;
        if (x < 1e-9) return 1.0;
        if (x >= 3.0) return 0.0;
        double px = Math.PI * x;
        return 3.0 * Math.Sin(px) * Math.Sin(px / 3.0) / (px * px);
    }

    public static Bitmap Resize(Bitmap src, int dstW, int dstH)
    {
        int sw = src.Width, sh = src.Height;
        // read source into float RGBA
        float[] sr = new float[sw * sh], sg = new float[sw * sh], sb = new float[sw * sh], sa = new float[sw * sh];
        BitmapData sd = src.LockBits(new Rectangle(0, 0, sw, sh), ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
        try
        {
            int stride = sd.Stride;
            byte[] row = new byte[Math.Abs(stride)];
            for (int y = 0; y < sh; y++)
            {
                System.Runtime.InteropServices.Marshal.Copy(IntPtr.Add(sd.Scan0, y * stride), row, 0, row.Length);
                for (int x = 0; x < sw; x++)
                {
                    int o = x * 4;
                    float b = row[o] / 255f, g = row[o + 1] / 255f, r = row[o + 2] / 255f, a = row[o + 3] / 255f;
                    int i = y * sw + x;
                    sr[i] = r * a; sg[i] = g * a; sb[i] = b * a; sa[i] = a;   // premultiply
                }
            }
        }
        finally { src.UnlockBits(sd); }

        // horizontal pass
        float[] hr = new float[dstW * sh], hg = new float[dstW * sh], hb = new float[dstW * sh], ha = new float[dstW * sh];
        double scaleX = (double)dstW / sw;
        double supportX = scaleX > 1.0 ? 3.0 * scaleX : 3.0;
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
                    double w = Kernel((k - center) / (scaleX > 1.0 ? scaleX : 1.0));
                    if (w == 0) continue;
                    int i = y * sw + k;
                    wr += sr[i] * w; wg += sg[i] * w; wb += sb[i] * w; wa += sa[i] * w; wsum += w;
                }
                if (wsum == 0) wsum = 1;
                int o = y * dstW + x;
                hr[o] = (float)(wr / wsum); hg[o] = (float)(wg / wsum); hb[o] = (float)(wb / wsum); ha[o] = (float)(wa / wsum);
            }
        }

        // vertical pass
        double scaleY = (double)dstH / sh;
        double supportY = scaleY > 1.0 ? 3.0 * scaleY : 3.0;
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
                        double w = Kernel((k - center) / (scaleY > 1.0 ? scaleY : 1.0));
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
                    orow[o]     = Clamp(b);
                    orow[o + 1] = Clamp(g);
                    orow[o + 2] = Clamp(r);
                    orow[o + 3] = Clamp(a);
                }
                System.Runtime.InteropServices.Marshal.Copy(orow, 0, IntPtr.Add(dd.Scan0, y * dd.Stride), orow.Length);
            }
        }
        finally { dst.UnlockBits(dd); }
        return dst;
    }

    static byte Clamp(float v)
    {
        int i = (int)Math.Round(v * 255f);
        if (i < 0) i = 0; if (i > 255) i = 255;
        return (byte)i;
    }

    // Mild unsharp mask: v + amount * (v - box3x3(v)). The source mark is only
    // ~50px, so pure resampling looks soft; this restores edge definition
    // without the ringing a deconvolution would add. Alpha is sharpened with a
    // smaller amount to keep the tile outline smooth.
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
            System.Runtime.InteropServices.Marshal.Copy(sd.Scan0, srcBytes, 0, srcBytes.Length);
            byte[] outBytes = new byte[dd.Stride * h];
            for (int y = 0; y < h; y++)
            {
                for (int x = 0; x < w; x++)
                {
                    for (int ch = 0; ch < 4; ch++)
                    {
                        double sum = 0; int n = 0;
                        for (int dy = -1; dy <= 1; dy++)
                        {
                            int yy = y + dy; if (yy < 0 || yy >= h) continue;
                            for (int dx = -1; dx <= 1; dx++)
                            {
                                int xx = x + dx; if (xx < 0 || xx >= w) continue;
                                sum += srcBytes[yy * stride + xx * 4 + ch]; n++;
                            }
                        }
                        double blur = sum / n;
                        double v = srcBytes[y * stride + x * 4 + ch];
                        double amt = (ch == 3) ? alphaAmount : amount;
                        outBytes[y * dd.Stride + x * 4 + ch] = Clamp((float)((v + amt * (v - blur)) / 255.0));
                    }
                }
            }
            System.Runtime.InteropServices.Marshal.Copy(outBytes, 0, dd.Scan0, outBytes.Length);
        }
        finally { copy.UnlockBits(sd); dst.UnlockBits(dd); copy.Dispose(); }
        return dst;
    }
}
'@

$src = [System.Drawing.Bitmap]::FromFile($sourcePath)
Write-Host ("  source: {0} x {1}" -f $src.Width, $src.Height)

# Resample to the biggest square the source allows (keep aspect), then centre it
# on a transparent 256 canvas: the ICO entry must be exactly 256x256.
$big = [IconResampler]::Resize($src, $src.Width * 5, $src.Height * 5)   # 245x250 pentuple
$canvas = New-Object System.Drawing.Bitmap 256, 256
$cg = [System.Drawing.Graphics]::FromImage($canvas)
$cg.InterpolationMode = [System.Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
$cg.PixelOffsetMode = [System.Drawing.Drawing2D.PixelOffsetMode]::HighQuality
$cg.Clear([System.Drawing.Color]::Transparent)
$scale = 256.0 / [Math]::Max($big.Width, $big.Height)
$w = [int][Math]::Round($big.Width * $scale)
$h = [int][Math]::Round($big.Height * $scale)
$cg.DrawImage($big, [int]((256 - $w) / 2), [int]((256 - $h) / 2), $w, $h)
$cg.Dispose(); $big.Dispose(); $src.Dispose()

# sharpened variant (kept alongside the plain one so both can be compared)
$plain = $canvas
$sharp = [IconResampler]::Unsharp($plain, 0.55, 0.20)
$sharp.Save((Join-Path $outDir 'icon-256-sharp.png'), [System.Drawing.Imaging.ImageFormat]::Png)
$plain.Save((Join-Path $outDir 'icon-256-plain.png'), [System.Drawing.Imaging.ImageFormat]::Png)

# side-by-side comparison sheet
$cmp = New-Object System.Drawing.Bitmap 420, 190
$cp = [System.Drawing.Graphics]::FromImage($cmp)
$cp.Clear([System.Drawing.Color]::FromArgb(255, 247, 244, 238))
$cp.InterpolationMode = [System.Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
$cp.DrawImage($plain, 16, 30, 128, 128)
$cp.DrawImage($sharp, 160, 30, 128, 128)
foreach ($n in 64, 32, 16) {
  $a = New-Object System.Drawing.Bitmap $plain, $n, $n
  $b = New-Object System.Drawing.Bitmap $sharp, $n, $n
  $cp.DrawImage($a, 300, 30 + (64 - $n), $n, $n)
  $cp.DrawImage($b, 300 + $n + 6, 30 + (64 - $n), $n, $n)
  $a.Dispose(); $b.Dispose()
}
$f = New-Object System.Drawing.Font 'Segoe UI', 8
$cp.DrawString('plain', $f, [System.Drawing.Brushes]::DimGray, 16, 10)
$cp.DrawString('unsharp', $f, [System.Drawing.Brushes]::DimGray, 160, 10)
$cp.DrawString('small sizes: plain | unsharp', $f, [System.Drawing.Brushes]::DimGray, 300, 10)
$cp.Dispose()
$cmp.Save((Join-Path $outDir 'icon-compare.png'), [System.Drawing.Imaging.ImageFormat]::Png)

# the published mark uses the sharpened variant
$canvas = $sharp
$pngPath = Join-Path $outDir 'icon-256.png'
$canvas.Save($pngPath, [System.Drawing.Imaging.ImageFormat]::Png)

# preview sheet on the app's own surface colour
$sheet = New-Object System.Drawing.Bitmap 440, 190
$sh = [System.Drawing.Graphics]::FromImage($sheet)
$sh.Clear([System.Drawing.Color]::FromArgb(255, 247, 244, 238))
$sh.InterpolationMode = [System.Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
$sh.DrawImage($canvas, 16, 20, 128, 128)
$sh.DrawImage($canvas, 160, 20, 64, 64)
$sh.DrawImage($canvas, 236, 20, 48, 48)
$sh.DrawImage($canvas, 296, 20, 32, 32)
$sh.DrawImage($canvas, 340, 20, 24, 24)
$sh.DrawImage($canvas, 376, 20, 16, 16)
$sh.DrawImage($canvas, 16, 156, 24, 24)
$sh.DrawImage($canvas, 52, 156, 16, 16)
$sheetPath = Join-Path $outDir 'icon-preview.png'
$sheet.Save($sheetPath, [System.Drawing.Imaging.ImageFormat]::Png)
$sh.Dispose(); $sheet.Dispose()

# ICO: single 256x256 PNG-compressed entry
$ms = New-Object System.IO.MemoryStream
$canvas.Save($ms, [System.Drawing.Imaging.ImageFormat]::Png)
$bytes = $ms.ToArray()
$ms.Dispose()
$canvas.Dispose()

$icoPath = Join-Path $outDir 'app.ico'
$fs = [System.IO.File]::Create($icoPath)
$bw = New-Object System.IO.BinaryWriter $fs
$bw.Write([uint16]0); $bw.Write([uint16]1); $bw.Write([uint16]1)
$bw.Write([byte]0);   $bw.Write([byte]0)
$bw.Write([byte]0);   $bw.Write([byte]0)
$bw.Write([uint16]1); $bw.Write([uint16]32)
$bw.Write([uint32]$bytes.Length); $bw.Write([uint32]22)
$bw.Write($bytes)
$bw.Flush(); $bw.Close(); $fs.Close()

Write-Host ("icon-256.png     : {0} B" -f (Get-Item -LiteralPath $pngPath).Length)
Write-Host ("icon-preview.png : {0} B" -f (Get-Item -LiteralPath $sheetPath).Length)
Write-Host ("app.ico          : {0} B" -f (Get-Item -LiteralPath $icoPath).Length)
