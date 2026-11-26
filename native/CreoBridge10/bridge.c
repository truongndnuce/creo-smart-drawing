/* CreoBridge10: flat C ABI over Creo Toolkit C (async mode). Spike version.
   Toolkit may only be called from the thread that called ProEngineerStart; cb_* refuse other threads (-100). */
#include "ProToolkit.h"
#include "ProCore.h"
#include "ProUtil.h"
#include "ProSizeConst.h"
#include "ProTKRunTime.h"
#include "ProMdl.h"
#include "ProSolid.h"
#include "ProIntf3Dexport.h"
#include "ProFeature.h"
#include "ProElemId.h"
#include "ProExtrude.h"
#include "ProRevolve.h"
#include "ProModFeat.h"
#include "ProStdSection.h"
#include "ProElement.h"
#include "ProElempath.h"
#include "ProFeatType.h"
#include "ProFeatForm.h"
#include "ProSelection.h"
#include "ProSection.h"
#include "ProSecerror.h"
#include "Pro2dEntdef.h"
#include "ProArray.h"
#include "ProModelitem.h"
#include "ProView.h"
#include "ProWindows.h"
#include <stdlib.h>
#include <stdio.h>
#include <string.h>
#include <windows.h>

#define CB_API __declspec(dllexport)
#define CB_WRONG_THREAD (-100)
#define CB_NOT_STARTED (-101)

static DWORD g_thread = 0;
static int g_started = 0;
static char g_err[256] = "";

static int guard(void)
{
    if (!g_started) return CB_NOT_STARTED;
    if (GetCurrentThreadId() != g_thread) return CB_WRONG_THREAD;
    return 0;
}
static int fail(const char *what, int e) { _snprintf(g_err, sizeof g_err, "%s failed: %d", what, e); g_err[sizeof g_err - 1] = 0; return e; }

static char g_info[256] = "";
CB_API const char *cb_last_error(void) { return g_err; }
CB_API const char *cb_info(void) { return g_info; }
CB_API int cb_version(void) { return 1; }

/* cmd = "\"...\\parametric.exe\" \"...\\parametric.psf\"" */
CB_API int cb_start(const char *cmd)
{
    ProError e;
    if (g_started) return 0;
    e = ProEngineerStart((char *)cmd, "");
    if (e != PRO_TK_NO_ERROR) return fail("ProEngineerStart", (int)e);
    g_thread = GetCurrentThreadId();
    g_started = 1;
    return 0;
}

/* Collapses the embedded start-page browser (the PTC advertising page) so the model gets the whole window. Best effort. */
static void hide_browser(void)
{
    int w; if (ProWindowCurrentGet(&w) == PRO_TK_NO_ERROR) ProWindowBrowserSizeSet(w, 0.0);
}

CB_API int cb_set_workdir(const char *dir)
{
    ProPath wd; int g = guard(); if (g) return g;
    hide_browser();
    ProStringToWstring(wd, (char *)dir);
    { ProError e = ProDirectoryChange(wd); return e == PRO_TK_NO_ERROR ? 0 : fail("ProDirectoryChange", (int)e); }
}

/* Retrieves <name>.prt from the working directory, regenerates, fills out[10] =
   volume, surface, density, mass, minX,minY,minZ, maxX,maxY,maxZ; optionally writes STEP (stepPath may be NULL). */
