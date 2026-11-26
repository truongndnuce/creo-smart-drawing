/* Spike 2: with Creo running (Toolkit async), retrieve a shipped sample part, regenerate, read mass props + outline, export STEP.
   env CREO_CMD as in spike 1; argv[1] = work dir that already contains <argv[2]>.prt */
#include "ProToolkit.h"
#include "ProCore.h"
#include "ProUtil.h"
#include "ProSizeConst.h"
#include "ProTKRunTime.h"
#include "ProMdl.h"
#include "ProSolid.h"
#include "ProIntf3Dexport.h"
#include <stdio.h>
#include <string.h>
#include <stdlib.h>
#include <windows.h>

#define CHECK(what, e) do { ProError _e = (e); printf("%-34s -> %d\n", what, (int)_e); fflush(stdout); if (_e != PRO_TK_NO_ERROR) { rc = 1; goto done; } } while (0)

int main(int argc, char *argv[])
{
    int rc = 0; int started = 0;
    ProPath wd, out; ProName name; ProMdl mdl; ProMassProperty mp; Pro3dPnt box[2];
    const char *cmd = getenv("CREO_CMD");
    if (!cmd || argc < 3) { printf("usage: set CREO_CMD; spike2 <workdir> <partname>\n"); return 2; }

    CHECK("ProEngineerStart", ProEngineerStart((char*)cmd, "")); started = 1;
    ProStringToWstring(wd, argv[1]);
    CHECK("ProDirectoryChange", ProDirectoryChange(wd));

    ProStringToWstring(name, argv[2]);
    CHECK("ProMdlnameRetrieve", ProMdlnameRetrieve(name, PRO_MDLFILE_PART, &mdl));
    CHECK("ProSolidRegenerate", ProSolidRegenerate((ProSolid)mdl, PRO_REGEN_NO_FLAGS));
    memset(&mp, 0, sizeof mp);
    CHECK("ProSolidMassPropertyGet", ProSolidMassPropertyGet((ProSolid)mdl, NULL, &mp));
    printf("  volume=%g surface=%g density=%g mass=%g\n", mp.volume, mp.surface_area, mp.density, mp.mass);
    CHECK("ProSolidOutlineGet", ProSolidOutlineGet((ProSolid)mdl, box));
    printf("  outline min=(%g,%g,%g) max=(%g,%g,%g)\n", box[0][0], box[0][1], box[0][2], box[1][0], box[1][1], box[1][2]);

    { char p[PRO_PATH_SIZE]; sprintf(p, "%s\\%s.stp", argv[1], argv[2]); ProStringToWstring(out, p); }
    CHECK("ProIntf3DFileWriteWithDefaultProfile", ProIntf3DFileWriteWithDefaultProfile((ProSolid)mdl, PRO_INTF_EXPORT_STEP, out));
    { const char *keep = getenv("CREO_KEEP"); int s = keep ? atoi(keep) : 0; if (s > 0) { printf("Creo stays open for %d s ...\n", s); fflush(stdout); Sleep(s * 1000); } }
done:
    if (started) printf("%-34s -> %d\n", "ProEngineerEnd", (int)ProEngineerEnd());
    return rc;
}
