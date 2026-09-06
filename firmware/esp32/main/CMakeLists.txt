idf_component_register(
    SRCS "app_main.c" "hal_esp32.c" "board.c"
    INCLUDE_DIRS "."
    EMBED_TXTFILES "certs/controller_ca.pem"
    PRIV_REQUIRES micromound_c nvs_flash esp_http_client esp-tls mbedtls esp_netif esp_wifi esp_event
                  driver esp_adc esp_timer esp_hw_support protocol_examples_common)
# The serial link (CONFIG_MM_LINK_SERIAL) needs no network component at run time; they are still linked
# so one component list builds both configurations. The size difference is the Wi-Fi/TLS code the
# serial image never calls — measured in the esp32 README.
