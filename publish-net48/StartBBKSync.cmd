@echo off
rem BBKSync launcher: ensures .NET Framework 4.8 on the system, installs it from
rem the bundled offline installer if missing, then starts BBKSync.exe
setlocal
set "DIR=%~dp0"

call :is48
if not errorlevel 1 goto :run

rem 4.8 missing: installing requires admin, relanch elevated if needed
net session >nul 2>&1
if not "%errorlevel%"=="0" goto :elevate

:install
type "%DIR%msg_need48.txt"
echo.
if not exist "%DIR%NDP48-x86-x64-AllOS-ENU.exe" goto :nofile
"%DIR%NDP48-x86-x64-AllOS-ENU.exe" /q /norestart
set "RC=%errorlevel%"
call :is48
if not errorlevel 1 goto :installed
echo INSTALL FAILED, exit code %RC% ^(1641 or 3010 = installed but reboot required^)
type "%DIR%msg_manual.txt"
echo.
pause
exit /b 1

:nofile
type "%DIR%msg_nofile.txt"
echo.
pause
exit /b 1

:elevate
type "%DIR%msg_elev.txt"
echo.
powershell -NoProfile -Command "Start-Process -FilePath '%~f0' -Verb RunAs"
exit /b 0

:installed
type "%DIR%msg_installed.txt"
echo.
goto :run

:run
type "%DIR%msg_run.txt"
echo.
start "" "%DIR%BBKSync.exe"
exit /b 0

:is48
rem returns 0 if .NET Framework 4.8 (or newer) is installed, else 1
set "IS48=1"
set "REL="
reg query "HKLM\SOFTWARE\Microsoft\NET Framework Setup\NDP\v4\Full" /v Release >nul 2>&1
if errorlevel 1 set "IS48=0"
if "%IS48%"=="0" exit /b 1
for /f "tokens=3" %%a in ('reg query "HKLM\SOFTWARE\Microsoft\NET Framework Setup\NDP\v4\Full" /v Release 2^>nul') do set "REL=%%a"
if not defined REL exit /b 1
set /a "R=REL"
if %R% LSS 528040 exit /b 1
exit /b 0