CB_API int cb_measure_part(const char *name, const char *stepPath, double *out)
{
    ProName n; ProMdl mdl; ProMassProperty mp; Pro3dPnt box[2]; ProError e; int g = guard(); if (g) return g;
    ProStringToWstring(n, (char *)name);
    if (ProMdlnameInit(n, PRO_MDLFILE_PART, &mdl) != PRO_TK_NO_ERROR && (e = ProMdlnameRetrieve(n, PRO_MDLFILE_PART, &mdl)) != PRO_TK_NO_ERROR) return fail("ProMdlnameRetrieve", (int)e);
    if ((e = ProSolidRegenerate((ProSolid)mdl, PRO_REGEN_NO_FLAGS)) != PRO_TK_NO_ERROR) return fail("ProSolidRegenerate", (int)e);
    memset(&mp, 0, sizeof mp);
    if ((e = ProSolidOutlineGet((ProSolid)mdl, box)) != PRO_TK_NO_ERROR) return fail("ProSolidOutlineGet", (int)e);
    out[4] = box[0][0]; out[5] = box[0][1]; out[6] = box[0][2]; out[7] = box[1][0]; out[8] = box[1][1]; out[9] = box[1][2];
    if ((e = ProSolidMassPropertyGet((ProSolid)mdl, NULL, &mp)) != PRO_TK_NO_ERROR) {
        /* e.g. a part without material: keep the outline, report mass as unavailable */
        out[0] = out[1] = out[2] = out[3] = -1;
        _snprintf(g_info, sizeof g_info, "mass properties unavailable (%d)", (int)e); g_info[sizeof g_info - 1] = 0;
    } else { out[0] = mp.volume; out[1] = mp.surface_area; out[2] = mp.density; out[3] = mp.mass; }
    if (stepPath && stepPath[0]) {
        ProPath p; ProStringToWstring(p, (char *)stepPath);
        if ((e = ProIntf3DFileWriteWithDefaultProfile((ProSolid)mdl, PRO_INTF_EXPORT_STEP, p)) != PRO_TK_NO_ERROR) return fail("ProIntf3DFileWrite", (int)e);
    }
    return 0;
}

/* ---------------- model creation ---------------- */
static ProMdl g_model = NULL;

/* Copies the template part (<templateName>.prt must be in the working directory) to a new in-session part and displays it. */
CB_API int cb_new_part(const char *templateName, const char *newName)
{
    ProName t, n; ProMdl tpl, nw; ProError e; int g = guard(); if (g) return g;
    ProStringToWstring(t, (char *)templateName); ProStringToWstring(n, (char *)newName);
    if ((e = ProMdlnameRetrieve(t, PRO_MDLFILE_PART, &tpl)) != PRO_TK_NO_ERROR) return fail("retrieve template", (int)e);
    if ((e = ProMdlnameCopy(tpl, n, &nw)) != PRO_TK_NO_ERROR) return fail("ProMdlnameCopy", (int)e);
    if ((e = ProMdlDisplay(nw)) != PRO_TK_NO_ERROR) return fail("ProMdlDisplay", (int)e);
    g_model = nw;
    hide_browser();
    return 0;
}

static int g_trace = -1; /* env CB_TRACE=1: print every stage to stdout (finds where Creo blocks) */
static void trace_stage(const char *what) {
    if (g_trace < 0) g_trace = getenv("CB_TRACE") ? 1 : 0;
    if (g_trace) { printf("[cb] %s\n", what); fflush(stdout); }
}
/* Fit the model in its window so the whole object is visible. */
CB_API int cb_fit_view(void)
{
    static const char *names[] = { "Default Orientation", "Default", "DEFAULT", "FRONT", NULL };
    int g = guard(), i; ProName n; ProView v; ProError e = PRO_TK_E_NOT_FOUND; if (g) return g;
    if (!g_model) return fail("no model", -1);
    hide_browser();
    for (i = 0; i < 4 && names[i]; i++) { ProStringToWstring(n, (char *)names[i]); if ((e = ProViewRetrieve(g_model, n, &v)) == PRO_TK_NO_ERROR) break; }
    if (e != PRO_TK_NO_ERROR) return fail("ProViewRetrieve", (int)e);
    if ((e = ProViewRefit(g_model, v)) != PRO_TK_NO_ERROR) return fail("ProViewRefit", (int)e);
    { int w; if (ProWindowCurrentGet(&w) == PRO_TK_NO_ERROR) ProWindowRepaint(w); }
    return 0;
}
#define ST(what, expr) do { ProError _e; trace_stage(what); _e = (expr); if (_e != PRO_TK_NO_ERROR) { stage_fail = what; rc = (int)_e; goto cleanup; } } while (0)

