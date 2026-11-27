#ifndef JAY_SCALAR_QUEUE_H
#define JAY_SCALAR_QUEUE_H

#include <stddef.h>
#include <stdint.h>

typedef struct jay_scalar_event {
    uint32_t type;
    int32_t value;
    struct jay_scalar_event *next;
} jay_scalar_event;

typedef struct {
    jay_scalar_event *head;
    jay_scalar_event *tail;
    size_t count;
} jay_scalar_queue;

int jay_scalar_queue_push(
    jay_scalar_queue *queue,
    uint32_t type,
    int32_t value);

uint32_t jay_scalar_queue_drain(
    jay_scalar_queue *queue,
    uint32_t capacity,
    uint32_t *types,
    int32_t *values);

size_t jay_scalar_queue_count(const jay_scalar_queue *queue);
void jay_scalar_queue_clear(jay_scalar_queue *queue);

#endif
