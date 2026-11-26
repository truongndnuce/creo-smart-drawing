@echo off
set CREO_CMD="C:\Program Files\PTC\Creo 10.0.1.0\Parametric\bin\parametric.exe" "C:\Program Files\PTC\Creo 10.0.1.0\Parametric\bin\parametric.psf"
set PRO_COMM_MSG_EXE=C:\PROGRA~1\PTC\CREO10~1.0\COMMON~1\X86E_W~1\obj\PRO_CO~1.EXE
set CREO_KEEP=15
out\spike2.exe "%CD%\out\work2" d6_rectangular_beam_1
echo exit=%errorlevel%
