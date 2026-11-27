#ifndef JAY_MINING_FRAME_H
#define JAY_MINING_FRAME_H

#include <stdint.h>

typedef struct {
    int64_t gesture;
    int last_partial;
    int mesh_for_next_frame;
    int64_t full_until;
} JayMiningFrame;

enum { JAY_WAITING_FOR_MESH = -2, JAY_MINING_IDLE = -1, JAY_MINING_FULL = 1000 };

// Called once per interactive window swap. The SDL client builds and draws its
// terrain commands before uploading rebuilt sections. An upload observed at this
// swap is included in the NEXT world frame, even if the device drops a display vsync.
static inline int jay_mining_frame(JayMiningFrame *frame, int64_t gesture, int sample, int64_t now_ms) {
    if (frame->gesture != gesture)
        *frame = (JayMiningFrame){.gesture = gesture, .last_partial = JAY_MINING_IDLE};

    if (sample >= 0 && sample < JAY_MINING_FULL) {
        frame->last_partial = sample;
        frame->mesh_for_next_frame = 0;
        frame->full_until = 0;
        return sample;
    }
    if (frame->mesh_for_next_frame) {
        frame->mesh_for_next_frame = 0;
        frame->last_partial = JAY_MINING_IDLE;
        frame->full_until = now_ms + 100;
    } else if (sample == JAY_MINING_FULL && frame->last_partial >= 0) {
        frame->mesh_for_next_frame = 1;
        return frame->last_partial;
    } else if (sample == JAY_WAITING_FOR_MESH) {
        return frame->last_partial;
    } else {
        frame->last_partial = JAY_MINING_IDLE;
    }
    return now_ms < frame->full_until ? JAY_MINING_FULL : JAY_MINING_IDLE;
}
#endif
