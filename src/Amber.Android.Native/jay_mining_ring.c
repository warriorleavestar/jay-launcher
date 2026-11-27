// Draw into Minecraft's own back buffer. A separate Android View has an independent
// presentation queue and can lead or trail the terrain even when both sample the same tick.
#include <GLES3/gl3.h>
#include <android/log.h>
#include <dlfcn.h>
#include <pthread.h>
#include <stdatomic.h>
#include <stdint.h>
#include <stdio.h>
#include <stdlib.h>
#include <time.h>

#include "jay_mining_frame.h"

int jay_game_frame_mining_progress(void);

typedef struct {
    int64_t gesture;
    float x, y, radius, stroke;
    int width, height;
} Ring;

static pthread_mutex_t lock = PTHREAD_MUTEX_INITIALIZER;
static Ring touch;
static _Atomic int holding, ready;
static void *last_context;
static GLuint program, vao;
static GLint geometry_uniform, arc_uniform, width_uniform;
static JayMiningFrame frame;

// Use the same GLES implementation as MobileGlues, including the app's ANGLE on
// emulators. Mixing system GL dispatch with an ANGLE context silently loses draws.
#define GL_FUNCTIONS(F) \
    F(CreateShader) F(ShaderSource) F(CompileShader) F(DeleteShader) F(CreateProgram) \
    F(AttachShader) F(LinkProgram) F(GetProgramiv) F(DeleteProgram) F(GetUniformLocation) \
    F(GenVertexArrays) F(BindVertexArray) F(UseProgram) F(GetIntegerv) F(GetBooleanv) \
    F(IsEnabled) F(Enable) F(Disable) F(Viewport) F(BindFramebuffer) F(ColorMask) \
    F(BlendFuncSeparate) F(BlendEquationSeparate) F(Uniform4f) F(Uniform1f) F(DrawArrays)
#define DECLARE(name) static __typeof__(&gl##name) ring##name;
GL_FUNCTIONS(DECLARE)

