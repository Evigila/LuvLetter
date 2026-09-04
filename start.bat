@echo off
setlocal
powershell.exe -NoLogo -NoProfile -ExecutionPolicy Bypass -File "%~dp0scripts\start.ps1" %*
set "launchExitCode=%ERRORLEVEL%"
if not "%launchExitCode%"=="0" (
    echo.
    echo LuvLetter could not be launched. Review the error above.
    pause
)
exit /b %launchExitCode%
