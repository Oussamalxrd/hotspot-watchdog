@echo off
setlocal
cd /d "%~dp0"

tasklist /fi "imagename eq HotspotWatchdog.exe" 2>nul | find /i "HotspotWatchdog.exe" >nul
if not errorlevel 1 (
  echo Hotspot Watchdog is running - close it first: tray icon ^> Exit.
  pause
  exit /b 1
)

set "CSC=%WINDIR%\Microsoft.NET\Framework64\v4.0.30319\csc.exe"
if not exist "%CSC%" set "CSC=%WINDIR%\Microsoft.NET\Framework\v4.0.30319\csc.exe"
if not exist "%CSC%" (
  echo .NET Framework 4.x compiler not found.
  pause
  exit /b 1
)

set "ICON="
if exist app.ico set "ICON=/win32icon:app.ico"

"%CSC%" /nologo /target:winexe /optimize+ /out:HotspotWatchdog.exe %ICON% /r:System.dll /r:System.Core.dll /r:System.Drawing.dll /r:System.Windows.Forms.dll HotspotWatchdog.cs
if errorlevel 1 (
  echo.
  echo Build FAILED - copy the errors above into the chat.
  pause
  exit /b 1
)

echo.
echo Built HotspotWatchdog.exe
timeout /t 3 >nul
