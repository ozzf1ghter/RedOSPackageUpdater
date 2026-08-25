param([string]$Path = (Join-Path (Split-Path -Parent $PSScriptRoot) 'data\linux-bdu.zip'))
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.IO.Compression.FileSystem
$resolved = [IO.Path]::GetFullPath($Path)
$archive = [IO.Compression.ZipFile]::OpenRead($resolved)
try {
    $entry = $archive.Entries | Where-Object { $_.Name -like '*.json' } | Select-Object -First 1
    if ($null -eq $entry) { throw 'JSON catalog is missing from the archive' }
    $reader = [IO.StreamReader]::new($entry.Open(), [Text.Encoding]::UTF8)
    try { $records = $reader.ReadToEnd() | ConvertFrom-Json } finally { $reader.Dispose() }
} finally { $archive.Dispose() }

$filtered = @($records | Where-Object {
    (@($_.Versions).Count -gt 0) -or (@($_.RedOsVersions).Count -gt 0)
})
if ($filtered.Count -lt 100) { throw "Only $($filtered.Count) records remain after filtering" }

$temporary = $resolved + '.tmp'
if (Test-Path -LiteralPath $temporary) { throw "Temporary file already exists: $temporary" }
$json = $filtered | ConvertTo-Json -Depth 8 -Compress
$stream = [IO.File]::Open($temporary, [IO.FileMode]::CreateNew, [IO.FileAccess]::ReadWrite, [IO.FileShare]::None)
try {
    $output = [IO.Compression.ZipArchive]::new($stream, [IO.Compression.ZipArchiveMode]::Create, $true)
    try {
        $newEntry = $output.CreateEntry('linux-bdu.json', [IO.Compression.CompressionLevel]::Optimal)
        $writer = [IO.StreamWriter]::new($newEntry.Open(), [Text.UTF8Encoding]::new($false))
        try { $writer.Write($json) } finally { $writer.Dispose() }
    } finally { $output.Dispose() }
} finally { $stream.Dispose() }
$backup = $resolved + '.compact-backup'
if (Test-Path -LiteralPath $backup) { throw "Backup file already exists: $backup" }
[IO.File]::Replace($temporary, $resolved, $backup)
[IO.File]::Delete($backup)
Write-Host "Catalog compacted: $($records.Count) -> $($filtered.Count) records"
