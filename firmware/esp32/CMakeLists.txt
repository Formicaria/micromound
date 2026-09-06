# MicroMound constrained controller — ESP-IDF project (ROADMAP M5).
# Hosts firmware/micromound-c (the protocol mirror, kernel, device loop and board layer, all
# host-tested) on an ESP32-class board. Wi-Fi station bring-up comes from ESP-IDF's own
# protocol_examples_common component (CONFIG_EXAMPLE_WIFI_SSID / _PASSWORD in menuconfig).
cmake_minimum_required(VERSION 3.16)

set(EXTRA_COMPONENT_DIRS $ENV{IDF_PATH}/examples/common_components/protocol_examples_common)

include($ENV{IDF_PATH}/tools/cmake/project.cmake)

# The library's capacities, project-wide so every component agrees on sizeof(mm_app): an uplink
# queue of 8 envelopes (a beat and seven records; a full queue refuses to record, audited) keeps
# the static mm_app near 40 KB and leaves DRAM for Wi-Fi and the TLS handshake.
idf_build_set_property(COMPILE_DEFINITIONS "-DMM_DEVICE_QUEUE=8" APPEND)
project(micromound_esp32)
