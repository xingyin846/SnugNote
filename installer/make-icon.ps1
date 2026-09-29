# Icon generator (pure ASCII source).
#
# v24 (2026-09-29): the brand mark changed. The user sent a 55x65 rounded-tile image (a cartoon
# 3D pushpin on an orange->pink gradient) and asked to cut the pushpin OUT of it and use that as
# the software icon, "high definition", confirmed by them first. The cut-out was produced by
# .tools\_icon_pin_hd\extract.exe (background modelled as a plane fitted to a band inside the tile,
# alpha from the residual, so the magenta cap AND the grey needle survive) and archived here as
#   _icon_source_v2.png        25x24, transparent  <- what this script renders from
#   _icon_source_v2_tile.png   the 55x65 original   <- provenance only, never rendered
# The previous mark (._icon_source.png, 49x50) is kept untouched for rollback.
#
# Two changes versus the old generator, both requested by the user's choice "use the faithful
# cut-out, and make it a proper multi-size icon":
#   * EVERY size is rendered from the source separately (16/24/32/48/64/128/256) instead of
#     packing one 256 entry and letting Windows scale it down - the small sizes are what the
#     user actually looks at in the taskbar, and the needle only survives if it is rendered at
#     the target size;
#   * the ICO carries all seven entries (all PNG-compressed, which Windows has supported since
#     Vista; the old single-256-PNG icon already proved that path works on this machine).
# The mark keeps ~86% of the canvas so it does not touch the icon edges.
#
# Honest limit, recorded so nobody "fixes" it later: the source pin is only 25x24 px. 16-48 px
# look crisp; 128/256 are soft because the pixels simply are not there. Only a bigger original
# (or a hand redraw) can change that.
#
# Usage: powershell -ExecutionPolicy Bypass -File installer\make-icon.ps1
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing

$root = Split-Path -Parent $PSScriptRoot
$outDir = Join-Path $root '.tools\_icon'
if (-not (Test-Path -LiteralPath $outDir)) { New-Item -ItemType Directory -Path $outDir -Force | Out-Null }
$sourcePath = Join-Path $outDir '_icon_source_v2.png'
if (-not (Test-Path -LiteralPath $sourcePath)) {
    # fall back to the pre-v24 mark so a fresh checkout can still build an icon
    $sourcePath = Join-Path $outDir '_icon_source.png'
    Write-Host '  WARNING: _icon_source_v2.png missing - falling back to the previous mark' -ForegroundColor Yellow
}
if (-not (Test-Path -LiteralPath $sourcePath)) { Write-Error ("icon source not found in " + $outDir); exit 1 }

Add-Type -ReferencedAssemblies 'System.Drawing' -TypeDefinition @'
using System;
using System.Drawing;
using System.Drawing.Imaging;

public static class IconResampler
{
    // Separable Lanczos3 resize working in premultiplied alpha. Straight-alpha
    // resampling would blend the transparent surround into the mark's edge and
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

