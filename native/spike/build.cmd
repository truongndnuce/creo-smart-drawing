@echo off
rem Builds spike.exe with the same flags as protoolkit\x86e_win64\obj\make_async_md (/MD, PT_ASYNC_MD).
call "C:\Program Files (x86)\Microsoft Visual Studio\2022\BuildTools\VC\Auxiliary\Build\vcvars64.bat" >nul
if "%SRC%"=="" set SRC=spike
set PTC=C:\Program Files\PTC\Creo 10.0.1.0\Common Files
set INC=/I"%PTC%\protoolkit\includes" /I"%PTC%\protoolkit\protk_appls\includes"
set OBJ=%PTC%\protoolkit\x86e_win64\obj
if not exist out mkdir out
cl /nologo /MD /c /wd4430 -GS -fp:precise -D_WSTDIO_DEFINED -DPRO_USE_VAR_ARGS -DPRO_USE_VAR_ARG -DPT_ASYNC_MD %INC% /Fo:out\ %SRC%.c || exit /b 1
link /nologo /force:multiple /NODEFAULTLIB:libcmd.lib /subsystem:console /machine:amd64 /out:out\%SRC%.exe out\%SRC%.obj "%OBJ%\protkmd_NU.lib" "%OBJ%\ptasyncmd.lib" "%OBJ%\ucore.lib" "%OBJ%\udata.lib" kernel32.lib user32.lib wsock32.lib advapi32.lib mpr.lib winspool.lib netapi32.lib psapi.lib gdi32.lib shell32.lib comdlg32.lib ole32.lib ws2_32.lib

