@echo off
rem Smoke test of CreoGeaHost on real Creo 10 (needs Administrator; the scheduled task CreoSpike can be pointed here or run this directly)
cd /d "%~dp0.."
set CREO_CMD=unused
set CB_TRACE=1
set SMOKE_HOLD_S=90
set SMOKE_TIMEOUT_S=240
set LOG=out\run5-%RANDOM%.log
"%~dp0..\..\..\tools\CreoGeaSmoke\bin\Release\net10.0\CreoGeaSmoke.exe" "%CD%\out\work5" > %LOG% 2>&1
echo exit=%errorlevel% >> %LOG%

