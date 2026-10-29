@echo off
title Android App Installer & File Provider

if "%~1"=="" (
    python "%~dp0install_app.py"
) else (
    if exist "%~1" (
        python "%~dp0install_app.py" -f "%~1" %2 %3 %4 %5 %6 %7 %8 %9
    ) else (
        python "%~dp0install_app.py" %*
    )
)

if %ERRORLEVEL% NEQ 0 (
    echo.
    pause
)
