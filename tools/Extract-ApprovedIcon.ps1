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

# Строит альфа-канал плитки заново. Это удаляет не только белый фон макета, но и
# светлый RGB-ореол, появившийся при сглаживании синей плитки на белой подложке.
function Apply-CleanRoundedTileMask([Drawing.Bitmap]$bitmap) {
    $w=$bitmap.Width;$h=$bitmap.Height;$scale=8;$mw=$w*$scale;$mh=$h*$scale
    $large=New-Object Drawing.Bitmap -ArgumentList $mw,$mh,([Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $mask=New-Object Drawing.Bitmap -ArgumentList $w,$h,([Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $g=[Drawing.Graphics]::FromImage($large)
    try {
        $g.Clear([Drawing.Color]::Transparent);$g.SmoothingMode=[Drawing.Drawing2D.SmoothingMode]::AntiAlias
        $radius=[float]($w*0.215*$scale);$diameter=$radius*2;$right=$mw-1;$bottom=$mh-1
        $path=New-Object Drawing.Drawing2D.GraphicsPath
        try {
            $path.AddArc(0,0,$diameter,$diameter,180,90);$path.AddArc($right-$diameter,0,$diameter,$diameter,270,90)
            $path.AddArc($right-$diameter,$bottom-$diameter,$diameter,$diameter,0,90);$path.AddArc(0,$bottom-$diameter,$diameter,$diameter,90,90);$path.CloseFigure()
            $brush=New-Object Drawing.SolidBrush ([Drawing.Color]::White);try{$g.FillPath($brush,$path)}finally{$brush.Dispose()}
        } finally {$path.Dispose()}
    } finally {$g.Dispose()}
    $mg=[Drawing.Graphics]::FromImage($mask)
    try {$mg.InterpolationMode=[Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic;$mg.DrawImage($large,0,0,$w,$h)} finally {$mg.Dispose();$large.Dispose()}
    try {
        for($y=0;$y-lt $h;$y++){for($x=0;$x-lt $w;$x++){
            $coverage=$mask.GetPixel($x,$y).A;$c=$bitmap.GetPixel($x,$y)
            if($coverage-eq 0){$bitmap.SetPixel($x,$y,[Drawing.Color]::FromArgb(0,0,0,0));continue}
            $position=if($h-le 1){0}else{$y/($h-1.0)}
            $baseR=[Math]::Round(30+(10-30)*$position);$baseG=[Math]::Round(101+(67-101)*$position);$baseB=[Math]::Round(222+(181-222)*$position)
            $edgeDistance=[Math]::Min([Math]::Min($x,$w-1-$x),[Math]::Min($y,$h-1-$y))
            $logo=if($edgeDistance-ge [Math]::Max(1,[Math]::Round($w*0.065))){[Math]::Max(0,[Math]::Min(1,($c.R-42)/190.0))}else{0}
            $r=[Math]::Round($baseR+(255-$baseR)*$logo)
            $green=[Math]::Round($baseG+(255-$baseG)*$logo)
            $b=[Math]::Round($baseB+(255-$baseB)*$logo)
            $bitmap.SetPixel($x,$y,[Drawing.Color]::FromArgb($coverage,$r,$green,$b))
        }}
    } finally {$mask.Dispose()}
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
            Apply-CleanRoundedTileMask $bitmap
            $path=Join-Path $assetDir ("app-icon-{0}.png" -f $size)
            $bitmap.Save($path,[Drawing.Imaging.ImageFormat]::Png); $pngs+=@{Size=$size;Path=$path}
        } finally {$g.Dispose();$bitmap.Dispose()}
    }
} finally {$source.Dispose()}

# Малые кадры строятся из уже очищенного мастер-значка. Отдельные миниатюры макета
# слишком малы для надёжного отделения белой монограммы от светлого фона.
$master=[Drawing.Bitmap]::FromFile((Join-Path $assetDir 'app-icon-256.png'))
try {
    foreach($item in $pngs|Where-Object{$_.Size-lt 256}) {
        $size=[int]$item.Size;$small=New-Object Drawing.Bitmap -ArgumentList $size,$size,([Drawing.Imaging.PixelFormat]::Format32bppArgb)
        $sg=[Drawing.Graphics]::FromImage($small)
        try {
            $sg.Clear([Drawing.Color]::Transparent);$sg.CompositingMode=[Drawing.Drawing2D.CompositingMode]::SourceCopy
            $sg.CompositingQuality=[Drawing.Drawing2D.CompositingQuality]::HighQuality;$sg.InterpolationMode=[Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
            $sg.PixelOffsetMode=[Drawing.Drawing2D.PixelOffsetMode]::HighQuality;$sg.DrawImage($master,0,0,$size,$size)
            $small.Save($item.Path,[Drawing.Imaging.ImageFormat]::Png)
        } finally {$sg.Dispose();$small.Dispose()}
    }
} finally {$master.Dispose()}

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
