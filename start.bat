@echo off
setlocal
powershell.exe -NoLogo -NoProfile -ExecutionPolicy Bypass -File "%~dp0scripts\start.ps1" %*
set "launchExitCode=%ERRORLEVEL%"
if not "%launchExitCode%"=="0" (
    echo.
    echo The LuvLetter debug session failed. Review the output above.
)
echo.
echo Debug session ended. Press any key to close this launcher.
pause >nul
exit /b %launchExitCode%
