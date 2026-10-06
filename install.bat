@echo off
setlocal enabledelayedexpansion
title HuntOpenHighlights - Installer

echo ================================================================
echo   HuntOpenHighlights - Automatic Setup
echo ================================================================
echo.

:: Check for Administrator privileges
net session >nul 2>&1
if %errorLevel% neq 0 (
    echo [ERROR] This installer requires Administrator privileges!
    echo Please right-click install.bat and select "Run as administrator".
    echo.
    pause
    exit /b 1
)

set SCRIPT_DIR=%~dp0
set TARGET_DIR=C:\NvidiaShim

echo [*] Step 1: Creating target directory %TARGET_DIR%...
if not exist "%TARGET_DIR%" mkdir "%TARGET_DIR%"

echo [*] Step 2: Copying signed MessageBus.dll...
if exist "%SCRIPT_DIR%bin\MessageBus.dll" (
    copy /Y "%SCRIPT_DIR%bin\MessageBus.dll" "%TARGET_DIR%\MessageBus.dll" >nul
    echo     [+] MessageBus.dll installed to %TARGET_DIR%
) else (
    echo     [!] Warning: bin\MessageBus.dll not found in current folder!
)

echo [*] Step 3: Registering NVIDIA MessageBus in Windows Registry...
reg add "HKLM\SOFTWARE\NVIDIA Corporation\NvContainer\MessageBus" /v "InstallPath" /t REG_SZ /d "%TARGET_DIR%" /f >nul
reg add "HKLM\SOFTWARE\NVIDIA Corporation\NvContainer\MessageBus" /v "LogPath" /t REG_SZ /d "%TARGET_DIR%" /f >nul
reg add "HKLM\SOFTWARE\NVIDIA Corporation\NvContainer\MessageBus" /v "LogLevel" /t REG_DWORD /d 5 /f >nul
reg add "HKLM\SOFTWARE\NVIDIA Corporation\NvContainer\ModuleMap" /v "MessageBus.dll" /t REG_SZ /d "%TARGET_DIR%\MessageBus.dll" /f >nul
echo     [+] Registry keys configured successfully!

echo [*] Step 4: Installing Python dependencies...
python -m pip install -r "%SCRIPT_DIR%requirements.txt"
if %errorLevel% neq 0 (
    echo     [!] Warning: Failed to install Python packages. Please ensure Python is installed and added to PATH.
) else (
    echo     [+] Dependencies installed successfully!
)

echo.
echo ================================================================
echo   INSTALLATION COMPLETE!
echo ================================================================
echo [*] To start capturing highlights:
echo     - Run "run.bat" (visible window)
echo     - OR run "run_hidden.vbs" (silent background mode)
echo [*] Then launch Hunt: Showdown 1896 via Steam.
echo.
pause
