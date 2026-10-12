@echo off
setlocal
if exist "%~dp0dist\win-x64\PC Companion.exe" (
    start "" "%~dp0dist\win-x64\PC Companion.exe"
    exit /b 0
)
echo Build the portable app first using build.ps1. See README.md for instructions.
pause
exit /b 1
