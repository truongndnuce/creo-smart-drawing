@echo off
set CREO_CMD="C:\Program Files\PTC\Creo 10.0.1.0\Parametric\bin\parametric.exe" "C:\Program Files\PTC\Creo 10.0.1.0\Parametric\bin\parametric.psf"
set PRO_COMM_MSG_EXE=C:\PROGRA~1\PTC\CREO10~1.0\COMMON~1\X86E_W~1\obj\PRO_CO~1.EXE
set CREO_KEEP=60
if not exist out\work mkdir out\work
out\spike.exe out\work
echo exit=%errorlevel%

