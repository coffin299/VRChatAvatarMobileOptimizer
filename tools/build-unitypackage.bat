@echo off
rem Builds dist\com.coffi.vrchat-mobile-optimizer-<version>.unitypackage (Unity is not required).
rem Usage: tools\build-unitypackage.bat

setlocal
set "EXIT_CODE=0"

powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0build-unitypackage.ps1" %*
if errorlevel 1 (
    echo.
    echo Build failed.
    set "EXIT_CODE=1"
)

rem Keep the window open when launched by double-click so the result can be read.
echo %cmdcmdline% | find /i "%~0" >nul
if not errorlevel 1 pause

endlocal & exit /b %EXIT_CODE%
