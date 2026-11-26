@echo off
rem Creo worker (native backend) elevated: polls the API and builds jobs in real Creo
cd /d "%~dp0.."
set CREO_CMD=unused
set CADFLOW_BACKEND=native
set CADFLOW_SERVER=http://localhost:18090/
set CADFLOW_WORKER_KEY=dev-worker-key
set CADFLOW_WORK_DIR=%CD%\out\worker-work
set LOG=out\worker-native.log
"%~dp0..\..\..\src\Cadflow.CreoWorker\bin\Release\net10.0\Cadflow.CreoWorker.exe" > %LOG% 2>&1
