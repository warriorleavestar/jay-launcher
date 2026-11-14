#include <jni.h>
#include <android/log.h>
#include <android/native_window.h>
#include <android/native_window_jni.h>
#include <dlfcn.h>
#include <pthread.h>
#include <stdatomic.h>
#include <stdio.h>
#include <stdint.h>
#include <stdlib.h>
#include <string.h>

#define JAY_JVM_ABI_VERSION 11
#define JAY_ACTION_COUNT 12
#define JAY_TEXT_CAPACITY 64
#define JAY_KEY_CAPACITY 16

typedef jint (*jay_create_jvm_fn)(JavaVM **, void **, void *);

typedef struct {
    float move_x;
    float move_y;
    float look_x;
    float look_y;
    float pointer_x;
    float pointer_y;
    uint32_t pointer_clicks;
    uint32_t actions;
    uint32_t action_taps;
    uint32_t text_count;
    uint32_t key_count;
    uint32_t text[JAY_TEXT_CAPACITY];
    int32_t keys[JAY_KEY_CAPACITY];
    uint64_t pointer_generation;
    uint64_t generation;
} jay_input_snapshot;

static void *g_jvm_library;
static jay_create_jvm_fn g_create_jvm;
static JavaVM *g_vm;
static jclass g_input_bridge_class;
static jmethodID g_dispatch_pointer;
static jmethodID g_dispatch_mouse_button;
static jmethodID g_dispatch_key;
static jmethodID g_dispatch_scroll;
static jmethodID g_dispatch_char;
static jmethodID g_dispatch_look;
static jmethodID g_is_block_target;
static _Atomic int g_input_bridge_error_logged;
static char g_load_error[256];
static char g_runtime_error[2048];
static pthread_mutex_t g_surface_mutex = PTHREAD_MUTEX_INITIALIZER;
static ANativeWindow *g_surface;
static _Atomic uint64_t g_surface_generation;
static _Atomic float g_move_x;
static _Atomic float g_move_y;
static _Atomic float g_look_x;
static _Atomic float g_look_y;
static _Atomic float g_pointer_x;
static _Atomic float g_pointer_y;
static _Atomic uint32_t g_pointer_clicks;
static _Atomic uint64_t g_pointer_generation;
static _Atomic uint32_t g_actions;
static _Atomic uint32_t g_action_taps;
static _Atomic int g_gameplay_mode;
static _Atomic int g_block_target;
static _Atomic uint64_t g_generation;
static pthread_mutex_t g_event_mutex = PTHREAD_MUTEX_INITIALIZER;
static uint32_t g_text[JAY_TEXT_CAPACITY];
static uint32_t g_text_count;
static int32_t g_keys[JAY_KEY_CAPACITY];
static uint32_t g_key_count;

__attribute__((visibility("default")))
int jay_jvm_abi_version(void) {
    return JAY_JVM_ABI_VERSION;
}

__attribute__((visibility("default")))
void jay_gameplay_mode_set(int active) {
    atomic_store(&g_gameplay_mode, active ? 1 : 0);
}

__attribute__((visibility("default")))
int jay_gameplay_mode_get(void) {
    return atomic_load(&g_gameplay_mode);
}

__attribute__((visibility("default")))
const char *jay_jvm_load(const char *libjvm_path) {
    if (g_create_jvm != NULL) return NULL;
    if (libjvm_path == NULL || libjvm_path[0] == '\0') return "libjvm path is empty";
    g_jvm_library = dlopen(libjvm_path, RTLD_NOW | RTLD_GLOBAL);
    if (g_jvm_library == NULL) return dlerror();
    dlerror();
    g_create_jvm = (jay_create_jvm_fn)dlsym(g_jvm_library, "JNI_CreateJavaVM");
    if (g_create_jvm == NULL) {
        const char *error = dlerror();
        snprintf(g_load_error, sizeof(g_load_error), "%s",
                 error == NULL ? "JNI_CreateJavaVM was not found" : error);
        dlclose(g_jvm_library);
        g_jvm_library = NULL;
        return g_load_error;
    }
    return NULL;
}

__attribute__((visibility("default")))
const char *jay_jvm_preload(const char *library_path) {
    if (library_path == NULL || library_path[0] == '\0') return "library path is empty";
    dlerror();
    if (dlopen(library_path, RTLD_NOW | RTLD_GLOBAL) != NULL) return NULL;
    const char *error = dlerror();
    snprintf(g_load_error, sizeof(g_load_error), "%s",
             error == NULL ? "dlopen failed without an error" : error);
    return g_load_error;
}

