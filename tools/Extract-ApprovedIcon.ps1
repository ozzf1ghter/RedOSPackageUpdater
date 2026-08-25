param(
    [string]$Board = 'C:\Users\VASILI~1\AppData\Local\Temp\codex-clipboard-8ab1133e-0640-4bec-852a-c903e4db25e9.png',
    [string]$Project = (Split-Path -Parent $PSScriptRoot)
)
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing
$source = [Drawing.Bitmap]::FromFile($Board)
$assetDir = Join-Path $Project 'assets\app-icon'
[IO.Directory]::CreateDirectory($assetDir) | Out-Null

# В макете варианты уже отрисованы отдельно. Области намеренно широкие;
# внутри каждой автоматически ищется точная граница синей плитки.
$regions = @(
    @{Size=256; X=20;  Y=130; W=205; H=210},
    @{Size=64;  X=225; Y=175; W=105; H=115},
    @{Size=48;  X=325; Y=190; W=90;  H=90},
    @{Size=32;  X=410; Y=200; W=70;  H=75},
    @{Size=24;  X=480; Y=205; W=65;  H=65},
    @{Size=20;  X=545; Y=210; W=60;  H=60},
    @{Size=16;  X=610; Y=215; W=55;  H=55}
)

function Find-TileBounds($region) {
    $minX=$source.Width; $minY=$source.Height; $maxX=-1; $maxY=-1
    for($y=$region.Y;$y -lt [Math]::Min($source.Height,$region.Y+$region.H);$y++) {
        for($x=$region.X;$x -lt [Math]::Min($source.Width,$region.X+$region.W);$x++) {
            $c=$source.GetPixel($x,$y)
            if($c.B -gt 125 -and $c.B -gt ($c.R+55) -and $c.B -gt ($c.G+25)) {
                if($x -lt $minX){$minX=$x}; if($x -gt $maxX){$maxX=$x}; if($y -lt $minY){$minY=$y}; if($y -gt $maxY){$maxY=$y}
            }
        }
    }
    if($maxX -lt $minX){throw "Blue icon tile was not found for $($region.Size)px"}
    New-Object Drawing.Rectangle $minX,$minY,($maxX-$minX+1),($maxY-$minY+1)
}

