#include "jay_scalar_queue.h"

#include <stdlib.h>

int jay_scalar_queue_push(
    jay_scalar_queue *queue,
    uint32_t type,
    int32_t value) {
    jay_scalar_event *event = malloc(sizeof(*event));
    if (event == NULL) return 0;

    event->type = type;
    event->value = value;
    event->next = NULL;

    if (queue->tail != NULL)
        queue->tail->next = event;
    else
        queue->head = event;
    queue->tail = event;
    queue->count++;
    return 1;
}

uint32_t jay_scalar_queue_drain(
    jay_scalar_queue *queue,
    uint32_t capacity,
    uint32_t *types,
    int32_t *values) {
    uint32_t drained = 0;
    while (drained < capacity && queue->head != NULL) {
        jay_scalar_event *event = queue->head;
        types[drained] = event->type;
        values[drained] = event->value;
        drained++;

        queue->head = event->next;
        if (queue->head == NULL) queue->tail = NULL;
        queue->count--;
        free(event);
    }
    return drained;
}

size_t jay_scalar_queue_count(const jay_scalar_queue *queue) {
    return queue->count;
}

void jay_scalar_queue_clear(jay_scalar_queue *queue) {
    while (queue->head != NULL) {
        jay_scalar_event *event = queue->head;
        queue->head = event->next;
        free(event);
    }
    queue->tail = NULL;
    queue->count = 0;
}
