#include "jay_mining_frame.h"
#include <assert.h>

int main(void) {
    JayMiningFrame frame = {0};
    assert(jay_mining_frame(&frame, 1, 999, 10) == 999);
    assert(jay_mining_frame(&frame, 1, JAY_WAITING_FOR_MESH, 27) == 999);
    // The old mesh is still in this back buffer even when the upload runs late.
    assert(jay_mining_frame(&frame, 1, JAY_MINING_FULL, 180) == 999);
    assert(jay_mining_frame(&frame, 1, JAY_MINING_IDLE, 320) == JAY_MINING_FULL);
    assert(jay_mining_frame(&frame, 1, JAY_MINING_FULL, 421) == JAY_MINING_IDLE);

    // A replacement finger and an interrupted hold cannot complete from a stale upload.
    assert(jay_mining_frame(&frame, 1, 947, 430) == 947);
    assert(jay_mining_frame(&frame, 1, JAY_MINING_FULL, 450) == 947);
    assert(jay_mining_frame(&frame, 2, JAY_MINING_FULL, 468) == JAY_MINING_IDLE);
    assert(jay_mining_frame(&frame, 2, 400, 490) == 400);
    assert(jay_mining_frame(&frame, 2, JAY_MINING_IDLE, 506) == JAY_MINING_IDLE);
    assert(jay_mining_frame(&frame, 2, JAY_MINING_FULL, 524) == JAY_MINING_IDLE);
    return 0;
}