__attribute__((visibility("default")))
const char *jay_jvm_preload_local(const char *library_path) {
    if (library_path == NULL || library_path[0] == '\0') return "library path is empty";
    dlerror();
    if (dlopen(library_path, RTLD_NOW | RTLD_LOCAL) != NULL) return NULL;
    const char *error = dlerror();
    snprintf(g_load_error, sizeof(g_load_error), "%s",
             error == NULL ? "local dlopen failed without an error" : error);
    return g_load_error;
}

__attribute__((visibility("default")))
const char *jay_setenv(const char *name, const char *value) {
    if (name == NULL || name[0] == '\0') return "environment variable name is empty";
    if (value == NULL) return "environment variable value is null";
    if (setenv(name, value, 1) == 0) return NULL;
    snprintf(g_load_error, sizeof(g_load_error), "setenv(%s) failed", name);
    return g_load_error;
}

__attribute__((visibility("default")))
int jay_jvm_create(
    const char *const *options,
    int option_count,
    JavaVM **vm,
    JNIEnv **env) {
    if (g_create_jvm == NULL || vm == NULL || env == NULL || option_count < 0 ||
        (option_count > 0 && options == NULL)) return JNI_ERR;
    JavaVMOption *jvm_options = NULL;
    if (option_count > 0) {
        jvm_options = calloc((size_t)option_count, sizeof(JavaVMOption));
        if (jvm_options == NULL) return JNI_ENOMEM;
        for (int i = 0; i < option_count; i++)
            jvm_options[i].optionString = (char *)options[i];
    }
    JavaVMInitArgs args = {
        .version = JNI_VERSION_1_6,
        .nOptions = option_count,
        .options = jvm_options,
        .ignoreUnrecognized = JNI_FALSE,
    };
    int result = g_create_jvm(vm, (void **)env, &args);
    free(jvm_options);
    if (result == JNI_OK) g_vm = *vm;
    return result;
}

static int jay_capture_exception(JNIEnv *env, const char *stage) {
    if (!(*env)->ExceptionCheck(env)) return 0;
    jthrowable exception = (*env)->ExceptionOccurred(env);
    (*env)->ExceptionDescribe(env);
    (*env)->ExceptionClear(env);
    snprintf(g_runtime_error, sizeof(g_runtime_error), "%s failed with a Java exception", stage);
    if (exception != NULL) {
        jclass exception_class = (*env)->GetObjectClass(env, exception);
        jmethodID to_string = exception_class == NULL ? NULL
            : (*env)->GetMethodID(env, exception_class, "toString", "()Ljava/lang/String;");
        if (!(*env)->ExceptionCheck(env) && to_string != NULL) {
            jstring message = (jstring)(*env)->CallObjectMethod(env, exception, to_string);
            if (!(*env)->ExceptionCheck(env) && message != NULL) {
                const char *text = (*env)->GetStringUTFChars(env, message, NULL);
                if (text != NULL) {
                    snprintf(g_runtime_error, sizeof(g_runtime_error), "%s: %s", stage, text);
                    (*env)->ReleaseStringUTFChars(env, message, text);
                }
                (*env)->DeleteLocalRef(env, message);
            }
        }
        if ((*env)->ExceptionCheck(env)) (*env)->ExceptionClear(env);
        if (exception_class != NULL) (*env)->DeleteLocalRef(env, exception_class);
        (*env)->DeleteLocalRef(env, exception);
    }
    __android_log_print(ANDROID_LOG_ERROR, "JayJvm", "%s", g_runtime_error);
    return -1;
}

__attribute__((visibility("default")))
const char *jay_jvm_last_error(void) {
    return g_runtime_error[0] == '\0' ? NULL : g_runtime_error;
}

