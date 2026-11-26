@echo off
rem The scheduled task CreoSpike always runs this file; put another script's name in task-next.cmd to run that instead.
if exist "%~dp0task-next.cmd" call "%~dp0task-next.cmd" & goto :eof
cd /d "%~dp0.."
set CREO_CMD="C:\Program Files\PTC\Creo 10.0.1.0\Parametric\bin\parametric.exe" "C:\Program Files\PTC\Creo 10.0.1.0\Parametric\bin\parametric.psf"
set PRO_COMM_MSG_EXE=C:\PROGRA~1\PTC\CREO10~1.0\COMMON~1\X86E_W~1\obj\PRO_CO~1.EXE
set CREO_KEEP=20
if defined LOG goto run
rem started by the scheduled task (no wrapper log): write our own
set LOG=out\run4-task-%RANDOM%.log
dllhost\out\DllHost.exe "%CD%\out\work4" block > %LOG% 2>&1
echo exit=%errorlevel% >> %LOG%
goto :eof
:run
dllhost\out\DllHost.exe "%CD%\out\work4" block
echo exit=%errorlevel%
