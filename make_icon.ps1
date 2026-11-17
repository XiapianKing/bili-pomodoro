# Generate tomato ICO (pure DIB entries, old-compiler friendly). ASCII only.
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing

function New-Tomato([int]$n) {
    $bmp = New-Object System.Drawing.Bitmap($n, $n)
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.SmoothingMode = 'AntiAlias'
    $s = $n / 256.0

    $body = New-Object System.Drawing.SolidBrush([System.Drawing.Color]::FromArgb(255,226,74,56))
    $g.FillEllipse($body, [float](30*$s), [float](80*$s), [float](196*$s), [float](156*$s))
    $dark = New-Object System.Drawing.SolidBrush([System.Drawing.Color]::FromArgb(70,140,30,20))
    $g.FillEllipse($dark, [float](50*$s), [float](150*$s), [float](156*$s), [float](80*$s))
    $leaf = New-Object System.Drawing.SolidBrush([System.Drawing.Color]::FromArgb(255,110,168,70))
    $g.TranslateTransform([float](128*$s), [float](82*$s)); $g.RotateTransform([float]-20)
    $g.FillEllipse($leaf, [float](-42*$s), [float](-14*$s), [float](84*$s), [float](28*$s))
    $g.RotateTransform([float]40)
    $g.FillEllipse($leaf, [float](-42*$s), [float](-14*$s), [float](84*$s), [float](28*$s))
    $g.ResetTransform()
    $stem = New-Object System.Drawing.SolidBrush([System.Drawing.Color]::FromArgb(255,86,134,56))
    $g.FillEllipse($stem, [float](120*$s), [float](44*$s), [float](14*$s), [float](34*$s))
    $hi = New-Object System.Drawing.SolidBrush([System.Drawing.Color]::FromArgb(95,255,255,255))
    $g.FillEllipse($hi, [float](62*$s), [float](98*$s), [float](52*$s), [float](34*$s))

    $g.Dispose()
    return $bmp
}

function Get-DibBytes($bmp, [int]$n) {
    $ms = New-Object System.IO.MemoryStream
    $bw = New-Object System.IO.BinaryWriter($ms)
    $bw.Write([int]40); $bw.Write([int]$n); $bw.Write([int]($n*2))
    $bw.Write([uint16]1); $bw.Write([uint16]32)
    $bw.Write([int]0); $bw.Write([int]($n*$n*4)); $bw.Write([int]0); $bw.Write([int]0); $bw.Write([int]0); $bw.Write([int]0)
    for ($y = $n-1; $y -ge 0; $y--) {
        for ($x = 0; $x -lt $n; $x++) {
            $c = $bmp.GetPixel($x, $y)
            $bw.Write([byte]$c.B); $bw.Write([byte]$c.G); $bw.Write([byte]$c.R); $bw.Write([byte]$c.A)
        }
    }
    $maskRow = [int][Math]::Ceiling($n/32.0)*4
    for ($i = 0; $i -lt $maskRow*$n; $i++) { $bw.Write([byte]0) }
    $bw.Flush()
    return ,$ms.ToArray()
}

$sizes = @(16,32,48,256)
$imgs = @()
foreach ($n in $sizes) {
    $b = New-Tomato $n
    $imgs += ,(Get-DibBytes $b $n)
    $b.Dispose()
}

$out = Join-Path $PSScriptRoot 'app.ico'
$fs = [System.IO.File]::Create($out)
$bw = New-Object System.IO.BinaryWriter($fs)
$bw.Write([uint16]0); $bw.Write([uint16]1); $bw.Write([uint16]$sizes.Count)
$offset = 6 + 16*$sizes.Count
for ($i = 0; $i -lt $sizes.Count; $i++) {
    $n = $sizes[$i]
    $w = if ($n -ge 256) { 0 } else { $n }
    $bw.Write([byte]$w); $bw.Write([byte]$w); $bw.Write([byte]0); $bw.Write([byte]0)
    $bw.Write([uint16]1); $bw.Write([uint16]32)
    $bw.Write([uint32]$imgs[$i].Length); $bw.Write([uint32]$offset)
    $offset += $imgs[$i].Length
}
foreach ($img in $imgs) { $bw.Write($img) }
$bw.Flush(); $bw.Close()
$len = (Get-Item $out).Length
Write-Host "icon OK -> $out ($len bytes)"
