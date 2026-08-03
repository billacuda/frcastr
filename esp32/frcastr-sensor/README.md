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
| Cadence | one reading every `SLEEP_INTERVAL_MS` (default 5 min) |

Only the fields listed in the source's `fieldMapping` are stored; anything else is ignored, so you
can add fields to the payload without touching the server. `temperature` is °C and `humidity` is
relative humidity in percent. DS18B20 is temperature-only, so its payload omits `humidity` entirely
rather than sending a bogus value.

## Wiring

**SHT3x (default, I²C)** — `SDA` → GPIO 8, `SCL` → GPIO 9, `VIN` → 3V3, `GND` → GND.
Set `SHT3X_ADDRESS` to `0x45` if the ADDR pin is tied high.

**DHT22** — data → GPIO 4 with a 10 kΩ pull-up to 3V3. Comment out `SENSOR_SHT3X` and uncomment
`SENSOR_DHT22` in `config.h`.

**DS18B20** — data → GPIO 4 with a 4.7 kΩ pull-up between data and 3V3 (required — the bus won't
work without it). `VDD` → 3V3, `GND` → GND. Comment out `SENSOR_SHT3X` and uncomment
`SENSOR_DS18B20` in `config.h`. Multiple DS18B20s can share the same bus/pin, but this firmware
only reads the first one it finds (`getTempCByIndex(0)`).

## Deep sleep

By default the board runs a duty cycle rather than a continuous loop: wake, read, publish, sleep
five minutes, repeat. An ESP32-S3 left running flat out warms its own board by several degrees, and
a DHT22 mounted near it reports that heat as room temperature — sleeping between readings is what
keeps the sensor measuring the room instead of the regulator.

A cycle is roughly 3–5 s awake, most of it WiFi association:

1. Start the radio, then wait out `SENSOR_WARMUP_MS` (2 s) so the sensor settles while the radio
   associates — the two overlap instead of running back to back.
2. Read the sensor, retrying up to `SENSOR_READ_ATTEMPTS` times (a cold DHT22 often misses its
   first read). A failed read skips the publish and sleeps immediately rather than spending the
   awake budget getting onto the network for nothing.
3. Connect, publish, disconnect cleanly, sleep. `AWAKE_BUDGET_MS` (15 s) caps the whole cycle, so
   a down broker or a missing AP costs one wake, not a board that stays up burning power.

The AP's BSSID and channel are cached in RTC memory, so a wake re-associates without a full scan.

Two consequences worth knowing:

- **Status stays `online` between readings.** The board sends a proper MQTT `DISCONNECT` before
  sleeping, so the broker discards the last will instead of flapping the device offline every five
  minutes. The trade-off: a board that dies while asleep never fires its will, so frcastr notices
  it by the readings going stale rather than by a retained `offline`.
- **Serial output is per-wake.** `pio device monitor` shows a fresh banner and `Wake #n` each
  cycle, then nothing for five minutes. That is the sketch restarting from `setup()`, not a crash.

Set `DEEP_SLEEP_ENABLED` to `0` in `config.h` for the old always-on behaviour (publish every
`PUBLISH_INTERVAL_MS`), which is fine for a DS18B20 on a probe lead or any sensor far enough from
the board that self-heating doesn't reach it.

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
  device drop; frcastr emails a device-offline alert once the threshold on the device passes. With
  deep sleep enabled the status can still read `online` for a dead board, so set the device's
  offline threshold above `SLEEP_INTERVAL_MS` — a couple of missed cycles — and let staleness catch
  it. That threshold governs both the alert email and the dashboard's stale marker, so a board on a
  five-minute cycle stops being flagged between readings.
- **A sleeping board is hard to reflash** — it only listens for a few seconds per cycle. Hold BOOT
  while tapping RESET to force the bootloader before `pio run -t upload`.
