@echo off
rem Builds dist\com.coffi.vrchat-mobile-optimizer-<version>.unitypackage (Unity is not required).
rem Usage: tools\build-unitypackage.bat

setlocal
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0build-unitypackage.ps1" %*
if errorlevel 1 (
    echo Build failed.
    endlocal
    exit /b 1
)
endlocal
