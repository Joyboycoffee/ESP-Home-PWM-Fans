# Pwm Fan controller via ESPHome using Esp01 or Esp32

Short: silent MOSFET-based high-side fan controller driven by ESP (ESP32/ESP01), wiring diagrams, ESPHome config and test notes. (the firmware (yaml) will work for low side switching too but needs different mosfet and hardware)

## Contents
- `firmware/` - ESPHome YAML and build notes
- `hardware/` - wiring diagrams, photos
- `docs/` - safety and test logs

## Build & flash (local)
1. Install ESPHome (pip or Home Assistant add-on).
2. Compile: esphome compile esp01.yaml


## Safety notes
- Use a fuse on the +12 V rail.
- Test with 1 fan first.
- Keep common ground between MCU and 12 V PSU.
- If anything goes wrong or it becomes a fire hazard somehow i won't be responsible
- Do it on your own risk 

## License
MIT