$pngs=@()
try {
    foreach($region in $regions) {
        $bounds=Find-TileBounds $region
        $size=[int]$region.Size
        $bitmap=New-Object Drawing.Bitmap -ArgumentList $size,$size,([Drawing.Imaging.PixelFormat]::Format32bppArgb)
        $g=[Drawing.Graphics]::FromImage($bitmap)
        try {
            $g.Clear([Drawing.Color]::Transparent)
            $g.InterpolationMode = if($size -le 24){[Drawing.Drawing2D.InterpolationMode]::NearestNeighbor}else{[Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic}
            $g.PixelOffsetMode=[Drawing.Drawing2D.PixelOffsetMode]::HighQuality
            $g.DrawImage($source,(New-Object Drawing.Rectangle 0,0,$size,$size),$bounds,[Drawing.GraphicsUnit]::Pixel)
            $path=Join-Path $assetDir ("app-icon-{0}.png" -f $size)
            $bitmap.Save($path,[Drawing.Imaging.ImageFormat]::Png); $pngs+=@{Size=$size;Path=$path}
        } finally {$g.Dispose();$bitmap.Dispose()}
    }
} finally {$source.Dispose()}

# ICO с классическими DIB-кадрами. PNG внутри ICO не поддерживается старыми оболочками
# Windows Server/классической панелью задач: они показывают стандартную иконку WinForms.
function Convert-PngToIconDib($item) {
    $bitmap=[Drawing.Bitmap]::FromFile($item.Path)
    $memory=New-Object IO.MemoryStream
    $dibWriter=New-Object IO.BinaryWriter $memory
    try {
        $width=[int]$item.Size; $height=[int]$item.Size
        $xorSize=$width*$height*4
        $maskStride=[int]([Math]::Ceiling($width/32.0)*4)
        $dibWriter.Write([uint32]40); $dibWriter.Write([int32]$width); $dibWriter.Write([int32]($height*2))
        $dibWriter.Write([uint16]1); $dibWriter.Write([uint16]32); $dibWriter.Write([uint32]0)
        $dibWriter.Write([uint32]$xorSize); $dibWriter.Write([int32]0); $dibWriter.Write([int32]0)
        $dibWriter.Write([uint32]0); $dibWriter.Write([uint32]0)
        for($y=$height-1;$y-ge 0;$y--){for($x=0;$x-lt $width;$x++){
            $c=$bitmap.GetPixel($x,$y); $dibWriter.Write([byte]$c.B); $dibWriter.Write([byte]$c.G)
            $dibWriter.Write([byte]$c.R); $dibWriter.Write([byte]$c.A)
        }}
        for($y=$height-1;$y-ge 0;$y--){
            $row=New-Object byte[] $maskStride
            for($x=0;$x-lt $width;$x++){if($bitmap.GetPixel($x,$y).A-lt 128){$row[[int]($x/8)] = $row[[int]($x/8)] -bor (0x80-shr($x%8))}}
            $dibWriter.Write($row)
        }
        $dibWriter.Flush(); return $memory.ToArray()
    } finally {$dibWriter.Dispose();$memory.Dispose();$bitmap.Dispose()}
}

$ico=Join-Path $Project 'icon.ico'
# .NET Framework csc использует старый Win32 resource compiler и отвергает 256px DIB.
# Большой вариант остаётся отдельным PNG-ресурсом для About/UI; системному значку достаточно 16-64px.
$icoFrames=@($pngs|Where-Object{$_.Size-lt 256})
# Унарная запятая запрещает PowerShell разворачивать byte[] каждого кадра в поток отдельных
# byte. Без неё таблица ICO получала размер кадра 1 байт, и csc молча ставил стандартный значок.
$images=@($icoFrames|ForEach-Object{ ,(Convert-PngToIconDib $_) })
$stream=[IO.File]::Open($ico,[IO.FileMode]::Create);$writer=New-Object IO.BinaryWriter $stream
try {
    $writer.Write([uint16]0);$writer.Write([uint16]1);$writer.Write([uint16]$icoFrames.Count);$offset=6+16*$icoFrames.Count
    for($i=0;$i -lt $icoFrames.Count;$i++){$v=$icoFrames[$i].Size;$writer.Write([byte]$v);$writer.Write([byte]$v);$writer.Write([byte]0);$writer.Write([byte]0);$writer.Write([uint16]1);$writer.Write([uint16]32);$writer.Write([uint32]$images[$i].Length);$writer.Write([uint32]$offset);$offset+=$images[$i].Length}
    foreach($bytes in $images){$writer.Write($bytes)}
} finally {$writer.Dispose();$stream.Dispose()}

# Win32 resource compiler из .NET Framework 4.x не принимает современный многокадровый
# 32-bit ICO стабильно. Системный ресурс создаём через HICON самой Windows; остальные
# оптические размеры приложение продолжает брать из встроенных PNG.
if(-not ('NativeIconMethods' -as [type])) {
    Add-Type 'using System; using System.Runtime.InteropServices; public static class NativeIconMethods { [DllImport("user32.dll")] public static extern bool DestroyIcon(IntPtr hIcon); }'
}
$systemBitmap=[Drawing.Bitmap]::FromFile((Join-Path $assetDir 'app-icon-32.png'))
$iconHandle=$systemBitmap.GetHicon()
try {
    $systemIcon=[Drawing.Icon]::FromHandle($iconHandle)
    $systemStream=[IO.File]::Open($ico,[IO.FileMode]::Create)
    try {$systemIcon.Save($systemStream)} finally {$systemStream.Dispose();$systemIcon.Dispose()}
} finally {[NativeIconMethods]::DestroyIcon($iconHandle)|Out-Null;$systemBitmap.Dispose()}
Write-Host "Extracted approved optical icon sizes: $($pngs.Size -join ', ') px"
