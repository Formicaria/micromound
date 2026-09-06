/*
 * app_main — boot the board, get a clock, hand everything to mm_app, and tick it forever.
 *
 * The order matters for safety: the relay is driven to its safe level (board_init) before the
 * network is even up; nothing signs or actuates until SNTP has set the clock (mm_app refuses a
 * zero clock); a boot that cannot get an identity or storage stops here with every output safe;
 * a service loop that hangs is rebooted by the task watchdog, and comes back up safe.
 */
#include <stdio.h>
#include <string.h>

#include "esp_log.h"
#include "esp_task_wdt.h"
#include "freertos/FreeRTOS.h"
#include "freertos/task.h"
#include "nvs_flash.h"
#include "sdkconfig.h"
#ifdef CONFIG_MM_LINK_WIFI
#include "esp_event.h"
#include "esp_netif.h"
#include "esp_netif_sntp.h"
#include "protocol_examples_common.h"
#endif

#include "board.h"
#include "hal_esp32.h"
#include "mm_app.h"

static const char *TAG = "micromound";

static mm_hal hal;
static mm_app app;                 /* ~60 KB: the device queue and the link buffer; static, never allocated */

static void halt_safe(const char *why)
{
    ESP_LOGE(TAG, "cannot run: %s — outputs held at their safe level", why);
    board_all_safe();
    for (;;) {
        esp_task_wdt_reset();
        vTaskDelay(pdMS_TO_TICKS(1000));
    }
}

void app_main(void)
{
    mm_app_config cfg;
    char error[256];
    esp_err_t err;
    int waited = 0;
#ifdef CONFIG_MM_LINK_WIFI
    esp_sntp_config_t sntp = ESP_NETIF_SNTP_DEFAULT_CONFIG(CONFIG_MM_NTP_SERVER);
    const char *controller = CONFIG_MM_CONTROLLER_URL;
#else
    const char *controller = "";                             /* the bridge holds the controller's address */
#endif

    ESP_ERROR_CHECK(esp_task_wdt_add(NULL));                 /* this task is the service loop; the watchdog watches it */

    err = nvs_flash_init();
    if (err == ESP_ERR_NVS_NO_FREE_PAGES || err == ESP_ERR_NVS_NEW_VERSION_FOUND) {
        ESP_ERROR_CHECK(nvs_flash_erase());
        err = nvs_flash_init();
    }
    if (err != ESP_OK) halt_safe("NVS will not initialise");

    if (mm_hal_esp32_init(&hal, controller) != 0) halt_safe("no protected storage or no link");
    if (board_init(&hal, &cfg) != 0) halt_safe("the relay line would not drive to its safe level");

#ifdef CONFIG_MM_LINK_WIFI
    ESP_ERROR_CHECK(esp_netif_init());
    ESP_ERROR_CHECK(esp_event_loop_create_default());
    ESP_ERROR_CHECK(example_connect());                      /* Wi-Fi station, from ESP-IDF's protocol_examples_common */
    ESP_ERROR_CHECK(esp_netif_sntp_init(&sntp));
#endif

    while (hal.now(hal.ctx) == 0) {                          /* no clock, no signatures, no actuation */
        esp_task_wdt_reset();
#ifdef CONFIG_MM_LINK_WIFI
        if (waited++ % 10 == 0) ESP_LOGI(TAG, "waiting for the clock (SNTP %s)", CONFIG_MM_NTP_SERVER);
#else
        if (waited++ % 10 == 0) ESP_LOGI(TAG, "waiting for the clock (asking the bridge)");
        mm_hal_esp32_sync_clock();                           /* the bridge answers with the Pi's clock, when it is there */
#endif
        vTaskDelay(pdMS_TO_TICKS(1000));
    }

    mm_hal_esp32_provision_token(&hal, CONFIG_MM_ENROLL_TOKEN);
    if (mm_app_init(&app, &hal, &cfg, error, sizeof error) != 0) halt_safe(error);
    ESP_LOGI(TAG, "mound %s: %s", CONFIG_MM_MOUND_ID, app.status.enrolled ? "enrolled; beating" : "not enrolled; trying the token");

    for (;;) {
        int before_beats = app.status.beats, before_enroll = app.status.enroll_attempts;
        esp_task_wdt_reset();
#ifdef CONFIG_MM_LINK_SERIAL
        if (waited++ % 3600 == 0) mm_hal_esp32_sync_clock();  /* an hourly correction from the bridge; a miss keeps the running clock */
#endif
        mm_app_tick(&app, 0);
        if (app.status.beats != before_beats || app.status.enroll_attempts != before_enroll)
            ESP_LOGI(TAG, "%s | %s | queue %u | %s", app.status.enrolled ? "enrolled" : "unenrolled",
                     mm_device_state(&app.device), (unsigned)mm_device_queue_depth(&app.device), app.status.last_detail);
        if (app.status.tripped) ESP_LOGE(TAG, "TRIP: a relay would not release; the mound is stopped");
        vTaskDelay(pdMS_TO_TICKS(CONFIG_MM_TICK_MS));
    }
}