__attribute__((visibility("default")))
int jay_jvm_run_main(JNIEnv *env, const char *class_name, int argc, const char *const *argv) {
    if (env == NULL || class_name == NULL || argc < 0 || (argc > 0 && argv == NULL)) return JNI_ERR;
    g_runtime_error[0] = '\0';
    jclass main_class = (*env)->FindClass(env, class_name);
    if (main_class == NULL) {
        jay_capture_exception(env, "FindClass(main)");
        return JNI_ERR;
    }
    jmethodID main_method = (*env)->GetStaticMethodID(env, main_class, "main", "([Ljava/lang/String;)V");
    if (main_method == NULL) {
        jay_capture_exception(env, "GetStaticMethodID(main)");
        return JNI_ERR;
    }
    g_input_bridge_class = (jclass)(*env)->NewGlobalRef(env, main_class);
    if ((*env)->ExceptionCheck(env)) {
        if (!atomic_exchange(&g_input_bridge_error_logged, 1))
            (*env)->ExceptionDescribe(env);
        (*env)->ExceptionClear(env);
        g_input_bridge_class = NULL;
    }
    if (g_input_bridge_class != NULL) {
#define JAY_INPUT_METHOD(target, name, signature) do { \
        target = (*env)->GetStaticMethodID(env, g_input_bridge_class, name, signature); \
        if ((*env)->ExceptionCheck(env)) { \
            if (!atomic_exchange(&g_input_bridge_error_logged, 1)) \
                (*env)->ExceptionDescribe(env); \
            (*env)->ExceptionClear(env); \
            target = NULL; \
        } \
    } while (0)
        JAY_INPUT_METHOD(g_dispatch_pointer, "dispatchPointer", "(FFI)Z");
        JAY_INPUT_METHOD(g_dispatch_mouse_button, "dispatchMouseButton", "(II)Z");
        JAY_INPUT_METHOD(g_dispatch_key, "dispatchKey", "(II)Z");
        JAY_INPUT_METHOD(g_dispatch_scroll, "dispatchScroll", "(DD)Z");
        JAY_INPUT_METHOD(g_dispatch_char, "dispatchChar", "(I)Z");
        JAY_INPUT_METHOD(g_dispatch_look, "dispatchLook", "(FF)Z");
        JAY_INPUT_METHOD(g_is_block_target, "isBlockTarget", "()Z");
#undef JAY_INPUT_METHOD
    }
    jclass string_class = (*env)->FindClass(env, "java/lang/String");
    if (string_class == NULL) {
        jay_capture_exception(env, "FindClass(String)");
        return JNI_ERR;
    }
    jobjectArray arguments = (*env)->NewObjectArray(env, argc, string_class, NULL);
    if (arguments == NULL || jay_capture_exception(env, "NewObjectArray(arguments)") != 0) return JNI_ENOMEM;
    for (int i = 0; i < argc; i++) {
        jstring value = (*env)->NewStringUTF(env, argv[i] == NULL ? "" : argv[i]);
        if (value == NULL || jay_capture_exception(env, "NewStringUTF(argument)") != 0) return JNI_ENOMEM;
        (*env)->SetObjectArrayElement(env, arguments, i, value);
        (*env)->DeleteLocalRef(env, value);
        if (jay_capture_exception(env, "SetObjectArrayElement(argument)") != 0) return JNI_ERR;
    }
    (*env)->CallStaticVoidMethod(env, main_class, main_method, arguments);
    return jay_capture_exception(env, "CallStaticVoidMethod(main)") == 0 ? JNI_OK : JNI_ERR;
}

static JNIEnv *jay_input_environment(int *attached) {
    *attached = 0;
    if (g_vm == NULL || g_input_bridge_class == NULL) return NULL;
    JNIEnv *env = NULL;
    jint result = (*g_vm)->GetEnv(g_vm, (void **)&env, JNI_VERSION_1_6);
    if (result == JNI_OK) return env;
    if (result != JNI_EDETACHED ||
        (*g_vm)->AttachCurrentThread(g_vm, &env, NULL) != JNI_OK)
        return NULL;
    *attached = 1;
    return env;
}

static int jay_finish_input_call(JNIEnv *env, int attached, jboolean delivered) {
    int success = delivered == JNI_TRUE;
    if (env != NULL && (*env)->ExceptionCheck(env)) {
        success = 0;
        if (!atomic_exchange(&g_input_bridge_error_logged, 1))
            (*env)->ExceptionDescribe(env);
        (*env)->ExceptionClear(env);
    }
    if (attached && g_vm != NULL) (*g_vm)->DetachCurrentThread(g_vm);
    return success;
}

__attribute__((visibility("default")))
int jay_java_pointer(float x, float y, uint32_t clicks) {
    int attached;
    JNIEnv *env = jay_input_environment(&attached);
    if (env == NULL || g_dispatch_pointer == NULL) return 0;
    jboolean delivered = (*env)->CallStaticBooleanMethod(env, g_input_bridge_class,
        g_dispatch_pointer, (jfloat)x, (jfloat)y, (jint)clicks);
    return jay_finish_input_call(env, attached, delivered);
}

__attribute__((visibility("default")))
int jay_block_target_get(void) {
    return atomic_load(&g_block_target);
}

