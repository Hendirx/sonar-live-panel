# Draws app.ico (three amber fader caps on a dark tile) at 16-256 px, stored as PNG frames.
# Only needed if you want to change the icon; app.ico is already in the folder.
Add-Type -AssemblyName System.Drawing

function New-Frame([int]$size) {
    $bmp = New-Object Drawing.Bitmap $size, $size
    $g = [Drawing.Graphics]::FromImage($bmp)
    $g.SmoothingMode = 'AntiAlias'
    $g.Clear([Drawing.Color]::Transparent)

    $r = [Math]::Max(2, $size * 0.2)
    $path = New-Object Drawing.Drawing2D.GraphicsPath
    $path.AddArc(0, 0, $r * 2, $r * 2, 180, 90)
    $path.AddArc($size - $r * 2 - 1, 0, $r * 2, $r * 2, 270, 90)
    $path.AddArc($size - $r * 2 - 1, $size - $r * 2 - 1, $r * 2, $r * 2, 0, 90)
    $path.AddArc(0, $size - $r * 2 - 1, $r * 2, $r * 2, 90, 90)
    $path.CloseFigure()
    $g.FillPath((New-Object Drawing.SolidBrush ([Drawing.Color]::FromArgb(17, 19, 22))), $path)

    $slot = New-Object Drawing.Pen ([Drawing.Color]::FromArgb(70, 76, 86)), ([Math]::Max(1, $size * 0.06))
    $cap = New-Object Drawing.SolidBrush ([Drawing.Color]::FromArgb(240, 168, 48))
    $top = $size * 0.2; $bottom = $size * 0.8
    $levels = 0.35, 0.7, 0.5
    for ($i = 0; $i -lt 3; $i++) {
        $x = $size * (0.28 + $i * 0.22)
        $g.DrawLine($slot, $x, $top, $x, $bottom)
        $y = $bottom - ($bottom - $top) * $levels[$i]
        $w = $size * 0.16; $h = [Math]::Max(2, $size * 0.09)
        $g.FillRectangle($cap, $x - $w / 2, $y - $h / 2, $w, $h)
    }
    $g.Dispose()
    $ms = New-Object IO.MemoryStream
    if ($size -ge 256) {
        $bmp.Save($ms, [Drawing.Imaging.ImageFormat]::Png)   # large frame: PNG, as Windows expects
    } else {
        # Small frames: classic 32-bit DIB (bottom-up BGRA + empty AND mask) so every icon reader can load them.
        $bw = New-Object IO.BinaryWriter $ms
        $bw.Write([UInt32]40); $bw.Write([Int32]$size); $bw.Write([Int32]($size * 2))
        $bw.Write([UInt16]1); $bw.Write([UInt16]32); $bw.Write([UInt32]0)
        $bw.Write([UInt32]0); $bw.Write([Int32]0); $bw.Write([Int32]0); $bw.Write([UInt32]0); $bw.Write([UInt32]0)
        for ($y = $size - 1; $y -ge 0; $y--) {
            for ($x = 0; $x -lt $size; $x++) {
                $c = $bmp.GetPixel($x, $y)
                $bw.Write([byte]$c.B); $bw.Write([byte]$c.G); $bw.Write([byte]$c.R); $bw.Write([byte]$c.A)
            }
        }
        $maskRow = [int]([Math]::Ceiling($size / 32.0) * 4)
        $bw.Write((New-Object byte[] ($maskRow * $size)))
    }
    $bmp.Dispose()
    return ,$ms.ToArray()
}

$sizes = 16, 24, 32, 48, 64, 256
$frames = @(); foreach ($s in $sizes) { $frames += ,(New-Frame $s) }

$out = New-Object IO.MemoryStream
$w = New-Object IO.BinaryWriter $out
$w.Write([UInt16]0); $w.Write([UInt16]1); $w.Write([UInt16]$sizes.Count)
$offset = 6 + 16 * $sizes.Count
for ($i = 0; $i -lt $sizes.Count; $i++) {
    $s = $sizes[$i]; $len = $frames[$i].Length
    $w.Write([byte]($(if ($s -ge 256) { 0 } else { $s })))
    $w.Write([byte]($(if ($s -ge 256) { 0 } else { $s })))
    $w.Write([byte]0); $w.Write([byte]0)
    $w.Write([UInt16]1); $w.Write([UInt16]32)
    $w.Write([UInt32]$len); $w.Write([UInt32]$offset)
    $offset += $len
}
foreach ($f in $frames) { $w.Write($f) }
[IO.File]::WriteAllBytes((Join-Path $PSScriptRoot 'app.ico'), $out.ToArray())
'Wrote app.ico'
