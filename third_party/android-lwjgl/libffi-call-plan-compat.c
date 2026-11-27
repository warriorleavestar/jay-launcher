#include <ffi.h>
#include <stdlib.h>

/*
 * LWJGL 3.4.3 exposes libffi's reusable-call-plan API. libffi 3.5.2 contains
 * every other ABI entry point it uses, but predates this small API. Android
 * does not need an architecture-specific fast path, so Jay supplies the same
 * observable semantics with a plan that retains the prepared CIF and delegates
 * invocation to ffi_call.
 */
struct ffi_call_plan {
    ffi_cif *cif;
};

ffi_call_plan *ffi_call_plan_alloc(ffi_cif *cif) {
    if (cif == NULL) return NULL;
    ffi_call_plan *plan = malloc(sizeof(*plan));
    if (plan != NULL) plan->cif = cif;
    return plan;
}

void ffi_call_plan_invoke(
    ffi_call_plan *plan,
    void (*function)(void),
    void *return_value,
    void **argument_values) {
    if (plan != NULL) ffi_call(plan->cif, function, return_value, argument_values);
}

void ffi_call_plan_free(ffi_call_plan *plan) {
    free(plan);
}

size_t ffi_call_plan_size(ffi_call_plan *plan) {
    return plan == NULL ? 0 : sizeof(*plan);
}