static void jay_refresh_block_target(void) {
    int attached;
    JNIEnv *env = jay_input_environment(&attached);
    if (env == NULL || g_is_block_target == NULL) {
        atomic_store(&g_block_target, 0);
        return;
    }
    jboolean targeted = (*env)->CallStaticBooleanMethod(env, g_input_bridge_class,
        g_is_block_target);
    if ((*env)->ExceptionCheck(env)) {
        if (!atomic_exchange(&g_input_bridge_error_logged, 1))
            (*env)->ExceptionDescribe(env);
        (*env)->ExceptionClear(env);
        atomic_store(&g_block_target, 0);
    } else {
        atomic_store(&g_block_target, targeted == JNI_TRUE ? 1 : 0);
    }
    if (attached && g_vm != NULL) (*g_vm)->DetachCurrentThread(g_vm);
}

__attribute__((visibility("default")))
int jay_java_mouse_button(int button, int action) {
    int attached;
    JNIEnv *env = jay_input_environment(&attached);
    if (env == NULL || g_dispatch_mouse_button == NULL) return 0;
    jboolean delivered = (*env)->CallStaticBooleanMethod(env, g_input_bridge_class,
        g_dispatch_mouse_button, (jint)button, (jint)action);
    return jay_finish_input_call(env, attached, delivered);
}

__attribute__((visibility("default")))
int jay_java_key(int key, int action) {
    int attached;
    JNIEnv *env = jay_input_environment(&attached);
    if (env == NULL || g_dispatch_key == NULL) return 0;
    jboolean delivered = (*env)->CallStaticBooleanMethod(env, g_input_bridge_class,
        g_dispatch_key, (jint)key, (jint)action);
    return jay_finish_input_call(env, attached, delivered);
}

__attribute__((visibility("default")))
int jay_java_scroll(double x, double y) {
    int attached;
    JNIEnv *env = jay_input_environment(&attached);
    if (env == NULL || g_dispatch_scroll == NULL) return 0;
    jboolean delivered = (*env)->CallStaticBooleanMethod(env, g_input_bridge_class,
        g_dispatch_scroll, (jdouble)x, (jdouble)y);
    return jay_finish_input_call(env, attached, delivered);
}

__attribute__((visibility("default")))
int jay_java_char(uint32_t codepoint) {
    int attached;
    JNIEnv *env = jay_input_environment(&attached);
    if (env == NULL || g_dispatch_char == NULL) return 0;
    jboolean delivered = (*env)->CallStaticBooleanMethod(env, g_input_bridge_class,
        g_dispatch_char, (jint)codepoint);
    return jay_finish_input_call(env, attached, delivered);
}

__attribute__((visibility("default")))
int jay_java_look(float x, float y) {
    int attached;
    JNIEnv *env = jay_input_environment(&attached);
    if (env == NULL || g_dispatch_look == NULL) return 0;
    jboolean delivered = (*env)->CallStaticBooleanMethod(env, g_input_bridge_class,
        g_dispatch_look, (jfloat)x, (jfloat)y);
    return jay_finish_input_call(env, attached, delivered);
}

__attribute__((visibility("default")))
void jay_surface_set(JNIEnv *env, jobject surface) {
    ANativeWindow *next = surface == NULL ? NULL : ANativeWindow_fromSurface(env, surface);
    pthread_mutex_lock(&g_surface_mutex);
    ANativeWindow *previous = g_surface;
    g_surface = next;
    atomic_fetch_add(&g_surface_generation, 1);
    pthread_mutex_unlock(&g_surface_mutex);
    if (previous != NULL) ANativeWindow_release(previous);
}

// Changes every time the activity hands over a new surface or takes it away (for example when
// the app goes to the background and comes back). GLFW compares it on every event poll and
// moves its EGL window surface to the current Android surface.
__attribute__((visibility("default")))
uint64_t jay_surface_generation(void) {
    return atomic_load(&g_surface_generation);
}

__attribute__((visibility("default")))
ANativeWindow *jay_surface_acquire(void) {
    pthread_mutex_lock(&g_surface_mutex);
    ANativeWindow *surface = g_surface;
    if (surface != NULL) ANativeWindow_acquire(surface);
    pthread_mutex_unlock(&g_surface_mutex);
    return surface;
}

__attribute__((visibility("default")))
void jay_surface_release(ANativeWindow *surface) {
    if (surface != NULL) ANativeWindow_release(surface);
}

__attribute__((visibility("default")))
void jay_input_set_action(int action, int pressed) {
    if (action < 0 || action >= JAY_ACTION_COUNT) return;
    uint32_t bit = 1u << (uint32_t)action;
    if (pressed) atomic_fetch_or(&g_actions, bit);
    else {
        atomic_fetch_and(&g_actions, ~bit);
        atomic_fetch_or(&g_action_taps, bit);
    }
    atomic_fetch_add(&g_generation, 1);
}

