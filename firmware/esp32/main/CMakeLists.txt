idf_component_register(
    SRCS "app_main.c" "hal_esp32.c" "board.c"
    INCLUDE_DIRS "."
    EMBED_TXTFILES "certs/controller_ca.pem"
    PRIV_REQUIRES micromound_c nvs_flash esp_http_client esp-tls mbedtls esp_netif esp_wifi esp_event
                  driver esp_adc esp_timer esp_hw_support protocol_examples_common)
