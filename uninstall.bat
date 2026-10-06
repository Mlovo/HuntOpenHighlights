@echo off
title HuntOpenHighlights - Uninstaller

echo ================================================================
echo   HuntOpenHighlights - Uninstaller
echo ================================================================
echo.

net session >nul 2>&1
if %errorLevel% neq 0 (
    echo [ERROR] This uninstaller requires Administrator privileges!
    echo Please right-click uninstall.bat and select "Run as administrator".
    echo.
    pause
    exit /b 1
)

echo [*] Removing registry entries...
reg delete "HKLM\SOFTWARE\NVIDIA Corporation\NvContainer\MessageBus" /f >nul 2>&1
reg delete "HKLM\SOFTWARE\NVIDIA Corporation\NvContainer\ModuleMap" /f >nul 2>&1
echo     [+] Registry cleaned up.

echo [*] Removing C:\NvidiaShim folder...
if exist "C:\NvidiaShim" (
    rmdir /S /Q "C:\NvidiaShim" >nul 2>&1
    echo     [+] C:\NvidiaShim removed.
)

echo.
echo [DONE] Uninstallation complete!
pause