static ProError add_elem(ProElement parent, ProElemId id, ProElement *out)
{
    ProElement el; ProError e = ProElementAlloc(id, &el);
    if (e != PRO_TK_NO_ERROR) return e;
    e = ProElemtreeElementAdd(parent, NULL, el);
    if (e == PRO_TK_NO_ERROR && out) *out = el;
    return e;
}
static ProError add_int(ProElement parent, ProElemId id, int v)
{
    ProElement el; ProError e = ProElementAlloc(id, &el);
    if (e != PRO_TK_NO_ERROR) return e;
    if ((e = ProElementIntegerSet(el, v)) != PRO_TK_NO_ERROR) return e;
    return ProElemtreeElementAdd(parent, NULL, el);
}
static ProError add_dbl(ProElement parent, ProElemId id, double v)
{
    ProElement el; ProError e = ProElementAlloc(id, &el);
    if (e != PRO_TK_NO_ERROR) return e;
    ProElementDecimalsSet(el, 4);
    if ((e = ProElementDoubleSet(el, v)) != PRO_TK_NO_ERROR) return e;
    return ProElemtreeElementAdd(parent, NULL, el);
}
static ProError add_ref(ProElement parent, ProElemId id, ProSelection sel)
{
    ProElement el; ProReference r; ProError e = ProElementAlloc(id, &el);
    if (e != PRO_TK_NO_ERROR) return e;
    if ((e = ProSelectionToReference(sel, &r)) != PRO_TK_NO_ERROR) return e;
    if ((e = ProElementReferenceSet(el, r)) != PRO_TK_NO_ERROR) return e;
    return ProElemtreeElementAdd(parent, NULL, el);
}

static ProError datum_sel(ProMdl mdl, const char *name, ProSelection *sel)
{
    ProName n; ProModelitem item; ProError e;
    ProStringToWstring(n, (char *)name);
    if ((e = ProModelitemByNameInit(mdl, PRO_SURFACE, n, &item)) != PRO_TK_NO_ERROR) return e;
    return ProSelectionAlloc(NULL, &item, sel);
}

/* How the sketch gets located/constrained (tried in turn by the host until one works):
   0 = regenerate only, 1 = project the two reference datums then autodim + regenerate, 2 = intent manager mode, no autodim. */
static int g_strategy = 1; /* 1 = the one that works on Creo 10 (spike 4) */
CB_API void cb_set_strategy(int s) { g_strategy = s; }

static int g_circle = 0; /* 1: section is a circle (center x0,y0, radius x1) */
static int g_flip = 0;   /* 1: extrude toward the other side of the sketch plane */
static const double *g_rev_pts = 0; static int g_rev_n = 0; static double g_rev_axis, g_rev_y0, g_rev_y1; /* revolve profile (sketch coords) */
static double g_corner = 0;               /* outer corner radius of the rectangle */
static int g_inner = 0;                     /* 1: a second (inner) rounded rectangle makes the section a ring */
static double g_ix0, g_iy0, g_ix1, g_iy1, g_ir;

