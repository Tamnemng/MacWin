# Generates MacWin\Assets\MacWin.ico (original artwork: gradient rounded tile with a four-pane mark).
Add-Type -AssemblyName System.Drawing
$ErrorActionPreference = 'Stop'
$outDir = Join-Path $PSScriptRoot '..\MacWin\Assets'
New-Item -ItemType Directory -Force $outDir | Out-Null

function New-RoundedPath([float]$x, [float]$y, [float]$w, [float]$h, [float]$r) {
    $p = New-Object System.Drawing.Drawing2D.GraphicsPath
    $d = $r * 2
    $p.AddArc($x, $y, $d, $d, 180, 90)
    $p.AddArc($x + $w - $d, $y, $d, $d, 270, 90)
    $p.AddArc($x + $w - $d, $y + $h - $d, $d, $d, 0, 90)
    $p.AddArc($x, $y + $h - $d, $d, $d, 90, 90)
    $p.CloseFigure()
    return $p
}

function New-IconPng([int]$size) {
    $bmp = New-Object System.Drawing.Bitmap $size, $size, ([System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.SmoothingMode = 'AntiAlias'
    $g.InterpolationMode = 'HighQualityBicubic'
    $s = [float]$size
    $m = $s * 0.06
    $tile = New-RoundedPath $m $m ($s - 2 * $m) ($s - 2 * $m) ($s * 0.22)
    $rect = New-Object System.Drawing.RectangleF $m, $m, ($s - 2 * $m), ($s - 2 * $m)
    $brush = New-Object System.Drawing.Drawing2D.LinearGradientBrush $rect, ([System.Drawing.Color]::FromArgb(255, 94, 178, 255)), ([System.Drawing.Color]::FromArgb(255, 112, 72, 232)), 90
    $g.FillPath($brush, $tile)
    # four-pane mark
    $penW = [Math]::Max(1.5, $s * 0.065)
    $pen = New-Object System.Drawing.Pen ([System.Drawing.Color]::White), $penW
    $inner = $s * 0.27
    $mark = New-RoundedPath $inner $inner ($s - 2 * $inner) ($s - 2 * $inner) ($s * 0.1)
    $g.DrawPath($pen, $mark)
    $g.DrawLine($pen, $s / 2, $inner, $s / 2, $s - $inner)
    $g.DrawLine($pen, $inner, $s / 2, $s - $inner, $s / 2)
    $g.Dispose()
    $ms = New-Object System.IO.MemoryStream
    $bmp.Save($ms, [System.Drawing.Imaging.ImageFormat]::Png)
    $bmp.Dispose()
    return ,$ms.ToArray()
}

$sizes = 16, 24, 32, 48, 64, 128, 256
$images = @($sizes | ForEach-Object { ,(New-IconPng $_) })

$out = New-Object System.IO.MemoryStream
$w = New-Object System.IO.BinaryWriter $out
$w.Write([uint16]0); $w.Write([uint16]1); $w.Write([uint16]$sizes.Count)
$offset = 6 + 16 * $sizes.Count
for ($i = 0; $i -lt $sizes.Count; $i++) {
    $sz = $sizes[$i]; $data = $images[$i]
    $b = if ($sz -ge 256) { 0 } else { $sz }
    $w.Write([byte]$b); $w.Write([byte]$b); $w.Write([byte]0); $w.Write([byte]0)
    $w.Write([uint16]1); $w.Write([uint16]32)
    $w.Write([uint32]$data.Length); $w.Write([uint32]$offset)
    $offset += $data.Length
}
foreach ($data in $images) { $w.Write($data) }
$w.Flush()
[System.IO.File]::WriteAllBytes((Join-Path $outDir 'MacWin.ico'), $out.ToArray())
Write-Output "Icon written to $outDir\MacWin.ico"
