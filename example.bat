@echo off
setlocal
set FASTER=%~dp0Faster.exe

"%FASTER%" --help
"%FASTER%" --list

REM Apply a saved list by name - replace with your own list name.
"%FASTER%" --activate "Gaming Mode"
if errorlevel 1 (
    echo Activate failed with exit code %errorlevel%
    goto :end
)

REM Put every service back exactly how the baseline found it - no saved list needed.
"%FASTER%" --restore
if errorlevel 1 (
    echo Restore failed with exit code %errorlevel%
    goto :end
)

:end
endlocal
