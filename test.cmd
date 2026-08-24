@echo off
setlocal
set CSC=%WINDIR%\Microsoft.NET\Framework64\v4.0.30319\csc.exe
if not exist "%CSC%" set CSC=%WINDIR%\Microsoft.NET\Framework\v4.0.30319\csc.exe
if not exist "%CSC%" exit /b 2
if not exist tests mkdir tests
"%CSC%" /nologo /target:exe /out:tests\ParserTests.exe ^
  /reference:System.dll /reference:System.Web.Extensions.dll ^
  tests\ParserTests.cs src\Models.cs src\PkgOpOutputParser.cs src\BduFindingEnricher.cs
if errorlevel 1 exit /b 1
tests\ParserTests.exe
set RESULT=%ERRORLEVEL%
del tests\ParserTests.exe >nul 2>&1
exit /b %RESULT%
