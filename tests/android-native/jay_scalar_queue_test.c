#include "jay_scalar_queue.h"

#include <assert.h>
#include <stdint.h>

#define SNAPSHOT_CAPACITY 80
#define CHAR_EVENT 1
#define KEY_EVENT 2
#define ENTER_KEY 257

static int glfw_should_process(
    uint64_t generation,
    uint64_t previous_generation,
    uint32_t event_count,
    uint32_t menu_event_count) {
    return generation != previous_generation ||
        event_count > 0 || menu_event_count > 0;
}

int main(void) {
    jay_scalar_queue queue = {0};

    /* A long IME commit followed by Enter must survive the snapshot boundary. */
    for (int32_t index = 0; index < SNAPSHOT_CAPACITY; index++) {
        assert(jay_scalar_queue_push(&queue, CHAR_EVENT, 'a' + index % 26));
    }
    assert(jay_scalar_queue_push(&queue, KEY_EVENT, ENTER_KEY));
    assert(jay_scalar_queue_count(&queue) == SNAPSHOT_CAPACITY + 1);

    uint32_t types[SNAPSHOT_CAPACITY];
    int32_t values[SNAPSHOT_CAPACITY];
    uint32_t first = jay_scalar_queue_drain(
        &queue, SNAPSHOT_CAPACITY, types, values);
    assert(first == SNAPSHOT_CAPACITY);
    for (uint32_t index = 0; index < first; index++) {
        assert(types[index] == CHAR_EVENT);
        assert(values[index] == 'a' + (int32_t)index % 26);
    }
    assert(jay_scalar_queue_count(&queue) == 1);

    uint32_t second = jay_scalar_queue_drain(
        &queue, SNAPSHOT_CAPACITY, types, values);
    assert(second == 1);
    assert(types[0] == KEY_EVENT);
    assert(values[0] == ENTER_KEY);
    assert(jay_scalar_queue_count(&queue) == 0);
    assert(glfw_should_process(81, 81, second, 0));
    assert(!glfw_should_process(81, 81, 0, 0));

    jay_scalar_queue_clear(&queue);
    return 0;
}