/* Rectangle [x0,x1]x[y0,y1]; corners rounded with radius r (4 lines + 4 arcs) when r > 0. */
static ProError add_rrect(ProSection sec, double x0, double y0, double x1, double y1, double r, const char **stage)
{
    Pro2dLinedef ln; Pro2dArcdef ar; int id, i; ProError e; const double H = 1.5707963267948966;
    if (r > 0) {
        double sx[4][4] = { { x0 + r, y0, x1 - r, y0 }, { x1, y0 + r, x1, y1 - r }, { x1 - r, y1, x0 + r, y1 }, { x0, y1 - r, x0, y0 + r } };
        double cc[4][2] = { { x1 - r, y0 + r }, { x1 - r, y1 - r }, { x0 + r, y1 - r }, { x0 + r, y0 + r } };
        for (i = 0; i < 4; i++) {
            ln.type = PRO_2D_LINE; ln.end1[0] = sx[i][0]; ln.end1[1] = sx[i][1]; ln.end2[0] = sx[i][2]; ln.end2[1] = sx[i][3];
            if ((e = ProSectionEntityAdd(sec, (Pro2dEntdef *)&ln, &id)) != PRO_TK_NO_ERROR) { *stage = "ProSectionEntityAdd(line)"; return e; }
            ar.type = PRO_2D_ARC; ar.center[0] = cc[i][0]; ar.center[1] = cc[i][1]; ar.radius = r;
            ar.start_angle = -H + i * H; ar.end_angle = i * H;
            if ((e = ProSectionEntityAdd(sec, (Pro2dEntdef *)&ar, &id)) != PRO_TK_NO_ERROR) { *stage = "ProSectionEntityAdd(arc)"; return e; }
        }
    } else {
        double px[5] = { x0, x1, x1, x0, x0 }, py[5] = { y0, y0, y1, y1, y0 };
        for (i = 0; i < 4; i++) {
            ln.type = PRO_2D_LINE; ln.end1[0] = px[i]; ln.end1[1] = py[i]; ln.end2[0] = px[i+1]; ln.end2[1] = py[i+1];
            if ((e = ProSectionEntityAdd(sec, (Pro2dEntdef *)&ln, &id)) != PRO_TK_NO_ERROR) { *stage = "ProSectionEntityAdd"; return e; }
        }
    }
    return PRO_TK_NO_ERROR;
}
static ProError build_rect(ProSection sec, ProSelection ref1, ProSelection ref2, double x0, double y0, double x1, double y1, const char **stage)
{
    Pro2dLinedef ln; int id, i; ProError e; ProWSecerror se;
    double px[5] = { x0, x1, x1, x0, x0 }, py[5] = { y0, y0, y1, y1, y0 };
    if (g_strategy == 2) {
        if ((e = ProSectionIntentManagerModeSet(sec, PRO_B_TRUE)) != PRO_TK_NO_ERROR) { *stage = "ProSectionIntentManagerModeSet"; return e; }
    }
    if (g_strategy == 1) {
        if ((e = ProSectionEntityFromProjection(sec, ref1, &id)) != PRO_TK_NO_ERROR) { *stage = "ProSectionEntityFromProjection(ref1)"; return e; }
        if ((e = ProSectionEntityFromProjection(sec, ref2, &id)) != PRO_TK_NO_ERROR) { *stage = "ProSectionEntityFromProjection(ref2)"; return e; }
    }
    if (g_rev_n > 0) {
        Pro2dLinedef cl; int cid, k;
        cl.type = PRO_2D_CENTER_LINE; cl.end1[0] = g_rev_axis; cl.end1[1] = g_rev_y0; cl.end2[0] = g_rev_axis; cl.end2[1] = g_rev_y1;
        if ((e = ProSectionEntityAdd(sec, (Pro2dEntdef *)&cl, &cid)) != PRO_TK_NO_ERROR) { *stage = "ProSectionEntityAdd(centerline)"; return e; }
        ProSectionEntityConstructionSet(sec, cid, PRO_B_FALSE);
        for (k = 0; k < g_rev_n; k++) {   /* closed polyline; the last segment (back to the first point) runs along the axis */
            int j = (k + 1) % g_rev_n;
            ln.type = PRO_2D_LINE; ln.end1[0] = g_rev_pts[2*k]; ln.end1[1] = g_rev_pts[2*k+1]; ln.end2[0] = g_rev_pts[2*j]; ln.end2[1] = g_rev_pts[2*j+1];
            if ((e = ProSectionEntityAdd(sec, (Pro2dEntdef *)&ln, &id)) != PRO_TK_NO_ERROR) { *stage = "ProSectionEntityAdd(profile)"; return e; }
        }
    } else if (g_circle) {
        Pro2dCircledef cd; cd.type = PRO_2D_CIRCLE; cd.center[0] = x0; cd.center[1] = y0; cd.radius = x1;
        if ((e = ProSectionEntityAdd(sec, (Pro2dEntdef *)&cd, &id)) != PRO_TK_NO_ERROR) { *stage = "ProSectionEntityAdd(circle)"; return e; }
    } else {
        if ((e = add_rrect(sec, x0, y0, x1, y1, g_corner, stage)) != PRO_TK_NO_ERROR) return e;
        if (g_inner && (e = add_rrect(sec, g_ix0, g_iy0, g_ix1, g_iy1, g_ir, stage)) != PRO_TK_NO_ERROR) return e;
    }
    (void)ln; (void)px; (void)py; (void)i;
    if (g_strategy == 2) return PRO_TK_NO_ERROR;
    if ((e = ProSecerrorAlloc(&se)) != PRO_TK_NO_ERROR) { *stage = "ProSecerrorAlloc"; return e; }
    trace_stage("sketch entities added");
    if (g_strategy == 1) {
        trace_stage("ProSectionAutodim");
        e = ProSectionAutodim(sec, &se);
        if (e != PRO_TK_NO_ERROR) { *stage = "ProSectionAutodim"; ProSecerrorFree(&se); return e; }
    }
    trace_stage("ProSectionRegenerate");
    e = ProSectionRegenerate(sec, &se);
    trace_stage("ProSectionRegenerate done");
    ProSecerrorFree(&se);
    if (e != PRO_TK_NO_ERROR) { *stage = "ProSectionRegenerate"; return e; }
    return PRO_TK_NO_ERROR;
}
static ProError first_surf(ProGeomitem *gi, ProError st, ProAppData data)
{ *(ProGeomitem *)data = *gi; return (ProError)1; }
/* Datum plane at 'offset' from datum 'base', named 'newName'. */
static ProError make_offset_datum(ProMdl mdl, const char *base, double offset, const char *newName, ProSelection *outSel)
{
    ProError e; ProSelection base_sel = NULL, mdl_sel = NULL; ProElement tree, ft, cs, c, ct, cr, co;
    ProModelitem mi; ProFeature feat; ProErrorlist errs; ProFeatureCreateOptions *opts = 0; ProReference ref;
    ProGeomitem gi; ProName nm; ProModelitem item;
    if ((e = datum_sel(mdl, base, &base_sel)) != PRO_TK_NO_ERROR) return e;
    ProElementAlloc(PRO_E_FEATURE_TREE, &tree);
    ProElementAlloc(PRO_E_FEATURE_TYPE, &ft); ProElementIntegerSet(ft, PRO_FEAT_DATUM); ProElemtreeElementAdd(tree, NULL, ft);
    ProElementAlloc(PRO_E_DTMPLN_CONSTRAINTS, &cs); ProElemtreeElementAdd(tree, NULL, cs);
    ProElementAlloc(PRO_E_DTMPLN_CONSTRAINT, &c); ProElemtreeElementAdd(cs, NULL, c);
    ProElementAlloc(PRO_E_DTMPLN_CONSTR_TYPE, &ct); ProElementIntegerSet(ct, PRO_DTMPLN_OFFS); ProElemtreeElementAdd(c, NULL, ct);
    ProElementAlloc(PRO_E_DTMPLN_CONSTR_REF, &cr); ProSelectionToReference(base_sel, &ref); ProElementReferenceSet(cr, ref); ProElemtreeElementAdd(c, NULL, cr);
    ProElementAlloc(PRO_E_DTMPLN_CONSTR_REF_OFFSET, &co); ProElementDecimalsSet(co, 4); ProElementDoubleSet(co, offset); ProElemtreeElementAdd(c, NULL, co);
    if ((e = ProMdlToModelitem(mdl, &mi)) != PRO_TK_NO_ERROR) return e;
    if ((e = ProSelectionAlloc(NULL, &mi, &mdl_sel)) != PRO_TK_NO_ERROR) return e;
    ProArrayAlloc(1, sizeof(ProFeatureCreateOptions), 1, (ProArray *)&opts); opts[0] = PRO_FEAT_CR_NO_OPTS;
    e = ProFeatureWithoptionsCreate(mdl_sel, tree, opts, PRO_REGEN_NO_FLAGS, &feat, &errs);
    ProArrayFree((ProArray *)&opts);
    if (e != PRO_TK_NO_ERROR) return e;
    e = ProFeatureGeomitemVisit(&feat, PRO_SURFACE, (ProGeomitemAction)first_surf, NULL, (ProAppData)&gi);
    if (e != PRO_TK_NO_ERROR && e != (ProError)1) return e;
    ProStringToWstring(nm, (char *)newName);
    item = *(ProModelitem *)&gi;
    ProModelitemNameSet(&item, nm);
    return ProSelectionAlloc(NULL, &item, outSel);
}
/* Extruded block: sketch plane = datum <plane> (e.g. "TOP"), orientation reference = datum <orient> (e.g. "FRONT"),
   rectangle [x0,x1] x [y0,y1] in sketch coordinates, extruded by height along the plane normal. */
