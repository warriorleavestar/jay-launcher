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

#include "jay_menu_queue.h"
#include "jay_scalar_queue.h"

#define JAY_JVM_ABI_VERSION 20
#define JAY_ACTION_COUNT 13
#define JAY_EVENT_CAPACITY 80
#define JAY_MENU_EVENT_CAPACITY 32
#define JAY_EVENT_CHAR 1
#define JAY_EVENT_KEY 2
#define JAY_MENU_EVENT_POINTER 1
#define JAY_MENU_EVENT_SPLIT 2

typedef jint (*jay_create_jvm_fn)(JavaVM **, void **, void *);

typedef struct {
    float move_x;
    float move_y;
    float look_x;
    float look_y;
    float pointer_x;
    float pointer_y;
    uint32_t actions;
    uint32_t action_taps;
    uint32_t event_count;
    uint32_t event_types[JAY_EVENT_CAPACITY];
    int32_t event_values[JAY_EVENT_CAPACITY];
    uint32_t menu_event_count;
    uint32_t menu_event_types[JAY_MENU_EVENT_CAPACITY];
    float menu_event_x[JAY_MENU_EVENT_CAPACITY];
    float menu_event_y[JAY_MENU_EVENT_CAPACITY];
    int32_t menu_event_values[JAY_MENU_EVENT_CAPACITY];
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
static jmethodID g_is_gameplay_controls_active;
static jmethodID g_is_block_target;
static jmethodID g_is_text_input_focused;
static jmethodID g_is_text_input_at;
static jmethodID g_gui_scale;
static jmethodID g_inventory_stack_count;
static jmethodID g_dispatch_inventory_split;
static _Atomic int g_input_bridge_error_logged;
static char g_load_error[256];
static char g_runtime_error[2048];
static pthread_mutex_t g_surface_mutex = PTHREAD_MUTEX_INITIALIZER;
static ANativeWindow *g_surface;
static _Atomic float g_move_x;
static _Atomic float g_move_y;
static _Atomic float g_look_x;
static _Atomic float g_look_y;
static _Atomic float g_pointer_x;
static _Atomic float g_pointer_y;
static _Atomic uint64_t g_pointer_generation;
static _Atomic uint32_t g_actions;
static _Atomic uint32_t g_action_taps;
static _Atomic int g_gameplay_mode;
static _Atomic int g_block_target;
static _Atomic uint64_t g_generation;
static pthread_mutex_t g_event_mutex = PTHREAD_MUTEX_INITIALIZER;
static jay_scalar_queue g_events;
static jay_menu_queue g_menu_events;

static int jay_query_gameplay_mode(void);
static void jay_refresh_block_target(void);

static void jay_logcat_write(JNIEnv *env, jclass owner, jint priority, jbyteArray message) {
    (void)owner;
    if (message == NULL) return;
    jsize length = (*env)->GetArrayLength(env, message);
    if (length <= 0) return;
    jbyte *bytes = (*env)->GetByteArrayElements(env, message, NULL);
    if (bytes == NULL) return;
    char *text = malloc((size_t)length + 1);
    if (text != NULL) {
        memcpy(text, bytes, (size_t)length);
        text[length] = '\0';
        __android_log_write(priority, "JayMinecraft", text);
        free(text);
    }
    (*env)->ReleaseByteArrayElements(env, message, bytes, JNI_ABORT);
}

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
    return jay_query_gameplay_mode();
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
    // Loader-based SDL instances run their original main class without the bootstrap jar.
    // Register console logging only for the two launchers that install that jar.
    int uses_bootstrap =
        strcmp(class_name, "nz/work/minecraft/jay/AndroidGameBootstrap") == 0 ||
        strcmp(class_name, "nz/work/minecraft/jay/SdlMenuBootstrap") == 0;
    if (uses_bootstrap) {
        jclass log_class = (*env)->FindClass(
            env, "nz/work/minecraft/jay/AndroidGameBootstrap$LogcatOutputStream");
        if (log_class == NULL) {
            jay_capture_exception(env, "FindClass(LogcatOutputStream)");
            return JNI_ERR;
        }
        JNINativeMethod log_methods[] = {
            { "writeLogcat", "(I[B)V", (void *)jay_logcat_write },
        };
        if ((*env)->RegisterNatives(env, log_class, log_methods, 1) != JNI_OK) {
            jay_capture_exception(env, "RegisterNatives(LogcatOutputStream)");
            (*env)->DeleteLocalRef(env, log_class);
            return JNI_ERR;
        }
        (*env)->DeleteLocalRef(env, log_class);
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
        JAY_INPUT_METHOD(g_is_gameplay_controls_active, "gameplayControlsState", "()I");
        JAY_INPUT_METHOD(g_is_block_target, "isBlockTarget", "()Z");
        JAY_INPUT_METHOD(g_is_text_input_focused, "textInputFocusState", "()I");
        JAY_INPUT_METHOD(g_is_text_input_at, "isTextInputAt", "(FF)Z");
        JAY_INPUT_METHOD(g_gui_scale, "guiScale", "()I");
        JAY_INPUT_METHOD(g_inventory_stack_count, "inventoryStackCount", "(FF)I");
        JAY_INPUT_METHOD(g_dispatch_inventory_split, "dispatchInventorySplit", "(FFI)Z");
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

static int jay_query_gameplay_mode(void) {
    int attached;
    JNIEnv *env = jay_input_environment(&attached);
    if (env == NULL || g_is_gameplay_controls_active == NULL)
        return atomic_load(&g_gameplay_mode);
    jint state = (*env)->CallStaticIntMethod(env, g_input_bridge_class,
        g_is_gameplay_controls_active);
    if ((*env)->ExceptionCheck(env)) {
        if (!atomic_exchange(&g_input_bridge_error_logged, 1))
            (*env)->ExceptionDescribe(env);
        (*env)->ExceptionClear(env);
        state = -1;
    }
    if (attached && g_vm != NULL) (*g_vm)->DetachCurrentThread(g_vm);
    if (state < 0) return atomic_load(&g_gameplay_mode);
    int active = state > 0 ? 1 : 0;
    atomic_store(&g_gameplay_mode, active);
    return active;
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
int jay_inventory_stack_count(float x, float y) {
    int attached;
    JNIEnv *env = jay_input_environment(&attached);
    if (env == NULL || g_inventory_stack_count == NULL) return 0;
    jint count = (*env)->CallStaticIntMethod(env, g_input_bridge_class,
        g_inventory_stack_count, (jfloat)x, (jfloat)y);
    if ((*env)->ExceptionCheck(env)) {
        if (!atomic_exchange(&g_input_bridge_error_logged, 1)) (*env)->ExceptionDescribe(env);
        (*env)->ExceptionClear(env);
        count = 0;
    }
    if (attached && g_vm != NULL) (*g_vm)->DetachCurrentThread(g_vm);
    return count;
}

__attribute__((visibility("default")))
int jay_gui_scale_get(void) {
    int attached;
    JNIEnv *env = jay_input_environment(&attached);
    if (env == NULL || g_gui_scale == NULL) return 0;
    jint scale = (*env)->CallStaticIntMethod(env, g_input_bridge_class, g_gui_scale);
    if ((*env)->ExceptionCheck(env)) {
        if (!atomic_exchange(&g_input_bridge_error_logged, 1)) (*env)->ExceptionDescribe(env);
        (*env)->ExceptionClear(env);
        scale = 0;
    }
    if (attached && g_vm != NULL) (*g_vm)->DetachCurrentThread(g_vm);
    return scale > 0 ? scale : 0;
}

__attribute__((visibility("default")))
int jay_java_inventory_split(float x, float y, int count) {
    int attached;
    JNIEnv *env = jay_input_environment(&attached);
    if (env == NULL || g_dispatch_inventory_split == NULL) return 0;
    jboolean delivered = (*env)->CallStaticBooleanMethod(env, g_input_bridge_class,
        g_dispatch_inventory_split, (jfloat)x, (jfloat)y, (jint)count);
    return jay_finish_input_call(env, attached, delivered);
}

__attribute__((visibility("default")))
int jay_block_target_get(void) {
    jay_refresh_block_target();
    return atomic_load(&g_block_target);
}

__attribute__((visibility("default")))
int jay_text_input_focused_get(void) {
    int attached;
    JNIEnv *env = jay_input_environment(&attached);
    if (env == NULL || g_is_text_input_focused == NULL) return -1;
    jint state = (*env)->CallStaticIntMethod(env, g_input_bridge_class,
        g_is_text_input_focused);
    if ((*env)->ExceptionCheck(env)) {
        if (!atomic_exchange(&g_input_bridge_error_logged, 1))
            (*env)->ExceptionDescribe(env);
        (*env)->ExceptionClear(env);
        state = -1;
    }
    if (attached && g_vm != NULL) (*g_vm)->DetachCurrentThread(g_vm);
    return state < 0 ? -1 : (state > 0 ? 1 : 0);
}

__attribute__((visibility("default")))
int jay_text_input_at_get(float x, float y) {
    int attached;
    JNIEnv *env = jay_input_environment(&attached);
    if (env == NULL || g_is_text_input_at == NULL) return 0;
    jboolean targeted = (*env)->CallStaticBooleanMethod(env, g_input_bridge_class,
        g_is_text_input_at, (jfloat)x, (jfloat)y);
    return jay_finish_input_call(env, attached, targeted);
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
    pthread_mutex_unlock(&g_surface_mutex);
    if (previous != NULL) ANativeWindow_release(previous);
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
void jay_input_cancel_action(int action) {
    if (action < 0 || action >= JAY_ACTION_COUNT) return;
    uint32_t bit = 1u << (uint32_t)action;
    // A cancelled Android gesture is not a short tap. Clear both the held state and any tap that
    // has not yet been consumed so ACTION_CANCEL cannot produce an attack, jump or menu action.
    atomic_fetch_and(&g_actions, ~bit);
    atomic_fetch_and(&g_action_taps, ~bit);
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
    pthread_mutex_lock(&g_event_mutex);
    atomic_store(&g_pointer_x, x);
    atomic_store(&g_pointer_y, y);
    if (click && !jay_menu_queue_push(
            &g_menu_events, JAY_MENU_EVENT_POINTER, x, y, 1)) {
        __android_log_write(ANDROID_LOG_ERROR, "JayMinecraft",
            "Out of memory while queueing a touch menu event");
    }
    atomic_fetch_add(&g_pointer_generation, 1);
    atomic_fetch_add(&g_generation, 1);
    pthread_mutex_unlock(&g_event_mutex);
}

__attribute__((visibility("default")))
void jay_input_split_inventory(float x, float y, int count) {
    if (count == 0) return;
    if (x < 0.f) x = 0.f;
    if (x > 1.f) x = 1.f;
    if (y < 0.f) y = 0.f;
    if (y > 1.f) y = 1.f;
    pthread_mutex_lock(&g_event_mutex);
    if (!jay_menu_queue_push(
            &g_menu_events, JAY_MENU_EVENT_SPLIT, x, y, count)) {
        __android_log_write(ANDROID_LOG_ERROR, "JayMinecraft",
            "Out of memory while queueing an inventory split event");
    }
    atomic_fetch_add(&g_generation, 1);
    pthread_mutex_unlock(&g_event_mutex);
}

__attribute__((visibility("default")))
void jay_input_add_codepoint(uint32_t codepoint) {
    if (codepoint == 0 || codepoint > 0x10ffffu || (codepoint >= 0xd800u && codepoint <= 0xdfffu)) return;
    pthread_mutex_lock(&g_event_mutex);
    if (!jay_scalar_queue_push(&g_events, JAY_EVENT_CHAR, (int32_t)codepoint)) {
        __android_log_write(ANDROID_LOG_ERROR, "JayMinecraft",
            "Out of memory while queueing a text input event");
    }
    atomic_fetch_add(&g_generation, 1);
    pthread_mutex_unlock(&g_event_mutex);
}

__attribute__((visibility("default")))
void jay_input_add_key(int32_t key) {
    pthread_mutex_lock(&g_event_mutex);
    if (!jay_scalar_queue_push(&g_events, JAY_EVENT_KEY, key)) {
        __android_log_write(ANDROID_LOG_ERROR, "JayMinecraft",
            "Out of memory while queueing a key input event");
    }
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
    pthread_mutex_lock(&g_event_mutex);
    snapshot->pointer_x = atomic_load(&g_pointer_x);
    snapshot->pointer_y = atomic_load(&g_pointer_y);
    snapshot->actions = atomic_load(&g_actions);
    snapshot->action_taps = atomic_exchange(&g_action_taps, 0);
    snapshot->event_count = jay_scalar_queue_drain(
        &g_events,
        JAY_EVENT_CAPACITY,
        snapshot->event_types,
        snapshot->event_values);
    snapshot->menu_event_count = jay_menu_queue_drain(
        &g_menu_events,
        JAY_MENU_EVENT_CAPACITY,
        snapshot->menu_event_types,
        snapshot->menu_event_x,
        snapshot->menu_event_y,
        snapshot->menu_event_values);
    snapshot->pointer_generation = atomic_load(&g_pointer_generation);
    snapshot->generation = atomic_load(&g_generation);
    pthread_mutex_unlock(&g_event_mutex);
}
