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

# Удаляет только светлый нейтральный фон, связанный с краями изображения. Белые элементы
# монограммы находятся внутри синей плитки и до краёв не связаны, поэтому сохраняются.
function Clear-ConnectedBoardBackground([Drawing.Bitmap]$bitmap) {
    $w=$bitmap.Width;$h=$bitmap.Height;$seen=New-Object bool[] ($w*$h)
    $queue=New-Object 'Collections.Generic.Queue[int]'
    function Is-BoardPixel([Drawing.Color]$c) {
        $max=[Math]::Max($c.R,[Math]::Max($c.G,$c.B));$min=[Math]::Min($c.R,[Math]::Min($c.G,$c.B))
        return $min-ge 180 -and ($max-$min)-le 38
    }
    for($x=0;$x-lt $w;$x++){$queue.Enqueue($x);$queue.Enqueue(($h-1)*$w+$x)}
    for($y=1;$y-lt $h-1;$y++){$queue.Enqueue($y*$w);$queue.Enqueue($y*$w+$w-1)}
    while($queue.Count-gt 0){$i=$queue.Dequeue();if($seen[$i]){continue};$seen[$i]=$true;$x=$i%$w;$y=[Math]::Floor($i/$w)
        if(-not(Is-BoardPixel $bitmap.GetPixel($x,$y))){continue}
        $bitmap.SetPixel($x,$y,[Drawing.Color]::FromArgb(0,0,0,0))
        if($x-gt 0){$queue.Enqueue($i-1)};if($x-lt $w-1){$queue.Enqueue($i+1)};if($y-gt 0){$queue.Enqueue($i-$w)};if($y-lt $h-1){$queue.Enqueue($i+$w)}
    }
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
            Clear-ConnectedBoardBackground $bitmap
            $path=Join-Path $assetDir ("app-icon-{0}.png" -f $size)
            $bitmap.Save($path,[Drawing.Imaging.ImageFormat]::Png); $pngs+=@{Size=$size;Path=$path}
        } finally {$g.Dispose();$bitmap.Dispose()}
    }
} finally {$source.Dispose()}

$ico=Join-Path $Project 'icon.ico'
# Win32 resource compiler из .NET Framework 4.x получает классический 24-bit DIB + AND-mask.
# GetHicon/Icon.Save здесь использовать нельзя: на классической теме он теряет alpha,
# превращает синий в бирюзовый и показывает RGB прозрачных углов как белые стрелки.
$systemBitmap=[Drawing.Bitmap]::FromFile((Join-Path $assetDir 'app-icon-32.png'))
$systemStream=[IO.File]::Open($ico,[IO.FileMode]::Create)
$systemWriter=New-Object IO.BinaryWriter $systemStream
try {
    $w=32;$h=32;$xorStride=96;$maskStride=4;$payloadSize=40+$xorStride*$h+$maskStride*$h
    $systemWriter.Write([uint16]0);$systemWriter.Write([uint16]1);$systemWriter.Write([uint16]1)
    $systemWriter.Write([byte]$w);$systemWriter.Write([byte]$h);$systemWriter.Write([byte]0);$systemWriter.Write([byte]0)
    $systemWriter.Write([uint16]1);$systemWriter.Write([uint16]24);$systemWriter.Write([uint32]$payloadSize);$systemWriter.Write([uint32]22)
    $systemWriter.Write([uint32]40);$systemWriter.Write([int32]$w);$systemWriter.Write([int32]($h*2))
    $systemWriter.Write([uint16]1);$systemWriter.Write([uint16]24);$systemWriter.Write([uint32]0)
    $systemWriter.Write([uint32]($xorStride*$h));$systemWriter.Write([int32]0);$systemWriter.Write([int32]0)
    $systemWriter.Write([uint32]0);$systemWriter.Write([uint32]0)
    for($y=$h-1;$y-ge 0;$y--){for($x=0;$x-lt $w;$x++){$c=$systemBitmap.GetPixel($x,$y);$systemWriter.Write([byte]$c.B);$systemWriter.Write([byte]$c.G);$systemWriter.Write([byte]$c.R)}}
    for($y=$h-1;$y-ge 0;$y--){$mask=New-Object byte[] $maskStride;for($x=0;$x-lt $w;$x++){if($systemBitmap.GetPixel($x,$y).A-lt 128){$byteIndex=[Math]::Floor($x/8);$mask[$byteIndex]=$mask[$byteIndex]-bor(0x80-shr($x%8))}};$systemWriter.Write($mask)}
} finally {$systemWriter.Dispose();$systemStream.Dispose();$systemBitmap.Dispose()}
Write-Host "Extracted approved optical icon sizes: $($pngs.Size -join ', ') px"
