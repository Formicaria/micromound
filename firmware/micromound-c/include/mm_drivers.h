/*
 * mm_drivers — the two generic drivers of the reduced profile as kernel executors, over the HAL.
 *
 * mm_relay  = DigitalActuatorDriver: a digital output line brought up at its SAFE level, driven
 *             active for an `on_s` the kernel already clamped (capped again here at the effective
 *             max_on_s as a last-resort belt), HELD, and released by mm_relay_service once the
 *             deadline passes or by mm_relay_safe on any stop, quiesce or trip. A command is not
 *             evidence: the relay produces none. A line that will not release keeps its hold pending
 *             (the next tick retries) and reports it, so the app can treat the line as unsafe.
 * mm_probe  = AnalogSensorDriver: one ADC channel, volts × scale + offset, reported as a `reading`
 *             evidence item ({"value":…,"unit":…,"capability":…}, PROTOCOL.md §6) captured at the
 *             read. A failed read is a fault with no reading — never a zero.
 * mm_switch = DigitalSensorDriver: one input line — a limit switch, an interlock, a float — read as
 *             1 (asserted) or 0, reported as the same `reading` item. This is what independent
 *             confirmation of an actuation is made of: a command is not evidence, a switch is.
 *             A line that will not read is a fault with no reading — never a 0.
 */
#ifndef MM_DRIVERS_H
#define MM_DRIVERS_H

#include <stdint.h>
#include "mm_hal.h"
#include "mm_kernel.h"

#ifdef __cplusplus
extern "C" {
#endif

typedef struct mm_relay {
    const mm_hal *hal;
    const char *capability;
    int pin;
    int active_high;
    int has_hold;
    int64_t held_until;
    int release_failed;                 /* the last release write failed; the hold is still pending */
    int actuations;
    mm_executor executor;               /* bind this to the kernel */
} mm_relay;

/* Configures the relay and drives the line to its safe level. Returns the HAL's write result. */
int mm_relay_init(mm_relay *r, const mm_hal *hal, const char *capability, int pin, int active_high);

/* ITimedDriver.ServiceHolds: release an elapsed hold. Returns -1 when the release write failed (hold stays pending). */
int mm_relay_service(mm_relay *r, int64_t now);

/* IDriver.EnterSafeState: drive safe, end any hold. Returns -1 when the write failed. */
int mm_relay_safe(mm_relay *r);

typedef struct mm_probe {
    const mm_hal *hal;
    const char *capability;
    int channel;
    double scale, offset;
    const char *unit;
    int n;                              /* readings taken, for evidence ids */
    mm_executor executor;
} mm_probe;

void mm_probe_init(mm_probe *p, const mm_hal *hal, const char *capability, int channel, double scale, double offset, const char *unit);

typedef struct mm_switch {
    const mm_hal *hal;
    const char *capability;
    int pin;
    int active_high;                    /* the physical level that means asserted */
    const char *unit;
    int n;                              /* reads taken, for evidence ids */
    mm_executor executor;
} mm_switch;

void mm_switch_init(mm_switch *s, const mm_hal *hal, const char *capability, int pin, int active_high, const char *unit);

/* The `reading` payload as EvidenceReadings.Create writes it. Returns the length or 0. */
size_t mm_reading_payload(double value, const char *unit, const char *capability, char *out, size_t cap);

#ifdef __cplusplus
}
#endif

#endif
