param([string]$OutPath, [string]$PreviewPath)
Add-Type -AssemblyName System.Drawing

function RoundRect([float]$x, [float]$y, [float]$w, [float]$h, [float]$r) {
    $p = New-Object System.Drawing.Drawing2D.GraphicsPath
    $d = $r * 2
    $p.AddArc($x, $y, $d, $d, 180, 90)
    $p.AddArc($x + $w - $d, $y, $d, $d, 270, 90)
    $p.AddArc($x + $w - $d, $y + $h - $d, $d, $d, 0, 90)
    $p.AddArc($x, $y + $h - $d, $d, $d, 90, 90)
    $p.CloseFigure()
    return $p
}

function Cassette($g, [float]$x, [float]$y, [float]$w, [float]$h, $body, $edge, [bool]$detail) {
    $path = RoundRect $x $y $w $h ([Math]::Max(1, $w * 0.08))
    $g.FillPath((New-Object System.Drawing.SolidBrush $body), $path)
    $g.DrawPath((New-Object System.Drawing.Pen($edge, [Math]::Max(1, $w / 40))), $path)
    # Label window
    $lw = $w * 0.70; $lh = $h * 0.42
    $lx = $x + ($w - $lw) / 2; $ly = $y + $h * 0.16
    $label = RoundRect $lx $ly $lw $lh ([Math]::Max(1, $w * 0.04))
    $g.FillPath((New-Object System.Drawing.SolidBrush ([System.Drawing.Color]::FromArgb(235, 238, 242))), $label)
    # Reels
    $rr = $lh * 0.30
    foreach ($cx in @(($lx + $lw * 0.27), ($lx + $lw * 0.73))) {
        $cy = $ly + $lh / 2
        $g.FillEllipse((New-Object System.Drawing.SolidBrush ([System.Drawing.Color]::FromArgb(60, 60, 66))), $cx - $rr, $cy - $rr, 2 * $rr, 2 * $rr)
        if ($detail) {
            $hr = $rr * 0.45
            $g.FillEllipse((New-Object System.Drawing.SolidBrush ([System.Drawing.Color]::White)), $cx - $hr, $cy - $hr, 2 * $hr, 2 * $hr)
        }
    }
    if ($detail) {
        # "DV" stripe at the bottom
        $g.FillRectangle((New-Object System.Drawing.SolidBrush ([System.Drawing.Color]::FromArgb(70, 120, 200))), $x + $w * 0.2, $y + $h * 0.70, $w * 0.6, $h * 0.12)
    }
}

function Draw([int]$s) {
    $bmp = New-Object System.Drawing.Bitmap($s, $s, [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.SmoothingMode = 'AntiAlias'
    $g.Clear([System.Drawing.Color]::Transparent)
    $detail = $s -ge 32
    $w = $s * 0.80; $h = $s * 0.56
    if ($s -ge 24) {
        # Second capture behind
        Cassette $g ($s * 0.18) ($s * 0.08) $w $h ([System.Drawing.Color]::FromArgb(150, 160, 175)) ([System.Drawing.Color]::FromArgb(100, 108, 120)) $false
        Cassette $g ($s * 0.02) ($s * 0.30) $w $h ([System.Drawing.Color]::FromArgb(45, 48, 56)) ([System.Drawing.Color]::FromArgb(20, 20, 24)) $detail
    } else {
        Cassette $g 0.5 ($s * 0.16) ($s - 1) ($s * 0.62) ([System.Drawing.Color]::FromArgb(45, 48, 56)) ([System.Drawing.Color]::FromArgb(20, 20, 24)) $false
    }
    # Green check badge
    $br = $s * 0.40
    $bx = $s - $br - [Math]::Max(0, $s * 0.01); $by = $s - $br - [Math]::Max(0, $s * 0.01)
    $g.FillEllipse((New-Object System.Drawing.SolidBrush ([System.Drawing.Color]::FromArgb(40, 150, 60))), $bx, $by, $br, $br)
    $g.DrawEllipse((New-Object System.Drawing.Pen(([System.Drawing.Color]::White), [Math]::Max(1, $s / 32))), $bx, $by, $br, $br)
    $pen = New-Object System.Drawing.Pen(([System.Drawing.Color]::White), [Math]::Max(1.5, $s / 14))
    $pen.StartCap = 'Round'; $pen.EndCap = 'Round'; $pen.LineJoin = 'Round'
    $pts = [System.Drawing.PointF[]]@(
        (New-Object System.Drawing.PointF(($bx + $br * 0.25), ($by + $br * 0.52))),
        (New-Object System.Drawing.PointF(($bx + $br * 0.43), ($by + $br * 0.70))),
        (New-Object System.Drawing.PointF(($bx + $br * 0.76), ($by + $br * 0.32))))
    $g.DrawLines($pen, $pts)
    $g.Dispose()
    return $bmp
}

$sizes = @(16, 24, 32, 48, 64, 256)
$images = @()
foreach ($s in $sizes) {
    $bmp = Draw $s
    $ms = New-Object System.IO.MemoryStream
    if ($s -ge 256) {
        $bmp.Save($ms, [System.Drawing.Imaging.ImageFormat]::Png)
        if ($PreviewPath) { $bmp.Save($PreviewPath, [System.Drawing.Imaging.ImageFormat]::Png) }
    } else {
        # Classic DIB entry: BITMAPINFOHEADER, bottom-up BGRA pixels, then an empty AND mask
        $w = New-Object System.IO.BinaryWriter($ms)
        $maskRow = [int]([Math]::Ceiling($s / 32.0) * 4)
        $w.Write([UInt32]40); $w.Write([Int32]$s); $w.Write([Int32]($s * 2))
        $w.Write([UInt16]1); $w.Write([UInt16]32); $w.Write([UInt32]0)
        $w.Write([UInt32]($s * $s * 4 + $maskRow * $s)); $w.Write([Int32]0); $w.Write([Int32]0); $w.Write([UInt32]0); $w.Write([UInt32]0)
        for ($y = $s - 1; $y -ge 0; $y--) {
            for ($x = 0; $x -lt $s; $x++) {
                $c = $bmp.GetPixel($x, $y)
                $w.Write([byte]$c.B); $w.Write([byte]$c.G); $w.Write([byte]$c.R); $w.Write([byte]$c.A)
            }
        }
        $w.Write((New-Object byte[] ($maskRow * $s)))
        $w.Flush()
    }
    $images += , @($s, $ms.ToArray())
    $bmp.Dispose()
}

# ICO container with PNG-compressed entries (supported since Windows Vista)
$out = New-Object System.IO.MemoryStream
$bw = New-Object System.IO.BinaryWriter($out)
$bw.Write([UInt16]0); $bw.Write([UInt16]1); $bw.Write([UInt16]$images.Count)
$offset = 6 + 16 * $images.Count
foreach ($img in $images) {
    $s = $img[0]; $data = $img[1]
    $dim = if ($s -ge 256) { 0 } else { $s }
    $bw.Write([byte]$dim); $bw.Write([byte]$dim); $bw.Write([byte]0); $bw.Write([byte]0)
    $bw.Write([UInt16]1); $bw.Write([UInt16]32)
    $bw.Write([UInt32]$data.Length); $bw.Write([UInt32]$offset)
    $offset += $data.Length
}
foreach ($img in $images) { $bw.Write([byte[]]$img[1]) }
$bw.Flush()
[System.IO.File]::WriteAllBytes($OutPath, $out.ToArray())