CB_API int cb_block(const char *plane, const char *orient, const char *refA, const char *refB, double x0, double y0, double x1, double y1, double height, double elevation)
{
    int rc = 0; const char *stage_fail = "?"; const char *sstage = "?";
    ProElement tree = NULL, depth = NULL, from_to = NULL, section = NULL, setup = NULL, created = NULL, sketch_el = NULL, el = NULL;
    ProSelection plane_sel = NULL, orient_sel = NULL, model_sel = NULL, ref1_sel = NULL, ref2_sel = NULL;
    ProModelitem mi; ProFeature feature; ProFeatureCreateOptions *opts = 0; ProErrorlist errors;
    ProElempath path; ProElempathItem items[2]; ProSection sec; ProAsmcomppath cp; int g = guard(); ProError e;
    if (g) return g;
    g_err[0] = 0;
    if (!g_model) return fail("no model (call cb_new_part first)", -1);

    if (elevation != 0) {
        static int s_dtm = 0; char dn[32]; sprintf(dn, "ELEV_%d", ++s_dtm);
        ST("offset datum", make_offset_datum(g_model, plane, elevation, dn, &plane_sel));
    } else {
        ST("datum plane", datum_sel(g_model, plane, &plane_sel));
    }
    ST("datum orient", datum_sel(g_model, orient, &orient_sel));
    ST("datum refA", datum_sel(g_model, refA, &ref1_sel));
    ST("datum refB", datum_sel(g_model, refB, &ref2_sel));

    ST("tree", ProElementAlloc(PRO_E_FEATURE_TREE, &tree));
    if (g_rev_n > 0) {   /* full revolve (0..360 deg) about the centerline of the section */
        ProElement ang = NULL, afrom = NULL, ato = NULL;
        ST("form", add_int(tree, PRO_E_FEATURE_FORM, PRO_REVOLVE));
        ST("solid type", add_int(tree, PRO_E_EXT_SURF_CUT_SOLID_TYPE, PRO_REV_FEAT_TYPE_SOLID));
        ST("add material", add_int(tree, PRO_E_REMOVE_MATERIAL, PRO_REV_MATERIAL_ADD));
        ST("not thin", add_int(tree, PRO_E_FEAT_FORM_IS_THIN, PRO_REV_FEAT_FORM_NO_THIN));
        ST("angle", add_elem(tree, PRO_E_REV_ANGLE, &ang));
        ST("angle from", add_elem(ang, PRO_E_REV_ANGLE_FROM, &afrom));
        /* one-sided from the sketch plane: no FROM side, TO = 360 deg (header ProRevolve.h: use FROM_NONE) */
        ST("angle from type", add_int(afrom, PRO_E_REV_ANGLE_FROM_TYPE, PRO_REV_ANG_FROM_NONE));
        ST("angle to", add_elem(ang, PRO_E_REV_ANGLE_TO, &ato));
        ST("angle to type", add_int(ato, PRO_E_REV_ANGLE_TO_TYPE, PRO_REV_ANG_TO_ANGLE));
        ST("angle to val", add_dbl(ato, PRO_E_REV_ANGLE_TO_VAL, 360.0));
    } else {
    ST("form", add_int(tree, PRO_E_FEATURE_FORM, PRO_EXTRUDE));
    ST("solid type", add_int(tree, PRO_E_EXT_SURF_CUT_SOLID_TYPE, PRO_EXT_FEAT_TYPE_SOLID));
    ST("add material", add_int(tree, PRO_E_REMOVE_MATERIAL, PRO_EXT_MATERIAL_ADD));
    ST("not thin", add_int(tree, PRO_E_FEAT_FORM_IS_THIN, PRO_EXT_FEAT_FORM_NO_THIN));
    ST("depth", add_elem(tree, PRO_E_STD_EXT_DEPTH, &depth));
    ST("depth to", add_elem(depth, PRO_E_EXT_DEPTH_TO, &from_to));
    ST("depth to type", add_int(from_to, PRO_E_EXT_DEPTH_TO_TYPE, PRO_EXT_DEPTH_TO_BLIND));
    ST("depth to value", add_dbl(from_to, PRO_E_EXT_DEPTH_TO_VALUE, height));
    }
    ST("section", add_elem(tree, PRO_E_STD_SECTION, &section));
    ST("setup plane", add_elem(section, PRO_E_STD_SEC_SETUP_PLANE, &setup));
    ST("sec plane", add_ref(setup, PRO_E_STD_SEC_PLANE, plane_sel));
    ST("view dir", add_int(setup, PRO_E_STD_SEC_PLANE_VIEW_DIR, PRO_SEC_VIEW_DIR_SIDE_ONE));
    ST("orient dir", add_int(setup, PRO_E_STD_SEC_PLANE_ORIENT_DIR, PRO_SEC_ORIENT_DIR_UP));
    ST("orient ref", add_ref(setup, PRO_E_STD_SEC_PLANE_ORIENT_REF, orient_sel));

    ST("ProMdlToModelitem", ProMdlToModelitem(g_model, &mi));
    ST("ProSelectionAlloc", ProSelectionAlloc(NULL, &mi, &model_sel));
    ST("ProArrayAlloc", ProArrayAlloc(1, sizeof(ProFeatureCreateOptions), 1, (ProArray *)&opts));
    opts[0] = PRO_FEAT_CR_INCOMPLETE_FEAT;
    e = ProFeatureWithoptionsCreate(model_sel, tree, opts, PRO_REGEN_NO_FLAGS, &feature, &errors);
    ProArrayFree((ProArray *)&opts); opts = 0;
    ST("ProFeatureWithoptionsCreate(incomplete)", e);
    ST("ProFeatureElemtreeExtract", ProFeatureElemtreeExtract(&feature, NULL, PRO_FEAT_EXTRACT_NO_OPTS, &created));

    items[0].type = PRO_ELEM_PATH_ITEM_TYPE_ID; items[0].path_item.elem_id = PRO_E_STD_SECTION;
    items[1].type = PRO_ELEM_PATH_ITEM_TYPE_ID; items[1].path_item.elem_id = PRO_E_SKETCHER;
    ST("ProElempathAlloc", ProElempathAlloc(&path));
    ST("ProElempathDataSet", ProElempathDataSet(path, items, 2));
    ST("get sketcher", ProElemtreeElementGet(created, path, &sketch_el));
    ProElempathFree(&path);
    ST("get section", ProElementSpecialvalueGet(sketch_el, NULL, (void *)&sec));
    e = build_rect(sec, ref1_sel, ref2_sel, x0, y0, x1, y1, &sstage);
    if (e != PRO_TK_NO_ERROR) { stage_fail = sstage; rc = (int)e; goto cleanup; }

    /* the two elements that can only be set once the section exists (an extrusion only) */
    if (g_rev_n == 0) {
    items[0].path_item.elem_id = PRO_E_STD_MATRLSIDE;
    ST("path1", ProElempathAlloc(&path)); ST("path1 set", ProElempathDataSet(path, items, 1));
    ST("get matrlside", ProElemtreeElementGet(created, path, &el)); ProElempathFree(&path);
    ST("set matrlside", ProElementIntegerSet(el, PRO_EXT_MATERIAL_SIDE_ONE));
    items[0].path_item.elem_id = PRO_E_STD_DIRECTION;
    ST("path2", ProElempathAlloc(&path)); ST("path2 set", ProElempathDataSet(path, items, 1));
    ST("get direction", ProElemtreeElementGet(created, path, &el)); ProElempathFree(&path);
    ST("set direction", ProElementIntegerSet(el, g_flip ? PRO_EXT_CR_IN_SIDE_ONE : PRO_EXT_CR_IN_SIDE_TWO)); /* TOP plane: SIDE_TWO extrudes toward +Y (up) */
    }

    ST("ProSelectionAsmcomppathGet", ProSelectionAsmcomppathGet(model_sel, &cp));
    ST("ProArrayAlloc2", ProArrayAlloc(1, sizeof(ProFeatureCreateOptions), 1, (ProArray *)&opts));
    opts[0] = PRO_FEAT_CR_DEFINE_MISS_ELEMS;
    e = ProFeatureWithoptionsRedefine(&cp, &feature, created, opts, PRO_REGEN_NO_FLAGS, &errors);
    ProArrayFree((ProArray *)&opts); opts = 0;
    ST("ProFeatureWithoptionsRedefine", e);
    {
        ProError rg = ProSolidRegenerate((ProSolid)g_model, PRO_REGEN_NO_FLAGS);
        ProFeatStatus fs = PRO_FEAT_INVALID; ProError se2 = ProFeatureStatusGet(&feature, &fs);
        _snprintf(g_info, sizeof g_info, "feature id=%d status=%d (get=%d) regen=%d", feature.id, (int)fs, (int)se2, (int)rg);
        g_info[sizeof g_info - 1] = 0;
    }

cleanup:
    if (created) ProFeatureElemtreeFree(&feature, created);
    if (tree) ProElementFree(&tree);
    if (rc != 0) { _snprintf(g_err, sizeof g_err, "cb_block failed at '%s': %d", stage_fail, rc); g_err[sizeof g_err - 1] = 0; }
    return rc;
}
/* Extruded cylinder: circle centre (cx,cy) radius r in sketch coordinates of datum <plane>. flip=1 extrudes to the other side. */
CB_API int cb_cylinder(const char *plane, const char *orient, const char *refA, const char *refB, double cx, double cy, double r, double height, double elevation, int flip)
{
    int rc;
    g_circle = 1; g_flip = flip;
    rc = cb_block(plane, orient, refA, refB, cx, cy, r, 0, height, elevation);
    g_circle = 0; g_flip = 0;
    return rc;
}
/* Rounded rectangle prism, optionally a ring (hasInner: inner rounded rectangle [ix0,ix1]x[iy0,iy1] radius ir is a hole through the extrusion). */
CB_API int cb_prism(const char *plane, const char *orient, const char *refA, const char *refB, double x0, double y0, double x1, double y1, double cornerR,
                    int hasInner, double ix0, double iy0, double ix1, double iy1, double innerR, double height, double elevation)
{
    int rc;
    g_corner = cornerR; g_inner = hasInner; g_ix0 = ix0; g_iy0 = iy0; g_ix1 = ix1; g_iy1 = iy1; g_ir = innerR;
    rc = cb_block(plane, orient, refA, refB, x0, y0, x1, y1, height, elevation);
    g_corner = 0; g_inner = 0;
    return rc;
}
/* Solid of revolution (360 deg). xy = n points (x0,y0,x1,y1,...) of a closed profile in sketch coordinates of datum <plane> (offset by 'offset' along its normal);
   the last segment (point n-1 back to point 0) must lie on the axis, the vertical line x = axisX from axisY0 to axisY1. */
CB_API int cb_revolve(const char *plane, const char *orient, const char *refA, const char *refB, const double *xy, int n, double axisX, double axisY0, double axisY1, double offset)
{
    int rc;
    g_rev_pts = xy; g_rev_n = n; g_rev_axis = axisX; g_rev_y0 = axisY0; g_rev_y1 = axisY1;
    rc = cb_block(plane, orient, refA, refB, 0, 0, 0, 0, 0, offset);
    g_rev_n = 0; g_rev_pts = 0;
    return rc;
}
CB_API int cb_end(void)
{
    int g = guard(); ProError e; if (g) return g;
    e = ProEngineerEnd(); g_started = 0;
    return e == PRO_TK_NO_ERROR ? 0 : fail("ProEngineerEnd", (int)e);
}
