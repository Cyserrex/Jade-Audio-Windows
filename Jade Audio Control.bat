@echo off
setlocal enabledelayedexpansion
cd /d "%~dp0"
title Jade Audio Control

rem Several Pythons are usually on PATH and only some of them have what this
rem app needs, so pick one deliberately instead of trusting the first hit.

set "PYEXE="
for %%I in ("py -3" "python" "%LOCALAPPDATA%\Programs\Python\Python312\python.exe") do (
    if not defined PYEXE (
        %%~I -c "import tkinter, hid" >nul 2>&1 && set "PYEXE=%%~I"
    )
)

if defined PYEXE goto :launch

rem Nothing has hidapi yet. Find a Python with Tk and install it there.
echo Setting up for first use...
echo.
for %%I in ("py -3" "python" "%LOCALAPPDATA%\Programs\Python\Python312\python.exe") do (
    if not defined PYEXE (
        %%~I -c "import tkinter" >nul 2>&1 && set "PYEXE=%%~I"
    )
)

if not defined PYEXE (
    echo Could not find a Python 3 installation with Tk support.
    echo.
    echo Install Python 3.10 or newer from https://www.python.org/downloads/
    echo and keep the "tcl/tk and IDLE" option ticked, then run this again.
    echo.
    pause
    exit /b 1
)

%PYEXE% -m pip install hidapi
%PYEXE% -c "import hid" >nul 2>&1
if errorlevel 1 (
    echo.
    echo Installing hidapi failed. Try running this by hand:
    echo     %PYEXE% -m pip install hidapi
    echo.
    pause
    exit /b 1
)

:launch
rem Prefer pythonw so no console window hangs around behind the app.
set "PYW="
for /f "delims=" %%V in ('%PYEXE% -c "import os,sys;p=os.path.join(os.path.dirname(sys.executable),'pythonw.exe');print(p if os.path.exists(p) else '')" 2^>nul') do set "PYW=%%V"

if defined PYW (
    start "" "%PYW%" run.py
    exit /b 0
)

%PYEXE% run.py
if errorlevel 1 pause
exit /b %errorlevel%
