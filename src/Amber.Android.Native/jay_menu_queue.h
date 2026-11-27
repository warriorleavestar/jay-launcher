#ifndef JAY_MENU_QUEUE_H
#define JAY_MENU_QUEUE_H

#include <stddef.h>
#include <stdint.h>

typedef struct jay_menu_event {
    uint32_t type;
    float x;
    float y;
    int32_t value;
    struct jay_menu_event *next;
} jay_menu_event;

typedef struct {
    jay_menu_event *head;
    jay_menu_event *tail;
    size_t count;
} jay_menu_queue;

int jay_menu_queue_push(
    jay_menu_queue *queue,
    uint32_t type,
    float x,
    float y,
    int32_t value);

uint32_t jay_menu_queue_drain(
    jay_menu_queue *queue,
    uint32_t capacity,
    uint32_t *types,
    float *xs,
    float *ys,
    int32_t *values);

size_t jay_menu_queue_count(const jay_menu_queue *queue);
void jay_menu_queue_clear(jay_menu_queue *queue);

#endif
