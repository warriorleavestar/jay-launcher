#include <jni.h>
#include <android/native_window.h>
#include <android/native_window_jni.h>
#include <dlfcn.h>
#include <pthread.h>
#include <stdatomic.h>
#include <stdio.h>
#include <stdint.h>
#include <stdlib.h>

#define JAY_JVM_ABI_VERSION 1
#define JAY_ACTION_COUNT 7

typedef jint (*jay_create_jvm_fn)(JavaVM **, void **, void *);

typedef struct {
    float move_x;
    float move_y;
    uint32_t actions;
    uint64_t generation;
} jay_input_snapshot;

static void *g_jvm_library;
static jay_create_jvm_fn g_create_jvm;
static char g_load_error[256];
static pthread_mutex_t g_surface_mutex = PTHREAD_MUTEX_INITIALIZER;
static ANativeWindow *g_surface;
static _Atomic float g_move_x;
static _Atomic float g_move_y;
static _Atomic uint32_t g_actions;
static _Atomic uint64_t g_generation;

__attribute__((visibility("default")))
int jay_jvm_abi_version(void) {
    return JAY_JVM_ABI_VERSION;
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
    return result;
}

static int jay_clear_exception(JNIEnv *env) {
    if (!(*env)->ExceptionCheck(env)) return 0;
    (*env)->ExceptionDescribe(env);
    (*env)->ExceptionClear(env);
    return -1;
}

__attribute__((visibility("default")))
int jay_jvm_run_main(JNIEnv *env, const char *class_name, int argc, const char *const *argv) {
    if (env == NULL || class_name == NULL || argc < 0 || (argc > 0 && argv == NULL)) return JNI_ERR;
    jclass main_class = (*env)->FindClass(env, class_name);
    if (main_class == NULL) {
        jay_clear_exception(env);
        return JNI_ERR;
    }
    jmethodID main_method = (*env)->GetStaticMethodID(env, main_class, "main", "([Ljava/lang/String;)V");
    if (main_method == NULL) {
        jay_clear_exception(env);
        return JNI_ERR;
    }
    jclass string_class = (*env)->FindClass(env, "java/lang/String");
    if (string_class == NULL) {
        jay_clear_exception(env);
        return JNI_ERR;
    }
    jobjectArray arguments = (*env)->NewObjectArray(env, argc, string_class, NULL);
    if (arguments == NULL || jay_clear_exception(env) != 0) return JNI_ENOMEM;
    for (int i = 0; i < argc; i++) {
        jstring value = (*env)->NewStringUTF(env, argv[i] == NULL ? "" : argv[i]);
        if (value == NULL || jay_clear_exception(env) != 0) return JNI_ENOMEM;
        (*env)->SetObjectArrayElement(env, arguments, i, value);
        (*env)->DeleteLocalRef(env, value);
        if (jay_clear_exception(env) != 0) return JNI_ERR;
    }
    (*env)->CallStaticVoidMethod(env, main_class, main_method, arguments);
    return jay_clear_exception(env) == 0 ? JNI_OK : JNI_ERR;
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
    else atomic_fetch_and(&g_actions, ~bit);
    atomic_fetch_add(&g_generation, 1);
}

__attribute__((visibility("default")))
void jay_input_set_movement(float x, float y) {
    atomic_store(&g_move_x, x);
    atomic_store(&g_move_y, y);
    atomic_fetch_add(&g_generation, 1);
}

__attribute__((visibility("default")))
void jay_input_read(jay_input_snapshot *snapshot) {
    if (snapshot == NULL) return;
    snapshot->move_x = atomic_load(&g_move_x);
    snapshot->move_y = atomic_load(&g_move_y);
    snapshot->actions = atomic_load(&g_actions);
    snapshot->generation = atomic_load(&g_generation);
}
