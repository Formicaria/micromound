/*
 * hal_esp32 — mm_hal over ESP-IDF v5.x. Written against the v5.2+ APIs (adc_oneshot, adc_cali,
 * esp_http_client, esp_crt_bundle, NVS) and not yet compiled on a bench — see the README's status
 * line; this is the file the first bench build will correct.
 *
 * Every function keeps the HAL's contract: 0 on success, -1 on a failure the library handles
 * (offline, absent key, a line that will not drive). Nothing here panics on a controller that is
 * down or a sensor that is unplugged.
 */
#include "hal_esp32.h"

#include <stdio.h>
#include <string.h>
#include <time.h>

#include "driver/gpio.h"
#include "esp_adc/adc_cali.h"
#include "esp_adc/adc_cali_scheme.h"
#include "esp_adc/adc_oneshot.h"
#include "esp_crt_bundle.h"
#include "esp_http_client.h"
#include "esp_log.h"
#include "esp_random.h"
#include "nvs.h"
#include "nvs_flash.h"

static const char *TAG = "mm_hal";

#define NVS_NAMESPACE "micromound"
#define HTTP_TIMEOUT_MS 10000
#define ADC_CHANNELS 10

/* main/certs/controller_ca.pem, embedded by CMake (EMBED_TXTFILES); empty when the bundle is to be used */
extern const char controller_ca_pem_start[] asm("_binary_controller_ca_pem_start");

typedef struct hal_ctx {
    char base_url[160];
    nvs_handle_t nvs;
    uint64_t gpio_configured;                 /* bit per pin already set up as an output */
    adc_oneshot_unit_handle_t adc;
    adc_cali_handle_t cali[ADC_CHANNELS];
    int channel_ready[ADC_CHANNELS];
} hal_ctx;

static hal_ctx the_ctx;

/* ---- clock ---------------------------------------------------------------------------------- */

static int64_t hal_now(void *ctx)
{
    time_t t = time(NULL);
    struct tm tm;
    (void)ctx;
    gmtime_r(&t, &tm);
    if (tm.tm_year + 1900 < 2024) return 0;   /* SNTP has not set the clock: the epoch, or the RTC's boot value */
    return (int64_t)t;
}

/* ---- entropy -------------------------------------------------------------------------------- */

static int hal_random(void *ctx, uint8_t *out, size_t n)
{
    (void)ctx;
    esp_fill_random(out, n);                  /* hardware RNG; with Wi-Fi up it is the documented CSPRNG path */
    return 0;
}

/* ---- HTTPS ---------------------------------------------------------------------------------- */

static int hal_http_post_json(void *ctx, const char *path, const char *body, size_t body_len,
                              char *resp, size_t cap, size_t *resp_len, int *status)
{
    hal_ctx *h = (hal_ctx *)ctx;
    char url[256];
    esp_http_client_config_t cfg;
    esp_http_client_handle_t client;
    int written, read_len;
    int64_t content_length;

    *resp_len = 0;
    if (cap) resp[0] = '\0';
    if (snprintf(url, sizeof url, "%s%s", h->base_url, path) >= (int)sizeof url) return -1;

    memset(&cfg, 0, sizeof cfg);
    cfg.url = url;
    cfg.method = HTTP_METHOD_POST;
    cfg.timeout_ms = HTTP_TIMEOUT_MS;
    cfg.buffer_size = 2048;
    cfg.buffer_size_tx = 2048;
    if (strstr(controller_ca_pem_start, "-----BEGIN") != NULL) cfg.cert_pem = controller_ca_pem_start;   /* a private CA */
    else cfg.crt_bundle_attach = esp_crt_bundle_attach;                                                    /* a public one */

    client = esp_http_client_init(&cfg);
    if (!client) return -1;
    esp_http_client_set_header(client, "Content-Type", "application/json");
    esp_http_client_set_header(client, "Accept", "application/json");

    if (esp_http_client_open(client, (int)body_len) != ESP_OK) {          /* DNS, TCP, TLS: no exchange happened */
        esp_http_client_cleanup(client);
        return -1;
    }
    written = esp_http_client_write(client, body, (int)body_len);
    if (written < 0 || (size_t)written != body_len) {
        esp_http_client_close(client);
        esp_http_client_cleanup(client);
        return -1;
    }
    content_length = esp_http_client_fetch_headers(client);                 /* -1: no response at all */
    if (content_length < 0) {
        esp_http_client_close(client);
        esp_http_client_cleanup(client);
        return -1;
    }
    read_len = esp_http_client_read_response(client, resp, (int)cap - 1);  /* handles chunked bodies too */
    if (read_len < 0) read_len = 0;
    resp[read_len] = '\0';
    *resp_len = (size_t)read_len;
    *status = esp_http_client_get_status_code(client);
    esp_http_client_close(client);
    esp_http_client_cleanup(client);
    return 0;
}

