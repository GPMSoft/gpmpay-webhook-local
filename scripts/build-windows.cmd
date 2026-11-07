@echo off
rem Double-click to build the Windows x64 version. Arguments are passed to build-windows.ps1, e.g.: build-windows.cmd -Arch arm64
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0build-windows.ps1" %*
if errorlevel 1 (
    echo.
    echo Build failed.
    if "%~1"=="" pause
    exit /b 1
)
if "%~1"=="" pause
