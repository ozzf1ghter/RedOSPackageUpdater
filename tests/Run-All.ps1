$ErrorActionPreference = 'Stop'
$project = Split-Path -Parent $PSScriptRoot
$compiler = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
if (-not (Test-Path $compiler)) { $compiler = Join-Path $env:WINDIR 'Microsoft.NET\Framework\v4.0.30319\csc.exe' }
if (-not (Test-Path $compiler)) { throw 'Компилятор .NET Framework не найден' }

$temp = Join-Path ([IO.Path]::GetTempPath()) ('rpu-tests-' + [Guid]::NewGuid().ToString('N'))
[IO.Directory]::CreateDirectory($temp) | Out-Null
try {
    $testExe = Join-Path $temp 'ParserTests.exe'
    $references = @(
        'System.dll', 'System.Core.dll', 'System.Drawing.dll', 'System.Windows.Forms.dll',
        'System.Web.Extensions.dll', 'System.Xml.Linq.dll', 'System.Security.dll',
        'System.IO.Compression.dll', 'System.IO.Compression.FileSystem.dll',
        (Join-Path $project 'libs\Renci.SshNet.dll')
    ) | ForEach-Object { '/reference:' + $_ }
    $sources = @((Join-Path $PSScriptRoot 'ParserTests.cs')) +
        [IO.Directory]::GetFiles((Join-Path $project 'src'), '*.cs')
    & $compiler /nologo /target:exe ('/out:' + $testExe) /main:ParserTests $references $sources
    if ($LASTEXITCODE -ne 0) { throw "Сборка логических тестов завершилась с кодом $LASTEXITCODE" }
    & $testExe
    if ($LASTEXITCODE -ne 0) { throw "Логические тесты завершились с кодом $LASTEXITCODE" }
    & (Join-Path $PSScriptRoot 'UiContractTests.ps1')
    if ($LASTEXITCODE -ne 0) { throw "UI-контракты завершились с кодом $LASTEXITCODE" }
}
finally {
    if (Test-Path $temp) { [IO.Directory]::Delete($temp, $true) }
}
