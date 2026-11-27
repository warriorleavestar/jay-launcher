#include "jay_menu_queue.h"

#include <stdlib.h>

int jay_menu_queue_push(
    jay_menu_queue *queue,
    uint32_t type,
    float x,
    float y,
    int32_t value) {
    jay_menu_event *event = malloc(sizeof(*event));
    if (event == NULL) return 0;

    event->type = type;
    event->x = x;
    event->y = y;
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

uint32_t jay_menu_queue_drain(
    jay_menu_queue *queue,
    uint32_t capacity,
    uint32_t *types,
    float *xs,
    float *ys,
    int32_t *values) {
    uint32_t drained = 0;
    while (drained < capacity && queue->head != NULL) {
        jay_menu_event *event = queue->head;
        types[drained] = event->type;
        xs[drained] = event->x;
        ys[drained] = event->y;
        values[drained] = event->value;
        drained++;

        queue->head = event->next;
        if (queue->head == NULL) queue->tail = NULL;
        queue->count--;
        free(event);
    }
    return drained;
}

size_t jay_menu_queue_count(const jay_menu_queue *queue) {
    return queue->count;
}

void jay_menu_queue_clear(jay_menu_queue *queue) {
    while (queue->head != NULL) {
        jay_menu_event *event = queue->head;
        queue->head = event->next;
        free(event);
    }
    queue->tail = NULL;
    queue->count = 0;
}
