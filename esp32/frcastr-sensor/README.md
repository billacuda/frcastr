# frcastr ESP32-S3 sensor

Reference firmware for an ESP32-S3 publishing temperature and humidity to frcastr over MQTT.
Flash the same sketch to as many boards as you like — give each one a unique `DEVICE_ID` and
frcastr registers them automatically.

> Reference code: it compiles against the pinned libraries but has not been verified on hardware.
> Check the pin assignments against your board before flashing.

## Device contract

| | |
|---|---|
| State topic | `frcastr/<DEVICE_ID>/state` |
| State payload | `{"temperature":21.42,"humidity":55.10,"firmware":"1.0.0"}` |
| Status topic | `frcastr/<DEVICE_ID>/status` (retained) |
| Status payload | `online` on connect, `offline` via MQTT last will |

Only the fields listed in the source's `fieldMapping` are stored; anything else is ignored, so you
can add fields to the payload without touching the server. `temperature` is °C and `humidity` is
relative humidity in percent.

## Wiring

**SHT3x (default, I²C)** — `SDA` → GPIO 8, `SCL` → GPIO 9, `VIN` → 3V3, `GND` → GND.
Set `SHT3X_ADDRESS` to `0x45` if the ADDR pin is tied high.

**DHT22** — data → GPIO 4 with a 10 kΩ pull-up to 3V3. Comment out `SENSOR_SHT3X` and uncomment
`SENSOR_DHT22` in `config.h`.

## Build and flash

```sh
cd esp32/frcastr-sensor
cp include/config.h.example include/config.h
# edit include/config.h: DEVICE_ID, WiFi, broker
pio run -t upload
pio device monitor
```

Repeat per board, changing only `DEVICE_ID`. `config.h` is gitignored so credentials stay local.

## frcastr setup

One MQTT data source serves every device. In **Admin → Data Sources**, add a source, choose the
**ESP32 sensors over MQTT (multi-device)** preset, and set the broker address:

```json
{
  "broker": "192.168.1.50",
  "port": 1883,
  "topicPattern": "frcastr/{device}/state",
  "statusTopicPattern": "frcastr/{device}/status",
  "autoRegisterDevices": true,
  "fieldMapping": {
    "temperature": { "channel": "temperature.indoor", "unit": "°C" },
    "humidity":    { "channel": "humidity.indoor",    "unit": "%"  }
  }
}
```

The source connects within 30 seconds — no restart needed. Devices then appear under
**Admin → Devices** as they publish, and their readings are addressable as
`temperature.indoor@greenhouse-01`.

Mark one device **primary** if you want its readings to also answer to the bare channel name
(`temperature.indoor`), which is what widgets bind to by default.

## Troubleshooting

- **Nothing appears under Devices** — use the **Test** button on the data source. It reports the
  filter it subscribed to, whether each message matched `topicPattern`, and which channels it
  resolved. A `matchesPattern: false` means the topic and pattern disagree.
- **Device appears but has no readings** — the payload field names must match the `fieldMapping`
  keys exactly. Values outside the sanity bounds for the mapped channel are dropped and logged.
- **Readings stop** — check `frcastr/<id>/status`. A retained `offline` means the broker saw the
  device drop; frcastr emails a device-offline alert once the threshold on the device passes.