__attribute__((visibility("default")))
void jay_input_set_movement(float x, float y) {
    atomic_store(&g_move_x, x);
    atomic_store(&g_move_y, y);
    atomic_fetch_add(&g_generation, 1);
}

static void jay_atomic_add_float(_Atomic float *target, float value) {
    float expected = atomic_load(target);
    while (!atomic_compare_exchange_weak(target, &expected, expected + value)) {}
}

__attribute__((visibility("default")))
void jay_input_add_look(float x, float y) {
    jay_atomic_add_float(&g_look_x, x);
    jay_atomic_add_float(&g_look_y, y);
    atomic_fetch_add(&g_generation, 1);
}

__attribute__((visibility("default")))
void jay_input_set_pointer(float x, float y, int click) {
    if (x < 0.f) x = 0.f;
    if (x > 1.f) x = 1.f;
    if (y < 0.f) y = 0.f;
    if (y > 1.f) y = 1.f;
    atomic_store(&g_pointer_x, x);
    atomic_store(&g_pointer_y, y);
    if (click) atomic_fetch_add(&g_pointer_clicks, 1);
    atomic_fetch_add(&g_pointer_generation, 1);
    atomic_fetch_add(&g_generation, 1);
}

__attribute__((visibility("default")))
void jay_input_add_codepoint(uint32_t codepoint) {
    if (codepoint == 0 || codepoint > 0x10ffffu || (codepoint >= 0xd800u && codepoint <= 0xdfffu)) return;
    pthread_mutex_lock(&g_event_mutex);
    if (g_text_count < JAY_TEXT_CAPACITY) g_text[g_text_count++] = codepoint;
    atomic_fetch_add(&g_generation, 1);
    pthread_mutex_unlock(&g_event_mutex);
}

__attribute__((visibility("default")))
void jay_input_add_key(int32_t key) {
    pthread_mutex_lock(&g_event_mutex);
    if (g_key_count < JAY_KEY_CAPACITY) g_keys[g_key_count++] = key;
    atomic_fetch_add(&g_generation, 1);
    pthread_mutex_unlock(&g_event_mutex);
}

__attribute__((visibility("default")))
void jay_input_read(jay_input_snapshot *snapshot) {
    if (snapshot == NULL) return;
    // GLFW polls this function on Minecraft's game thread. Refresh the reflected hit result
    // here; the Android drawing thread only reads the atomic cache above.
    jay_refresh_block_target();
    snapshot->move_x = atomic_load(&g_move_x);
    snapshot->move_y = atomic_load(&g_move_y);
    snapshot->look_x = atomic_exchange(&g_look_x, 0.f);
    snapshot->look_y = atomic_exchange(&g_look_y, 0.f);
    snapshot->pointer_x = atomic_load(&g_pointer_x);
    snapshot->pointer_y = atomic_load(&g_pointer_y);
    snapshot->pointer_clicks = atomic_exchange(&g_pointer_clicks, 0);
    snapshot->actions = atomic_load(&g_actions);
    snapshot->action_taps = atomic_exchange(&g_action_taps, 0);
    pthread_mutex_lock(&g_event_mutex);
    snapshot->text_count = g_text_count;
    snapshot->key_count = g_key_count;
    for (uint32_t i = 0; i < g_text_count; i++) snapshot->text[i] = g_text[i];
    for (uint32_t i = 0; i < g_key_count; i++) snapshot->keys[i] = g_keys[i];
    g_text_count = 0;
    g_key_count = 0;
    // Writers store a value first and bump the counters after it. A look delta, click or tap drained
    // above can belong to a write whose bump has not happened yet, and GLFW skips a snapshot whose
    // generation it has seen. Claim a fresh generation whenever something was drained so it is
    // always delivered; an extra bump later only causes one empty poll.
    if (snapshot->pointer_clicks != 0)
        snapshot->pointer_generation = atomic_fetch_add(&g_pointer_generation, 1) + 1;
    else
        snapshot->pointer_generation = atomic_load(&g_pointer_generation);
    if (snapshot->look_x != 0.f || snapshot->look_y != 0.f ||
        snapshot->pointer_clicks != 0 || snapshot->action_taps != 0)
        snapshot->generation = atomic_fetch_add(&g_generation, 1) + 1;
    else
        snapshot->generation = atomic_load(&g_generation);
    pthread_mutex_unlock(&g_event_mutex);
}
