/* A preview-simulator module for Apex.Shots (docs/plugin-abi/apex_sim.h). Not a simulator: every output is a plain
 * function of the keys and inputs, so a check can assert exact values, and keys ask for each failure Apex must survive.
 *
 * Keys (any order; others are counted and ignored):
 *   fxGain   number, default 1
 *   fxMode   "", create-fail, step-fail, unsupported, overrun, nan, fpu (a step leaves flush-to-zero on), slow-step
 *            (a step takes 80 ms), slow-create (create takes 1.1 s), create-overrun (create writes past err and
 *            still returns a simulation)
 *   fxNote   text, copied to notes and the create error
 * Outputs after each successful step (t = total dt since reset, n = steps since reset, s = shots since reset):
 *   viewAngles = { gain * t, ads, s }        viewOrigin = { keys, n, dt }
 *   gunAngles  = { 2 * gain, first byte of the last key, length of its value }   gunOrigin = { -gain, -t, n + s }
 *
 * Variants (build.ps1): FIX_ABI (another ABI version), FIX_INFO_ABI (apex_sim_info reports another version than
 * apex_sim_abi_version), FIX_ID (another id), FIX_NO_STEP (apex_sim_step missing), FIX_HELPER (imports sim-helper.dll,
 * which sits beside it; with FIX_DELAY it is delay-loaded).
 * No C runtime (/NODEFAULTLIB, no entry point): a few KB, and nothing to install beside it. */
#include "../../../docs/plugin-abi/apex_sim.h"
#include <windows.h>
#include <xmmintrin.h>

#ifndef FIX_ABI
#define FIX_ABI APEX_SIM_ABI_VERSION
#endif
#ifndef FIX_INFO_ABI
#define FIX_INFO_ABI FIX_ABI
#endif
#ifndef FIX_ID
#define FIX_ID "sim-fixture"
#endif

#ifdef FIX_HELPER
__declspec(dllimport) uint32_t sim_helper_value(void);
#endif

int _fltused = 1;

/* What the compiler calls for struct copies and zeroing, since there's no CRT to supply them. */
#pragma function(memset, memcpy)
void *memset(void *d, int c, size_t n)
{
	unsigned char *p = (unsigned char *)d;
	while (n--)
		*p++ = (unsigned char)c;
	return d;
}

void *memcpy(void *d, const void *s, size_t n)
{
	unsigned char *p = (unsigned char *)d;
	const unsigned char *q = (const unsigned char *)s;
	while (n--)
		*p++ = *q++;
	return d;
}

enum
{
	MODE_NONE, MODE_CREATE_FAIL, MODE_STEP_FAIL, MODE_UNSUPPORTED, MODE_OVERRUN, MODE_NAN, MODE_FPU, MODE_SLOW_STEP,
	MODE_SLOW_CREATE, MODE_CREATE_OVERRUN
};

typedef struct Sim
{
	float gain, t;
	uint32_t steps, shots, keys, mode;
	float lastKeyByte, lastValueLength;
	char note[96];
} Sim;

static volatile LONG g_live, g_steps;

static int same(const char *a, const char *b)
{
	while (*a && *a == *b)
		a++, b++;
	return *a == *b;
}

static uint32_t length(const char *s)
{
	uint32_t n = 0;
	while (s[n])
		n++;
	return n;
}

static void copy(char *dst, uint32_t cap, const char *src)
{
	uint32_t i = 0;
	if (!cap)
		return;
	for (; src[i] && i + 1 < cap; i++)
		dst[i] = src[i];
	dst[i] = 0;
}

/* Enough of strtof for "2", "-0.5", "12.25". */
static float number(const char *s)
{
	float v = 0, scale = 1, sign = 1;
	if (*s == '-')
		sign = -1, s++;
	for (; *s >= '0' && *s <= '9'; s++)
		v = v * 10 + (float)(*s - '0');
	if (*s == '.')
		for (s++; *s >= '0' && *s <= '9'; s++)
			scale /= 10, v += (float)(*s - '0') * scale;
	return sign * v;
}

APEX_SIM_API uint32_t apex_sim_abi_version(void) { return FIX_ABI; }

#ifdef FIX_HELPER
/* Not part of the ABI and never called: it only puts sim-helper.dll in the import table Apex reads. */
APEX_SIM_API uint32_t fixture_helper(void) { return sim_helper_value(); }
#endif

#ifdef FIX_DELAY
/* Enough of the delay-load helper to link without a C runtime; nothing calls through it. */
FARPROC WINAPI __delayLoadHelper2(const void *descriptor, FARPROC *slot)
{
	(void)descriptor;
	(void)slot;
	return 0;
}
#endif

APEX_SIM_API int apex_sim_info(ApexSimInfo *out)
{
	if (out->size != sizeof(ApexSimInfo))
		return 1;
	out->abi = FIX_INFO_ABI;
	copy(out->id, sizeof out->id, FIX_ID);
	copy(out->version, sizeof out->version, "fixture-1");
	return 0;
}

