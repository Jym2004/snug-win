@echo off
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0Install-SnugDriver.ps1" -Action Install
if errorlevel 1 (
    echo Installation failed with errorlevel %errorlevel%.
    pause
    exit /b %errorlevel%
)
exit /b 0
