# MicroMound constrained controller — ESP-IDF project (ROADMAP M5).
# Hosts firmware/micromound-c (the protocol mirror, kernel, device loop and board layer, all
# host-tested) on an ESP32-class board. Wi-Fi station bring-up comes from ESP-IDF's own
# protocol_examples_common component (CONFIG_EXAMPLE_WIFI_SSID / _PASSWORD in menuconfig).
cmake_minimum_required(VERSION 3.16)

set(EXTRA_COMPONENT_DIRS $ENV{IDF_PATH}/examples/common_components/protocol_examples_common)

include($ENV{IDF_PATH}/tools/cmake/project.cmake)
project(micromound_esp32)
