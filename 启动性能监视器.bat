@echo off
setlocal
cd /d "%~dp0"

set "APP_EXE=src\PerformanceMonitor\bin\x64\Release\net8.0-windows\win-x64\PerformanceMonitor.exe"

if exist "%APP_EXE%" goto launch

where dotnet.exe >nul 2>nul
if errorlevel 1 goto no_sdk

echo Performance Monitor is not built. Building Release x64...
dotnet build "PerformanceMonitor.sln" -c Release -p:Platform=x64
if errorlevel 1 goto build_failed

if not exist "%APP_EXE%" goto exe_missing

:launch
start "" "%APP_EXE%"
if errorlevel 1 goto launch_failed
exit /b 0

:no_sdk
echo .NET 8 SDK was not found. Install the .NET 8 SDK and try again.
pause
exit /b 1

:build_failed
echo Release x64 build failed. Review the build errors above.
pause
exit /b 1

:exe_missing
echo Build completed, but PerformanceMonitor.exe was not found.
pause
exit /b 1

:launch_failed
echo PerformanceMonitor.exe could not be started.
pause
exit /b 1