/* ---- NVS ------------------------------------------------------------------------------------ */

static int hal_kv_get(void *ctx, const char *key, uint8_t *out, size_t cap, size_t *n)
{
    hal_ctx *h = (hal_ctx *)ctx;
    size_t len = 0;
    esp_err_t err = nvs_get_blob(h->nvs, key, NULL, &len);   /* the length first */
    if (err != ESP_OK || len > cap) return -1;
    if (len > 0) {
        err = nvs_get_blob(h->nvs, key, out, &len);
        if (err != ESP_OK) return -1;
    }
    *n = len;
    return 0;
}

static int hal_kv_set(void *ctx, const char *key, const uint8_t *data, size_t n)
{
    hal_ctx *h = (hal_ctx *)ctx;
    esp_err_t err;
    if (n == 0) {                                             /* NVS has no empty blob; absent means the same to the library */
        err = nvs_erase_key(h->nvs, key);
        if (err != ESP_OK && err != ESP_ERR_NVS_NOT_FOUND) return -1;
    } else {
        err = nvs_set_blob(h->nvs, key, data, n);
        if (err != ESP_OK) return -1;
    }
    return nvs_commit(h->nvs) == ESP_OK ? 0 : -1;
}

/* ---- GPIO ----------------------------------------------------------------------------------- */

static int hal_gpio_write(void *ctx, int pin, int level)
{
    hal_ctx *h = (hal_ctx *)ctx;
    if (pin < 0 || pin >= 64 || !GPIO_IS_VALID_OUTPUT_GPIO(pin)) return -1;
    if (!(h->gpio_configured & (1ULL << pin))) {
        gpio_config_t io;
        memset(&io, 0, sizeof io);
        io.pin_bit_mask = 1ULL << pin;
        io.mode = GPIO_MODE_OUTPUT;
        io.pull_up_en = GPIO_PULLUP_DISABLE;
        io.pull_down_en = GPIO_PULLDOWN_DISABLE;
        io.intr_type = GPIO_INTR_DISABLE;
        if (gpio_config(&io) != ESP_OK) return -1;
        h->gpio_configured |= 1ULL << pin;
    }
    return gpio_set_level((gpio_num_t)pin, level ? 1 : 0) == ESP_OK ? 0 : -1;
}

/* ---- ADC ------------------------------------------------------------------------------------ */

static int adc_channel_ready(hal_ctx *h, int channel)
{
    adc_oneshot_chan_cfg_t cc;
    adc_atten_t atten;
    if (h->channel_ready[channel]) return 0;
#ifdef ADC_ATTEN_DB_12
    atten = ADC_ATTEN_DB_12;                                  /* 0 – ~3.1 V on the ESP32 */
#else
    atten = ADC_ATTEN_DB_11;
#endif
    memset(&cc, 0, sizeof cc);
    cc.atten = atten;
    cc.bitwidth = ADC_BITWIDTH_DEFAULT;
    if (adc_oneshot_config_channel(h->adc, (adc_channel_t)channel, &cc) != ESP_OK) return -1;

    h->cali[channel] = NULL;
#if ADC_CALI_SCHEME_CURVE_FITTING_SUPPORTED
    {
        adc_cali_curve_fitting_config_t cf;
        memset(&cf, 0, sizeof cf);
        cf.unit_id = ADC_UNIT_1;
        cf.chan = (adc_channel_t)channel;
        cf.atten = atten;
        cf.bitwidth = ADC_BITWIDTH_DEFAULT;
        if (adc_cali_create_scheme_curve_fitting(&cf, &h->cali[channel]) != ESP_OK) h->cali[channel] = NULL;
    }
#elif ADC_CALI_SCHEME_LINE_FITTING_SUPPORTED
    {
        adc_cali_line_fitting_config_t lf;
        memset(&lf, 0, sizeof lf);
        lf.unit_id = ADC_UNIT_1;
        lf.atten = atten;
        lf.bitwidth = ADC_BITWIDTH_DEFAULT;
        if (adc_cali_create_scheme_line_fitting(&lf, &h->cali[channel]) != ESP_OK) h->cali[channel] = NULL;
    }
#endif
    if (!h->cali[channel]) ESP_LOGW(TAG, "adc channel %d: no calibration scheme; readings use the nominal 3.3 V / 4095 scale", channel);
    h->channel_ready[channel] = 1;
    return 0;
}

