@echo off
:: Rebuilds PulseWatch from native\ and starts it. Exit PulseWatch from the tray first (the exe is locked while it runs).
cd /d "%~dp0native"
tasklist /FI "IMAGENAME eq PulseWatch.exe" | find /I "PulseWatch.exe" >nul
if %errorlevel%==0 (
    echo PulseWatch is still running. Right-click the tray icon and choose Exit, then run this again.
    pause
    exit /b 1
)
set DOTNET="%LOCALAPPDATA%\Microsoft\dotnet\dotnet.exe"
if not exist %DOTNET% set DOTNET=dotnet
%DOTNET% publish -c Release -o ..\bin\native_win64
if errorlevel 1 (
    echo Build failed.
    pause
    exit /b 1
)
start "" "%~dp0bin\native_win64\PulseWatch.exe"