APEX_SIM_API void *apex_sim_create(const ApexSimKv *kv, uint32_t count, char *err, uint32_t errCap)
{
	Sim *sim;
	uint32_t i;
	Sim s = { 0 };
	s.gain = 1;
	s.keys = count;
	for (i = 0; i < count; i++)
	{
		if (same(kv[i].key, "fxGain"))
			s.gain = number(kv[i].value);
		else if (same(kv[i].key, "fxNote"))
			copy(s.note, sizeof s.note, kv[i].value);
		else if (same(kv[i].key, "fxMode"))
			s.mode = same(kv[i].value, "create-fail") ? MODE_CREATE_FAIL
				: same(kv[i].value, "step-fail") ? MODE_STEP_FAIL
				: same(kv[i].value, "unsupported") ? MODE_UNSUPPORTED
				: same(kv[i].value, "overrun") ? MODE_OVERRUN
				: same(kv[i].value, "nan") ? MODE_NAN
				: same(kv[i].value, "fpu") ? MODE_FPU
				: same(kv[i].value, "slow-step") ? MODE_SLOW_STEP
				: same(kv[i].value, "slow-create") ? MODE_SLOW_CREATE
				: same(kv[i].value, "create-overrun") ? MODE_CREATE_OVERRUN : MODE_NONE;
	}
	if (count)
	{
		s.lastKeyByte = (float)(unsigned char)kv[count - 1].key[0];
		s.lastValueLength = (float)length(kv[count - 1].value);
	}
	if (s.mode == MODE_CREATE_FAIL)
	{
		copy(err, errCap, s.note[0] ? s.note : "this weapon asks the fixture to fail");
		return 0;
	}
	if (s.mode == MODE_SLOW_CREATE)
		Sleep(1100);
	if (s.mode == MODE_CREATE_OVERRUN)
		memset(err + errCap, 0x5a, 8);
	sim = (Sim *)HeapAlloc(GetProcessHeap(), 0, sizeof(Sim));
	if (!sim)
	{
		copy(err, errCap, "out of memory");
		return 0;
	}
	*sim = s;
	InterlockedIncrement(&g_live);
	return sim;
}

APEX_SIM_API void apex_sim_reset(void *p)
{
	Sim *sim = (Sim *)p;
	sim->t = 0;
	sim->steps = 0;
	sim->shots = 0;
}

#ifndef FIX_NO_STEP
APEX_SIM_API int apex_sim_step(void *p, const ApexSimInput *in, ApexSimOutput *out)
{
	Sim *sim = (Sim *)p;
	InterlockedIncrement(&g_steps);
	if (in->size != sizeof(ApexSimInput) || out->size != sizeof(ApexSimOutput))
	{
		copy(out->note, sizeof out->note, "the fixture was given a struct of the wrong size");
		return 1;
	}
	if (sim->mode == MODE_STEP_FAIL)
	{
		copy(out->note, sizeof out->note, sim->note[0] ? sim->note : "the fixture fails every step");
		return 2;
	}
	sim->t += in->dt;
	sim->steps++;
	sim->shots += in->shots;

	out->supported = APEX_SIM_VIEW_ANGLES | APEX_SIM_VIEW_ORIGIN | APEX_SIM_GUN_ANGLES | APEX_SIM_GUN_ORIGIN;
	out->viewAngles[0] = sim->gain * sim->t;
	out->viewAngles[1] = in->ads;
	out->viewAngles[2] = (float)sim->shots;
	out->viewOrigin[0] = (float)sim->keys;
	out->viewOrigin[1] = (float)sim->steps;
	out->viewOrigin[2] = in->dt;
	out->gunAngles[0] = 2 * sim->gain;
	out->gunAngles[1] = sim->lastKeyByte;
	out->gunAngles[2] = sim->lastValueLength;
	out->gunOrigin[0] = -sim->gain;
	out->gunOrigin[1] = -sim->t;
	out->gunOrigin[2] = (float)(sim->steps + sim->shots);
	if (sim->mode == MODE_UNSUPPORTED)
	{
		out->supported = APEX_SIM_VIEW_ANGLES;
		copy(out->note, sizeof out->note, sim->note[0] ? sim->note : "only the view angles are simulated");
	}
	else if (sim->mode == MODE_NAN)
	{
		uint32_t bits = 0x7fc00000u;
		out->gunOrigin[1] = *(float *)&bits;
	}
	else if (sim->mode == MODE_OVERRUN)
	{
		/* Past the size Apex gave: Apex's guard bytes catch it. */
		memset((char *)out + out->size, 0x5a, 8);
	}
	else if (sim->mode == MODE_FPU)
		/* Flush-to-zero and denormals-are-zero left on: the header forbids it, Apex restores and counts it. */
		_mm_setcsr(_mm_getcsr() | 0x8040);
	else if (sim->mode == MODE_SLOW_STEP)
		Sleep(80);
	return 0;
}
#endif

APEX_SIM_API void apex_sim_destroy(void *p)
{
	HeapFree(GetProcessHeap(), 0, p);
	InterlockedDecrement(&g_live);
}

/* Not part of the ABI: how many simulations are alive and how many steps reached the module, for leak and
 * thread-guard checks. */
APEX_SIM_API int32_t fixture_live(void) { return g_live; }
APEX_SIM_API int32_t fixture_steps(void) { return g_steps; }