    // Mild unsharp mask: v + amount * (v - box3x3(v)). The mark is tiny, so pure resampling looks
    // soft; this restores edge definition without the ringing a deconvolution would add. Alpha is
    // sharpened with a smaller amount so the silhouette stays smooth.
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
Write-Host ("  source: {0} x {1}  ({2})" -f $src.Width, $src.Height, (Split-Path -Leaf $sourcePath))

# ---- render every size from the source itself (see the header for why) ----
$sizes = @(16, 24, 32, 48, 64, 128, 256)
$entries = New-Object System.Collections.Generic.List[object]
$made = @{}
foreach ($n in $sizes) {
    # the mark keeps ~86% of the canvas, aspect preserved, centred
    $inner = [double]$n * 0.86
    $scale = $inner / [Math]::Max($src.Width, $src.Height)
    $rw = [int][Math]::Max(1, [Math]::Round($src.Width * $scale))
    $rh = [int][Math]::Max(1, [Math]::Round($src.Height * $scale))
    $scaled = [IconResampler]::Resize($src, $rw, $rh)
    $canvas = New-Object System.Drawing.Bitmap $n, $n
    $g = [System.Drawing.Graphics]::FromImage($canvas)
    $g.InterpolationMode = [System.Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
    $g.PixelOffsetMode = [System.Drawing.Drawing2D.PixelOffsetMode]::HighQuality
    $g.Clear([System.Drawing.Color]::Transparent)
    $g.DrawImage($scaled, [int]((($n - $rw) / 2)), [int]((($n - $rh) / 2)), $rw, $rh)
    $g.Dispose(); $scaled.Dispose()
    # small sizes need more help than large ones
    $amount = 0.35
    if ($n -le 24) { $amount = 0.85 } elseif ($n -le 48) { $amount = 0.65 } elseif ($n -le 64) { $amount = 0.50 }
    $final = [IconResampler]::Unsharp($canvas, $amount, 0.15)
    $canvas.Dispose()

    $ms = New-Object System.IO.MemoryStream
    $final.Save($ms, [System.Drawing.Imaging.ImageFormat]::Png)
    $entries.Add([pscustomobject]@{ size = $n; bytes = $ms.ToArray() })
    $ms.Dispose()
    $made[$n] = $final
    if ($n -eq 256) { $final.Save((Join-Path $outDir 'icon-256.png'), [System.Drawing.Imaging.ImageFormat]::Png) }
    if ($n -eq 128) { $final.Save((Join-Path $outDir 'icon-128.png'), [System.Drawing.Imaging.ImageFormat]::Png) }
    if ($n -eq 48)  { $final.Save((Join-Path $outDir 'icon-48.png'),  [System.Drawing.Imaging.ImageFormat]::Png) }
    Write-Host ("    {0,3}px -> {1,6} B  unsharp {2}" -f $n, $entries[$entries.Count - 1].bytes.Length, $amount)
}
$src.Dispose()

# ---- multi-size ICO (all entries PNG-compressed) ----
$icoPath = Join-Path $outDir 'app.ico'
$fs = [System.IO.File]::Create($icoPath)
$bw = New-Object System.IO.BinaryWriter $fs
$bw.Write([uint16]0); $bw.Write([uint16]1); $bw.Write([uint16]$entries.Count)
$offset = 6 + (16 * $entries.Count)
foreach ($e in $entries) {
    $bw.Write([byte]($e.size -band 0xFF));   # 0 means 256
    $bw.Write([byte]($e.size -band 0xFF));
    $bw.Write([byte]0); $bw.Write([byte]0);  # palette count, reserved
    $bw.Write([uint16]1); $bw.Write([uint16]32)
    $bw.Write([uint32]$e.bytes.Length)
    $bw.Write([uint32]$offset)
    $offset += $e.bytes.Length
}
foreach ($e in $entries) { $bw.Write($e.bytes) }
$bw.Flush(); $bw.Close(); $fs.Close()

# ---- preview sheet: every size on a light and on a dark strip ----
$sheet = New-Object System.Drawing.Bitmap 620, 200
$sh = [System.Drawing.Graphics]::FromImage($sheet)
$sh.Clear([System.Drawing.Color]::FromArgb(255, 247, 244, 238))
$bf = New-Object System.Drawing.Font 'Segoe UI', 9
$sh.DrawString('16 / 24 / 32 / 48 on a light background', $bf, [System.Drawing.Brushes]::DimGray, 14, 10)
$sh.DrawString('64 / 128 / 256', $bf, [System.Drawing.Brushes]::DimGray, 300, 10)
$x = 14
foreach ($n in @(16, 24, 32, 48)) {
    $sh.DrawImage($made[$n], $x, 30, $n, $n)
    $x += $n + 12
}
$sh.DrawImage($made[64], 300, 30, 64, 64)
$sh.DrawImage($made[128], 380, 30, 128, 128)
$sh.DrawImage($made[256], 520, 30, 96, 96)
$sh.DrawString('the same mark on a dark bar (taskbar)', $bf, [System.Drawing.Brushes]::DimGray, 14, 150)
$sh.FillRectangle([System.Drawing.Brushes]::Black, 14, 168, 592, 24)
$x = 320
foreach ($n in @(48, 32, 24, 16)) {
    $sh.DrawImage($made[$n], $x, 168 + [int]((24 - $n) / 2), $n, $n)
    $x += $n + 14
}
$sh.Dispose(); $bf.Dispose()
$sheetPath = Join-Path $outDir 'icon-preview.png'
$sheet.Save($sheetPath, [System.Drawing.Imaging.ImageFormat]::Png)
$sh.Dispose(); $sheet.Dispose()
foreach ($k in $made.Keys) { $made[$k].Dispose() }

Write-Host ("icon-256.png     : {0} B" -f (Get-Item -LiteralPath (Join-Path $outDir 'icon-256.png')).Length)
Write-Host ("icon-preview.png : {0} B" -f (Get-Item -LiteralPath $sheetPath).Length)
Write-Host ("app.ico          : {0} B, {1} entries" -f (Get-Item -LiteralPath $icoPath).Length, $entries.Count)
Write-Host ("next: copy it to launcher\app.ico, then run launcher\build.ps1 and installer\build-installer.ps1")
