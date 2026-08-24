param(
    [Parameter(Mandatory = $true)]
    [ValidatePattern('^\d+\.\d+\.\d+$')]
    [string]$Version,
    [string]$Notes = ''
)

$ErrorActionPreference = 'Stop'
$projectDir = Split-Path -Parent $MyInvocation.MyCommand.Path
$buildInfoPath = Join-Path $projectDir 'src\BuildInfo.cs'
$manifestPath = Join-Path $projectDir 'update.json'
$exePath = Join-Path $projectDir 'RedOSPackageUpdater.exe'

$source = [IO.File]::ReadAllText($buildInfoPath)
$source = [Regex]::Replace($source, 'Version = "\d+\.\d+\.\d+"', 'Version = "' + $Version + '"', 1)
$source = [Regex]::Replace($source, 'AssemblyVersion = "\d+\.\d+\.\d+\.0"', 'AssemblyVersion = "' + $Version + '.0"', 1)
[IO.File]::WriteAllText($buildInfoPath, $source, [Text.UTF8Encoding]::new($false))

$manifest = Get-Content $manifestPath -Raw | ConvertFrom-Json
$manifest.version = $Version
if ($Notes) { $manifest.notes = $Notes }

Push-Location $projectDir
try {
    & cmd.exe /c build.cmd
    if ($LASTEXITCODE -ne 0) { throw "Сборка завершилась с кодом $LASTEXITCODE" }
    $manifest.sha256 = (Get-FileHash $exePath -Algorithm SHA256).Hash.ToLowerInvariant()
    $json = $manifest | ConvertTo-Json
    [IO.File]::WriteAllText($manifestPath, $json + [Environment]::NewLine, [Text.UTF8Encoding]::new($false))
    Write-Host "Релиз $Version собран. SHA-256: $($manifest.sha256)"
}
finally { Pop-Location }
