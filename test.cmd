@echo off
setlocal
set CSC=%WINDIR%\Microsoft.NET\Framework64\v4.0.30319\csc.exe
if not exist "%CSC%" set CSC=%WINDIR%\Microsoft.NET\Framework\v4.0.30319\csc.exe
if not exist "%CSC%" exit /b 2
if not exist tests mkdir tests
"%CSC%" /nologo /target:exe /out:tests\ParserTests.exe ^
  /reference:System.dll /reference:System.Core.dll /reference:System.Web.Extensions.dll ^
  /reference:System.Xml.Linq.dll /reference:System.IO.Compression.dll /reference:System.IO.Compression.FileSystem.dll ^
  tests\ParserTests.cs src\Models.cs src\PkgOpOutputParser.cs src\BduFindingEnricher.cs ^
  src\Infrastructure.cs src\ConfigurationRules.cs src\UiLayoutRules.cs src\FstecLinuxCatalog.cs src\VulnerabilityDb.cs src\VulnerabilityReportService.cs src\BuildInfo.cs src\Store.cs src\Crypto.cs
if errorlevel 1 exit /b 1
tests\ParserTests.exe
set RESULT=%ERRORLEVEL%
del tests\ParserTests.exe >nul 2>&1
if not "%RESULT%"=="0" exit /b %RESULT%

powershell.exe -NoLogo -NoProfile -ExecutionPolicy Bypass -File tests\UiContractTests.ps1
if errorlevel 1 exit /b 1

set BASH_EXE=C:\Program Files\Git\bin\bash.exe
if exist "%BASH_EXE%" (
  for %%S in (profiles\*.sh) do (
    "%BASH_EXE%" -n "%%S"
    if errorlevel 1 exit /b 1
  )
  echo OK   синтаксис shell-профилей
)
exit /b %RESULT%
