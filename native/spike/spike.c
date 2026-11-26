/* Spike 1: start Creo 10 via Toolkit async. env CREO_CMD = "<parametric.exe>" "<parametric.psf>"; optional argv[1] = workdir */
#include "ProToolkit.h"
#include "ProCore.h"
#include "ProUtil.h"
#include "ProSizeConst.h"
#include "ProTKRunTime.h"
#include <stdio.h>
#include <string.h>
#include <stdlib.h>
#include <windows.h>

int main(int argc, char *argv[])
{
    ProError err;
    ProPath wd;
    char buf[PRO_PATH_SIZE];
    const char *cmd = getenv("CREO_CMD");
    if (!cmd) { printf("set env CREO_CMD to the parametric.exe + parametric.psf command line\n"); return 2; }

    printf("ProEngineerStart(%s) ...\n", cmd); fflush(stdout);
    err = ProEngineerStart((char*)cmd, "");
    printf("ProEngineerStart -> %d\n", (int)err); fflush(stdout);
    if (err != PRO_TK_NO_ERROR) return 1;

    if (argc > 1) {
        ProStringToWstring(wd, argv[1]);
        err = ProDirectoryChange(wd);
        printf("ProDirectoryChange -> %d\n", (int)err);
    }
    err = ProDirectoryCurrentGet(wd);
    ProWstringToString(buf, wd);
    printf("ProDirectoryCurrentGet -> %d, '%s'\n", (int)err, buf); fflush(stdout);

    { const char *keep = getenv("CREO_KEEP"); int s = keep ? atoi(keep) : 0;
      if (s > 0) { printf("Creo stays open for %d s ...\n", s); fflush(stdout); Sleep(s * 1000); } }
    err = ProEngineerEnd();
    printf("ProEngineerEnd -> %d\n", (int)err);
    return 0;
}


