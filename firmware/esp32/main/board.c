#include "board.h"
#include "mm_ports.h"
#include "sdkconfig.h"

#include <string.h>

/*
 * The same device the host fixtures describe (kernel-decisions.txt, device-session.txt,
 * enroll-exchange.txt): one temperature probe, one relay. Hardware limits are the relay's own
 * (60 s on, 120 s off, 4 starts an hour); a charter can only narrow them (PROTOCOL.md §5).
 */
static const char *const RELAY_PARAMS[] = { "on_s" };
static const mm_param_range RELAY_RANGES[] = { { "on_s", 1, 3600 } };
#define SET(v) { 1, (v) }
#define UNSET { 0, 0 }
static const mm_capability_desc CAPS[] = {
    { "sense.temp",  0, { UNSET, UNSET, UNSET, UNSET, UNSET }, NULL, 0, NULL, 0, NULL, 0, NULL, NULL },
    { "act.relay_1", 1, { SET(60), SET(120), UNSET, UNSET, SET(4) }, RELAY_PARAMS, 1, RELAY_PARAMS, 1, RELAY_RANGES, 1, "on_s", NULL },
};

/* The compiled routine schedule: a reading every minute; the relay for 30 s every ten minutes when
   chartered (the kernel refuses it otherwise, and records the refusal). */
static const mm_param RELAY_ON = { "on_s", 30 };
static const mm_schedule_entry SCHEDULE[] = {
    { "sense.temp", NULL, 0, 60 },
    { "act.relay_1", &RELAY_ON, 1, 600 },
};

static mm_relay relay;
static mm_probe probe;

int board_init(const mm_hal *hal, mm_app_config *cfg)
{
    int rc = mm_relay_init(&relay, hal, "act.relay_1", CONFIG_MM_RELAY_GPIO,
#ifdef CONFIG_MM_RELAY_ACTIVE_HIGH
                           1
#else
                           0
#endif
                           );
    mm_probe_init(&probe, hal, "sense.temp", CONFIG_MM_PROBE_ADC_CHANNEL,
                  CONFIG_MM_PROBE_SCALE_MILLI / 1000.0, CONFIG_MM_PROBE_OFFSET_MILLI / 1000.0, "C");

    memset(cfg, 0, sizeof *cfg);
    cfg->mound_id = CONFIG_MM_MOUND_ID;
    cfg->hardware_profile = "sense.temp,act.relay_1";
    cfg->caps = CAPS; cfg->n_caps = sizeof CAPS / sizeof CAPS[0];
    cfg->relays = &relay; cfg->n_relays = 1;
    cfg->probes = &probe; cfg->n_probes = 1;
    cfg->schedule = SCHEDULE; cfg->n_schedule = sizeof SCHEDULE / sizeof SCHEDULE[0];
    return rc;
}

void board_all_safe(void)
{
    mm_relay_safe(&relay);
}

/*
 * The same board as a port server (CONFIG_MM_LINK_PORTS): the relay pin with the CAPS table's hardware
 * max_on_s as the bound the board keeps for itself, and the probe's ADC channel. The Pi's manifest names
 * these by `link` + `pin` / `channel`, and its active_high must match the polarity compiled here.
 */
int board_ports_init(const mm_hal *hal, mm_ports *ports, const char *firmware, int64_t watchdog_s)
{
    mm_ports_init(ports, hal, "sense.temp,act.relay_1", firmware, watchdog_s);
    if (mm_ports_add_pin(ports, CONFIG_MM_RELAY_GPIO,
#ifdef CONFIG_MM_RELAY_ACTIVE_HIGH
                         1,
#else
                         0,
#endif
                         CAPS[1].hardware.max_on_s.present ? CAPS[1].hardware.max_on_s.value : 0) != 0) return -1;
    return mm_ports_add_channel(ports, CONFIG_MM_PROBE_ADC_CHANNEL);
}
