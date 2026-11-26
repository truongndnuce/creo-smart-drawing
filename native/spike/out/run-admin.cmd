@echo off
rem Runs the Creo 10 Toolkit spike as Administrator. The window stays open; the log is also saved to out\run.log
echo [%date% %time%] run-admin started > "%~dp0admin-trace.txt"
net session >nul 2>&1
if errorlevel 1 (
  echo Asking for Administrator rights...
  powershell -NoProfile -Command "Start-Process -FilePath '%~f0' -Verb RunAs"
  exit /b
)
echo Running as Administrator.
cd /d "%~dp0.."
echo cwd=%cd% >> "%~dp0admin-trace.txt"
set LOG=out\run-%RANDOM%.log
call out\run.cmd > %LOG% 2>&1
type %LOG%
echo.
echo ===== finished, press any key to close =====
pause >nul

