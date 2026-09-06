/*
 * hal_esp32 — mm_hal for ESP-IDF: SNTP-set clock, esp_random, esp_http_client over esp-tls, NVS,
 * gpio, adc_oneshot with calibration. The one file that knows it is on an ESP32.
 */
#ifndef HAL_ESP32_H
#define HAL_ESP32_H

#include "mm_hal.h"

#ifdef __cplusplus
extern "C" {
#endif

/*
 * Fills *hal. controller_url is the base ("https://host:port/", trailing slash) for the Wi-Fi link,
 * ignored for the serial link (CONFIG_MM_LINK_SERIAL: the same exchanges go to a bridge over a UART,
 * and the clock is asked of the bridge). Opens NVS (namespace "micromound") and the ADC unit. Returns
 * 0, or -1 when NVS cannot be opened — a board without protected storage has no identity and must not
 * run.
 */
int mm_hal_esp32_init(mm_hal *hal, const char *controller_url);

/* Serial link only: ask the bridge for the time and set the system clock. Returns 0, or -1 when it did not answer. */
int mm_hal_esp32_sync_clock(void);

/* The link UART (serial link and port server): mm_serial_io-shaped, so either role can be built on them. */
int mm_hal_esp32_uart_init(void);
int mm_hal_esp32_uart_write(void *ctx, const uint8_t *bytes, size_t n);
int mm_hal_esp32_uart_read_byte(void *ctx, uint8_t *out, int timeout_ms);

/* Bench provisioning: store the one-time token unless a token or a controller key is already stored. */
void mm_hal_esp32_provision_token(const mm_hal *hal, const char *token);

#ifdef __cplusplus
}
#endif

#endif
