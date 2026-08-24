@echo off
setlocal enabledelayedexpansion
REM ============================================================
REM  Build RedOSPackageUpdater.exe as a single file (csc.exe, .NET Framework 4.x)
REM  Usage:
REM     build.cmd          - empty exe (for any customer, no preset)
REM     build.cmd seed     - embed preset from seed\seed_config.json
REM  Requires: libs\ with Renci.SshNet.dll and deps, folders src\ and profiles\
REM ============================================================

set NAME=RedOSPackageUpdater

set CSC=%WINDIR%\Microsoft.NET\Framework64\v4.0.30319\csc.exe
if not exist "%CSC%" set CSC=%WINDIR%\Microsoft.NET\Framework\v4.0.30319\csc.exe
if not exist "%CSC%" (
  echo [ERROR] csc.exe .NET Framework 4.x not found. Install .NET Framework 4.6.2+.
  exit /b 1
)
echo Compiler: %CSC%

if not exist libs\Renci.SshNet.dll (
  echo [ERROR] libs\Renci.SshNet.dll missing. Put SSH.NET and deps into libs\.
  exit /b 1
)

REM --- resources: all dll from libs ---
set RES=
for %%D in (libs\*.dll) do set RES=!RES! /resource:"%%D","%%~nxD"

REM --- resources: profiles ---
for %%P in (profiles\*.sh) do set RES=!RES! /resource:"%%P","%%~nxP"

REM --- icon (exe file + window) ---
set ICON=
if exist icon.ico (
  set ICON=/win32icon:icon.ico
  set RES=!RES! /resource:"icon.ico","app.ico"
)

REM --- seed (optional) ---
if /I "%~1"=="seed" (
  if exist seed\seed_config.json (
    set RES=!RES! /resource:"seed\seed_config.json","seed_config.json"
    echo [seed] embedded preset ON
  ) else (
    echo [WARN] seed\seed_config.json not found, building empty
  )
) else (
  echo [empty] building without preset
)

echo Compiling...
"%CSC%" /nologo /target:winexe /platform:anycpu /out:%NAME%.exe ^
  /reference:System.dll ^
  /reference:System.Core.dll ^
  /reference:System.Drawing.dll ^
  /reference:System.Windows.Forms.dll ^
  /reference:System.Web.Extensions.dll ^
  /reference:System.Xml.Linq.dll ^
  /reference:System.Security.dll ^
  /reference:System.IO.Compression.dll ^
  /reference:System.IO.Compression.FileSystem.dll ^
  /reference:libs\Renci.SshNet.dll ^
  !ICON! ^
  !RES! ^
  src\*.cs

if errorlevel 1 (
  echo [ERROR] Build failed.
  exit /b 1
)
echo.
echo [DONE] %NAME%.exe built. Ship only this file to the customer.
endlocal
