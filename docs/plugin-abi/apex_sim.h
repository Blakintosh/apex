/* Apex preview-simulator interface, ABI version 1.
 *
 * What it is: the one native hook an Apex extension may ship (docs/schema-extensions-design.md,
 * docs/extensions.md). The module computes motion for a preview; Apex draws it. The module never sees Apex's memory, never
 * calls back into Apex and never writes files through Apex.
 *
 * Shape rules (so the module can move out of process later without changing):
 *   - plain C, flat structs, no callbacks, no pointers kept past a call, all strings UTF-8 and NUL-terminated;
 *   - every struct starts with `size` (bytes, filled by the caller); a reader never reads past the size it was given,
 *     and a writer never writes past it, so structs grow at the end and old modules and old Apex keep working;
 *   - Apex always passes a size at least the ABI 1 sizeof of each struct, so a module may read and write every ABI 1
 *     member without checking (a larger size only means a newer Apex with more members after them);
 *   - the ABI version is a major number: Apex refuses a module whose apex_sim_abi_version() it doesn't know, and one
 *     whose ApexSimInfo.abi differs from what apex_sim_abi_version() returned;
 *   - one apex_sim object per previewed weapon, and several may be alive at once (two previews, or a new one made
 *     before the old one is destroyed): keep no per-weapon state in globals;
 *   - Apex zero-fills every struct it passes out and sets its size before each call, and passes no NULL pointer
 *     except kv when count is 0;
 *   - exported with C linkage, __cdecl, 64-bit Windows.
 *
 * Process rules (the module runs inside Apex, so these keep Apex working):
 *   - no exception may leave a call: catch every C++ exception and never let an SEH exception (an access violation
 *     included) escape; an exception crossing into Apex ends it;
 *   - leave the floating-point control state as you found it on return from every call (MXCSR and the x87 control
 *     word: rounding, precision, exception masks, flush-to-zero, denormals-are-zero). Apex's renderer must stay
 *     bit-exact with APE's: Apex checks MXCSR after every call, puts it back, and counts a change as a failed call;
 *   - start no thread that outlives the call that started it: Apex unloads the module (FreeLibrary) when it closes,
 *     and a thread still running its code then crashes Apex;
 *   - load no other DLL at run time (LoadLibrary): the user agreed to load one file, by its hash. Apex refuses a module
 *     whose import table names any DLL Windows doesn't supply, but can't see what it loads by itself;
 *   - never write GDTs or .gdtx files: only Apex's save path does, when the user saves.
 *
 * Time (Apex can't interrupt a call, so a module that doesn't return hangs Apex):
 *   - every call runs on Apex's UI thread, the one that draws its window;
 *   - apex_sim_step runs once per display frame while something moves and must return in about a millisecond;
 *   - apex_sim_create runs on every edit to the weapon's values (150 ms after the last), apex_sim_destroy when a preview
 *     closes or is remade: both must return quickly, well under a second;
 *   - a module must never block: no waiting on locks, files, the network or other threads;
 *   - Apex counts a step over 50 ms, or a create over a second, with the failed steps (ten in a row turn the module
 *     off until Apex restarts) and names the module to the user once.
 *
 * The manifest names the module file (`simulator.module`, relative to the extension folder).
 */
#ifndef APEX_SIM_H
#define APEX_SIM_H

#include <stdint.h>

#ifdef __cplusplus
extern "C" {
#endif

#ifdef _WIN32
#define APEX_SIM_API __declspec(dllexport)
#else
#define APEX_SIM_API
#endif

#define APEX_SIM_ABI_VERSION 1u

/* One effective extension value of the previewed weapon, exactly as Apex shows it (raw: backslashes literal).
 * Numbered groups arrive as their individual keys, in row order, e.g. wtKick1, wtKick2... An array element, so it has
 * no size field: its layout is fixed for ABI 1. */
typedef struct ApexSimKv
{
	const char *key;
	const char *value;
} ApexSimKv;

/* What the module is. Filled by apex_sim_info. */
typedef struct ApexSimInfo
{
	uint32_t size;       /* in: sizeof(ApexSimInfo) of the caller */
	uint32_t abi;        /* out: APEX_SIM_ABI_VERSION the module was built against */
	char id[64];         /* out: extension id, matches the manifest */
	char version[32];    /* out: module version, shown in logs only */
} ApexSimInfo;

/* Per-step input. */
typedef struct ApexSimInput
{
	uint32_t size;
	float dt;            /* seconds since the previous step, > 0 */
	float ads;           /* 0 = hip, 1 = fully aimed down sights */
	uint32_t shots;      /* rounds fired at the start of this step (0 or more) */
	uint32_t reserved;
} ApexSimInput;

/* Which parts of the motion this module computed for the weapon (bits of ApexSimOutput.supported).
 * A weapon whose motion the module cannot reproduce exactly says so here instead of guessing. */
enum
{
	APEX_SIM_VIEW_ANGLES = 1u << 0,
	APEX_SIM_VIEW_ORIGIN = 1u << 1,
	APEX_SIM_GUN_ANGLES = 1u << 2,
	APEX_SIM_GUN_ORIGIN = 1u << 3,
};

/* Per-step output, all in the engine's viewmodel space (degrees, inches), added to the pose Apex already draws:
 * view* moves the camera (the whole viewmodel and the world move with it), gun* moves only the viewmodel.
 *
 * Sign conventions are the engine's: negative pitch is up, positive yaw is left;
 * viewOrigin is in view axes (x forward, y left, z up); gunOrigin likewise in viewmodel axes. */
typedef struct ApexSimOutput
{
	uint32_t size;
	uint32_t supported;      /* APEX_SIM_* bits the module computed this step */
	float viewAngles[3];     /* pitch, yaw, roll */
	float viewOrigin[3];
	float gunAngles[3];
	float gunOrigin[3];
	char note[160];          /* "" or a short plain-language reason for unsupported bits, shown beside the preview */
} ApexSimOutput;

/* The ABI version this module implements. Called first; nothing else is called if Apex does not know it. */
APEX_SIM_API uint32_t apex_sim_abi_version(void);

/* Returns 0 on success; anything else and Apex uses nothing more from the module. `abi` must equal what
 * apex_sim_abi_version() returned, and `id` the manifest id exactly: Apex compares the bytes up to the NUL as they are,
 * without trimming or changing case ("weapon-tech " is not "weapon-tech"). */
APEX_SIM_API int apex_sim_info(ApexSimInfo *out);

/* Create the simulation for one weapon from its effective extension values. Returns NULL and writes a short plain
 * message into err (capacity errCap, its NUL included) when the weapon can't be simulated at all. When it returns
 * non-NULL, Apex ignores err: the module may leave it untouched. */
APEX_SIM_API void *apex_sim_create(const ApexSimKv *kv, uint32_t count, char *err, uint32_t errCap);

/* Back to the state right after create (a fresh burst): shot counter, springs, noise sequence. */
APEX_SIM_API void apex_sim_reset(void *sim);

/* Advance by in->dt and report the motion. Returns 0 on success, non-zero with out->note set on failure. */
APEX_SIM_API int apex_sim_step(void *sim, const ApexSimInput *in, ApexSimOutput *out);

APEX_SIM_API void apex_sim_destroy(void *sim);

#ifdef __cplusplus
}
#endif

#endif