static int hal_adc_read(void *ctx, int channel, double *volts)
{
    hal_ctx *h = (hal_ctx *)ctx;
    int raw = 0, mv = 0;
    if (channel < 0 || channel >= ADC_CHANNELS || !h->adc) return -1;
    if (adc_channel_ready(h, channel) != 0) return -1;
    if (adc_oneshot_read(h->adc, (adc_channel_t)channel, &raw) != ESP_OK) return -1;
    if (h->cali[channel] && adc_cali_raw_to_voltage(h->cali[channel], raw, &mv) == ESP_OK) *volts = mv / 1000.0;
    else *volts = raw * (3.3 / 4095.0);                       /* uncalibrated: nominal, a few percent off */
    return 0;
}

/* ---- init ----------------------------------------------------------------------------------- */

int mm_hal_esp32_init(mm_hal *hal, const char *controller_url)
{
    hal_ctx *h = &the_ctx;
    adc_oneshot_unit_init_cfg_t unit;

    memset(h, 0, sizeof *h);
    snprintf(h->base_url, sizeof h->base_url, "%s", controller_url);
    if (h->base_url[0] && h->base_url[strlen(h->base_url) - 1] != '/' && strlen(h->base_url) + 1 < sizeof h->base_url)
        strcat(h->base_url, "/");

    if (nvs_open(NVS_NAMESPACE, NVS_READWRITE, &h->nvs) != ESP_OK) {
        ESP_LOGE(TAG, "NVS namespace '%s' cannot be opened: no protected storage, no identity", NVS_NAMESPACE);
        return -1;
    }

    memset(&unit, 0, sizeof unit);
    unit.unit_id = ADC_UNIT_1;
    unit.ulp_mode = ADC_ULP_MODE_DISABLE;
    if (adc_oneshot_new_unit(&unit, &h->adc) != ESP_OK) {
        ESP_LOGW(TAG, "ADC unit 1 unavailable; probe reads will fault (never read as zero)");
        h->adc = NULL;
    }

    memset(hal, 0, sizeof *hal);
    hal->ctx = h;
    hal->now = hal_now;
    hal->random_bytes = hal_random;
    hal->http_post_json = hal_http_post_json;
    hal->kv_get = hal_kv_get;
    hal->kv_set = hal_kv_set;
    hal->gpio_write = hal_gpio_write;
    hal->adc_read = hal_adc_read;
    return 0;
}

void mm_hal_esp32_provision_token(const mm_hal *hal, const char *token)
{
    uint8_t scratch[64];
    size_t n = 0;
    if (!token || !token[0]) return;
    if (hal->kv_get(hal->ctx, MM_KV_CONTROLLER_PK, scratch, sizeof scratch, &n) == 0 && n == 32) return;   /* already enrolled */
    if (hal->kv_get(hal->ctx, MM_KV_ENROLL_TOKEN, scratch, sizeof scratch, &n) == 0 && n > 0) return;      /* already provisioned */
    if (hal->kv_set(hal->ctx, MM_KV_ENROLL_TOKEN, (const uint8_t *)token, strlen(token)) == 0)
        ESP_LOGI(TAG, "enrollment token provisioned from the build configuration (bench mode)");
}
