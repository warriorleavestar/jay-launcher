#include "jay_menu_queue.h"

#include <assert.h>
#include <stdint.h>

#define SNAPSHOT_CAPACITY 32
#define POINTER_EVENT 1
#define SPLIT_EVENT 2

static void assert_float(float actual, float expected) {
    float difference = actual - expected;
    if (difference < 0.f) difference = -difference;
    assert(difference < 0.0001f);
}

int main(void) {
    jay_menu_queue queue = {0};

    /* Fill all but one slot with clicks, then queue a pickup and its place. */
    for (int32_t index = 0; index < 31; index++) {
        assert(jay_menu_queue_push(
            &queue, POINTER_EVENT, (float)index / 100.f, 0.25f, index));
    }
    assert(jay_menu_queue_push(&queue, SPLIT_EVENT, 0.75f, 0.5f, 32));
    assert(jay_menu_queue_push(&queue, POINTER_EVENT, 0.9f, 0.8f, 33));
    assert(jay_menu_queue_count(&queue) == 33);

    uint32_t types[SNAPSHOT_CAPACITY];
    float xs[SNAPSHOT_CAPACITY];
    float ys[SNAPSHOT_CAPACITY];
    int32_t values[SNAPSHOT_CAPACITY];

    uint32_t first = jay_menu_queue_drain(
        &queue, SNAPSHOT_CAPACITY, types, xs, ys, values);
    assert(first == SNAPSHOT_CAPACITY);
    for (uint32_t index = 0; index < 31; index++) {
        assert(types[index] == POINTER_EVENT);
        assert(values[index] == (int32_t)index);
    }
    assert(types[31] == SPLIT_EVENT);
    assert_float(xs[31], 0.75f);
    assert_float(ys[31], 0.5f);
    assert(values[31] == 32);
    assert(jay_menu_queue_count(&queue) == 1);

    uint32_t second = jay_menu_queue_drain(
        &queue, SNAPSHOT_CAPACITY, types, xs, ys, values);
    assert(second == 1);
    assert(types[0] == POINTER_EVENT);
    assert_float(xs[0], 0.9f);
    assert_float(ys[0], 0.8f);
    assert(values[0] == 33);
    assert(jay_menu_queue_count(&queue) == 0);

    jay_menu_queue_clear(&queue);
    return 0;
}