static int load_gl(void) {
    static void *library;
    if (!library) {
        const char *angle = getenv("MG_ANGLE_DIR");
        char path[4096];
        if (angle && *angle) {
            if ((size_t)snprintf(path, sizeof path, "%s/libGLESv2_angle.so", angle) >= sizeof path)
                return 0;
            library = dlopen(path, RTLD_NOW | RTLD_LOCAL);
        } else {
            library = dlopen("libGLESv3.so", RTLD_NOW | RTLD_LOCAL);
        }
    }
    if (!library)
        return 0;
#define RESOLVE(name) \
    ring##name = dlsym(library, "gl" #name); \
    if (!ring##name) return 0;
    GL_FUNCTIONS(RESOLVE)
    return 1;
}

static GLuint shader(GLenum type, const char *src) {
    GLuint id = ringCreateShader(type);
    ringShaderSource(id, 1, &src, NULL);
    ringCompileShader(id);
    return id;
}

static int prepare(void) {
    if (!load_gl())
        return 0;
    GLuint vs = shader(GL_VERTEX_SHADER,
        "#version 300 es\n"
        "void main() {\n"
        "    vec2 corner = vec2((gl_VertexID << 1) & 2, gl_VertexID & 2);\n"
        "    gl_Position = vec4(corner * 2.0 - 1.0, 0.0, 1.0);\n"
        "}\n");
    GLuint fs = shader(GL_FRAGMENT_SHADER,
        "#version 300 es\n"
        "precision highp float;\n"
        "uniform vec4 geometry;\n"
        "uniform float arc, halfWidth;\n"
        "const float turn = 6.2831853;\n"
        "out vec4 color;\n"
        "void main() {\n"
        "    vec2 p = (gl_FragCoord.xy - geometry.xy) / geometry.zw;\n"
        "    float radius = length(p);\n"
        "    float pixel = fwidth(radius);\n"
        "    float band = 1.0 - smoothstep(0.0, pixel, abs(radius - 1.0) - halfWidth);\n"
        "    float angle = mod(atan(p.x, p.y) + turn, turn);\n"
        "    float cap = min(length(p - vec2(0, 1)), length(p - vec2(sin(arc), cos(arc))));\n"
        "    float ink = angle <= arc ? band : 1.0 - smoothstep(halfWidth, halfWidth + pixel, cap);\n"
        "    float white = 0.92 * ink;\n"
        "    float track = 0.43 * band * (1.0 - white);\n"
        "    float alpha = white + track;\n"
        "    if (alpha <= 0.0) discard;\n"
        "    color = vec4((vec3(white) + vec3(0.063, 0.078, 0.094) * track) / alpha, alpha);\n"
        "}\n");
    program = ringCreateProgram();
    ringAttachShader(program, vs);
    ringAttachShader(program, fs);
    ringLinkProgram(program);
    ringDeleteShader(vs);
    ringDeleteShader(fs);
    GLint linked = 0;
    ringGetProgramiv(program, GL_LINK_STATUS, &linked);
    if (!linked) {
        ringDeleteProgram(program);
        program = 0;
        return 0;
    }
    geometry_uniform = ringGetUniformLocation(program, "geometry");
    arc_uniform = ringGetUniformLocation(program, "arc");
    width_uniform = ringGetUniformLocation(program, "halfWidth");
    ringGenVertexArrays(1, &vao);
    return vao != 0;
}

static void draw(Ring ring, int width, int height, int damage) {
    float sx = (float)width / ring.width, sy = (float)height / ring.height;
    float x = ring.x * sx, y = height - ring.y * sy, rx = ring.radius * sx, ry = ring.radius * sy;
    float max_arc = 6.2831853f - 2 * ring.stroke / ring.radius;
    float arc = 6.2831853f * damage / 1000;
    if (damage < 1000 && arc > max_arc)
        arc = max_arc;
    // Only cover the square containing the stroke. Restore every changed game state.
    GLint viewport[4], fbo, previous_program, previous_vao, blend[6];
    GLboolean mask[4];
    const GLenum blend_params[] = {GL_BLEND_SRC_RGB,   GL_BLEND_DST_RGB,      GL_BLEND_SRC_ALPHA,
                                   GL_BLEND_DST_ALPHA, GL_BLEND_EQUATION_RGB, GL_BLEND_EQUATION_ALPHA};
    const GLenum caps[] = {GL_CULL_FACE,      GL_DEPTH_TEST,         GL_STENCIL_TEST,
                           GL_SCISSOR_TEST,   GL_RASTERIZER_DISCARD, GL_SAMPLE_ALPHA_TO_COVERAGE,
                           GL_SAMPLE_COVERAGE};
    GLboolean enabled[sizeof caps / sizeof *caps];
    ringGetIntegerv(GL_VIEWPORT, viewport);
    ringGetIntegerv(GL_DRAW_FRAMEBUFFER_BINDING, &fbo);
    ringGetIntegerv(GL_CURRENT_PROGRAM, &previous_program);
    ringGetIntegerv(GL_VERTEX_ARRAY_BINDING, &previous_vao);
    ringGetBooleanv(GL_COLOR_WRITEMASK, mask);
    for (size_t i = 0; i < 6; i++)
        ringGetIntegerv(blend_params[i], &blend[i]);
    for (size_t i = 0; i < sizeof caps / sizeof *caps; i++) {
        enabled[i] = ringIsEnabled(caps[i]);
        if (enabled[i])
            ringDisable(caps[i]);
    }
    GLboolean blended = ringIsEnabled(GL_BLEND);
    if (!blended)
        ringEnable(GL_BLEND);
    ringBlendEquationSeparate(GL_FUNC_ADD, GL_FUNC_ADD);
    ringBlendFuncSeparate(GL_SRC_ALPHA, GL_ONE_MINUS_SRC_ALPHA, GL_ONE, GL_ONE_MINUS_SRC_ALPHA);
    ringColorMask(GL_TRUE, GL_TRUE, GL_TRUE, GL_TRUE);
    ringBindFramebuffer(GL_DRAW_FRAMEBUFFER, 0);
    float pad = ring.stroke + 2;
    ringViewport((GLint)(x - rx - pad * sx), (GLint)(y - ry - pad * sy), (GLsizei)(2 * (rx + pad * sx) + 2),
                 (GLsizei)(2 * (ry + pad * sy) + 2));
    ringUseProgram(program);
    ringBindVertexArray(vao);
    ringUniform4f(geometry_uniform, x, y, rx, ry);
    ringUniform1f(arc_uniform, arc);
    ringUniform1f(width_uniform, ring.stroke / (2 * ring.radius));
    ringDrawArrays(GL_TRIANGLES, 0, 3);
    ringBindVertexArray(previous_vao);
    ringUseProgram(previous_program);
    ringBindFramebuffer(GL_DRAW_FRAMEBUFFER, fbo);
    ringViewport(viewport[0], viewport[1], viewport[2], viewport[3]);
    ringColorMask(mask[0], mask[1], mask[2], mask[3]);
    ringBlendFuncSeparate(blend[0], blend[1], blend[2], blend[3]);
    ringBlendEquationSeparate(blend[4], blend[5]);
    if (!blended)
        ringDisable(GL_BLEND);
    for (size_t i = 0; i < sizeof caps / sizeof *caps; i++)
        if (enabled[i])
            ringEnable(caps[i]);
}

__attribute__((visibility("default"))) int jay_mining_ring_hold(int64_t gesture, float x, float y,
                                                                float radius, float stroke, int w, int h) {
    pthread_mutex_lock(&lock);
    touch = (Ring){gesture, x, y, radius, stroke, w, h};
    atomic_store(&holding, gesture != 0 && w > 0 && h > 0 && radius > 0);
    pthread_mutex_unlock(&lock);
    return atomic_load(&ready);
}

__attribute__((visibility("default"))) void jay_mining_ring_before_swap(void *context, int width,
                                                                        int height) {
    if (!atomic_load(&holding) || !context || width <= 0 || height <= 0)
        return;
    if (context != last_context) {
        last_context = context;
        frame = (JayMiningFrame){0};
        int available = prepare();
        atomic_store(&ready, available);
        __android_log_print(available ? ANDROID_LOG_INFO : ANDROID_LOG_WARN, "JayGame",
                            "Mining feedback renderer: %s",
                            available ? "game framebuffer" : "Android canvas");
    }
    if (!atomic_load(&ready))
        return;
    pthread_mutex_lock(&lock);
    Ring ring = touch;
    pthread_mutex_unlock(&lock);
    if (!ring.gesture || ring.width <= 0 || ring.height <= 0)
        return;
    struct timespec ts;
    clock_gettime(CLOCK_MONOTONIC, &ts);
    int64_t now = ts.tv_sec * 1000LL + ts.tv_nsec / 1000000;
    int damage = jay_mining_frame(&frame, ring.gesture, jay_game_frame_mining_progress(), now);
    if (damage >= 0)
        draw(ring, width, height, damage);
}
