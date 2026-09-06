/*
 * board — what THIS board is: the compiled capability tables, the drivers on their pins, the
 * routine schedule. Change this file to describe different hardware; nothing else knows the pins.
 */
#ifndef BOARD_H
#define BOARD_H

#include "mm_app.h"
#include "mm_ports.h"

#ifdef __cplusplus
extern "C" {
#endif

/* Initialises the drivers over the HAL (relays come up at their safe level) and fills *cfg. Returns 0 or -1. */
int board_init(const mm_hal *hal, mm_app_config *cfg);

/* Every relay to its safe level, for a boot that cannot continue. */
void board_all_safe(void);

/* The board as a port server: its pins (with their compiled bounds) and channels. Returns 0 or -1. */
int board_ports_init(const mm_hal *hal, mm_ports *ports, const char *firmware, int64_t watchdog_s);

#ifdef __cplusplus
}
#endif

#endif
