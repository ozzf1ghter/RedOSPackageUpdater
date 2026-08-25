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

# ICO с PNG-кадрами; Windows выбирает готовый оптический размер самостоятельно.
$ico=Join-Path $Project 'icon.ico'; $images=@($pngs|ForEach-Object{[IO.File]::ReadAllBytes($_.Path)})
$stream=[IO.File]::Open($ico,[IO.FileMode]::Create);$writer=New-Object IO.BinaryWriter $stream
try {
    $writer.Write([uint16]0);$writer.Write([uint16]1);$writer.Write([uint16]$pngs.Count);$offset=6+16*$pngs.Count
    for($i=0;$i -lt $pngs.Count;$i++){$v=$pngs[$i].Size;$writer.Write([byte]$(if($v-eq 256){0}else{$v}));$writer.Write([byte]$(if($v-eq 256){0}else{$v}));$writer.Write([byte]0);$writer.Write([byte]0);$writer.Write([uint16]1);$writer.Write([uint16]32);$writer.Write([uint32]$images[$i].Length);$writer.Write([uint32]$offset);$offset+=$images[$i].Length}
    foreach($bytes in $images){$writer.Write($bytes)}
} finally {$writer.Dispose();$stream.Dispose()}
Write-Host "Extracted approved optical icon sizes: $($pngs.Size -join ', ') px"
