/* A DLL a fixture module imports from its own folder (sim-fixture-sibling.dll, sim-fixture-delay.dll): what a module
 * that isn't one self-contained file looks like. Apex must refuse such a module before loading it. */
#include <stdint.h>

__declspec(dllexport) uint32_t sim_helper_value(void) { return 0; }
