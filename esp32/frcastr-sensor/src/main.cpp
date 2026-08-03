// frcastr ESP32-S3 temperature/humidity sensor.
//
// Publishes  frcastr/<DEVICE_ID>/state   {"temperature":21.42,"humidity":55.10,"firmware":"1.1.0"}
// Last will  frcastr/<DEVICE_ID>/status  "offline"  (retained)
//
// frcastr auto-registers the device on its first state message; no server-side setup beyond one
// MQTT data source is needed.
//
// With DEEP_SLEEP_ENABLED the board runs as a duty cycle rather than a loop: wake, read, publish,
// sleep. setup() ends in esp_deep_sleep_start() and loop() never runs — a timer wake restarts the
// sketch from setup(). Only RTC_DATA_ATTR state survives a cycle.

#include <Arduino.h>
#include <WiFi.h>
#include <PubSubClient.h>
#include <ArduinoJson.h>
#include <esp_sleep.h>

#include "config.h"

#if defined(SENSOR_SHT3X)
  #include <Wire.h>
  #include <Adafruit_SHT31.h>
  static Adafruit_SHT31 sht31 = Adafruit_SHT31();
#elif defined(SENSOR_DHT22)
  #include <DHT.h>
  static DHT dht(DHT_PIN, DHT22);
#elif defined(SENSOR_DS18B20)
  #include <OneWire.h>
  #include <DallasTemperature.h>
  static OneWire oneWire(DS18B20_PIN);
  static DallasTemperature ds18b20(&oneWire);
  // Power-on value of the DS18B20 scratchpad. A sensor that answers on the bus but never
  // finishes a conversion reads this forever, so it means "no measurement yet", not 85 C.
  // Exact comparison is safe: the part reports sixteenths, and 1360/16 is exactly 85.
  static constexpr float DS18B20_RESET_VALUE_C  = 85.0f;
  static constexpr int   DS18B20_READ_ATTEMPTS  = 3;
#else
  #error "Define SENSOR_SHT3X, SENSOR_DHT22, or SENSOR_DS18B20 in config.h"
#endif

static WiFiClient wifiClient;
static PubSubClient mqtt(wifiClient);

static char stateTopic[128];
static char statusTopic[128];

#if DEEP_SLEEP_ENABLED
// Kept in RTC memory across deep sleep. Re-associating with a known BSSID and channel skips the
// scan, which is a second or more of radio time — and radio time is heat — saved every wake.
RTC_DATA_ATTR static uint32_t wakeCount  = 0;
RTC_DATA_ATTR static bool     apKnown    = false;
RTC_DATA_ATTR static uint8_t  apBssid[6] = {0};
RTC_DATA_ATTR static int32_t  apChannel  = 0;
#else
static unsigned long lastPublish = 0;
static unsigned long lastReconnectAttempt = 0;
static unsigned int reconnectBackoffMs = 1000;
#endif

// millis()-rollover-safe "is this deadline still in the future?"
static inline bool notYet(unsigned long deadline) {
    return (long)(millis() - deadline) < 0;
}

// ── WiFi ─────────────────────────────────────────────────────────────────────

static void beginWifi() {
    Serial.printf("WiFi: connecting to %s\n", WIFI_SSID);
    WiFi.mode(WIFI_STA);
    WiFi.setSleep(false);

#if DEEP_SLEEP_ENABLED
    if (apKnown) {
        // Same AP as the last wake: associate directly instead of scanning every channel.
        WiFi.begin(WIFI_SSID, WIFI_PASSWORD, apChannel, apBssid);
        return;
    }
#endif
    WiFi.begin(WIFI_SSID, WIFI_PASSWORD);
}

static bool waitForWifi(unsigned long deadline) {
    while (WiFi.status() != WL_CONNECTED && notYet(deadline)) {
        delay(10);
    }
    bool connected = WiFi.status() == WL_CONNECTED;

#if DEEP_SLEEP_ENABLED
    if (connected) {
        memcpy(apBssid, WiFi.BSSID(), sizeof(apBssid));
        apChannel = WiFi.channel();
        apKnown   = true;
    } else {
        // A cached BSSID that no longer answers is the usual cause, so scan again next wake.
        apKnown = false;
    }
#endif

    if (connected) {
        Serial.printf("WiFi: connected, ip=%s rssi=%d\n",
                      WiFi.localIP().toString().c_str(), WiFi.RSSI());
    } else {
        Serial.println("WiFi: connect failed");
    }
    return connected;
}

#if !DEEP_SLEEP_ENABLED
static void ensureWifi() {
    if (WiFi.status() == WL_CONNECTED) return;

    // Bounded wait; the main loop retries rather than blocking forever.
    beginWifi();
    waitForWifi(millis() + 20000);
}
#endif

// ── MQTT ─────────────────────────────────────────────────────────────────────

static bool connectMqtt() {
    Serial.printf("MQTT: connecting to %s:%d as %s\n", MQTT_HOST, MQTT_PORT, DEVICE_ID);

    const char* user = strlen(MQTT_USER) > 0 ? MQTT_USER : nullptr;
    const char* pass = strlen(MQTT_PASSWORD) > 0 ? MQTT_PASSWORD : nullptr;

    // Last will: the broker publishes "offline" (retained) if this device drops off
    // ungracefully, which frcastr picks up on the status topic.
    bool ok = mqtt.connect(DEVICE_ID, user, pass,
                           statusTopic, /*willQos*/ 1, /*willRetain*/ true, "offline",
                           /*cleanSession*/ true);

    if (!ok) {
        Serial.printf("MQTT: connect failed, state=%d\n", mqtt.state());
        return false;
    }

    mqtt.publish(statusTopic, "online", /*retained*/ true);
    Serial.printf("MQTT: connected, publishing to %s\n", stateTopic);
    return true;
}

#if !DEEP_SLEEP_ENABLED
static void ensureMqtt() {
    if (mqtt.connected()) return;

    unsigned long now = millis();
    if (now - lastReconnectAttempt < reconnectBackoffMs) return;
    lastReconnectAttempt = now;

    if (connectMqtt()) {
        reconnectBackoffMs = 1000;
    } else {
        // Back off to 30 s so a down broker does not hammer the network.
        reconnectBackoffMs = min<unsigned int>(reconnectBackoffMs * 2, 30000);
    }
}
#endif

// ── Sensor ───────────────────────────────────────────────────────────────────

static void beginSensor() {
#if defined(SENSOR_SHT3X)
    Wire.begin(I2C_SDA_PIN, I2C_SCL_PIN);
    if (!sht31.begin(SHT3X_ADDRESS)) {
        Serial.println("Sensor: SHT3x not found — check wiring and address");
    }
#elif defined(SENSOR_DHT22)
    dht.begin();
#elif defined(SENSOR_DS18B20)
    ds18b20.begin();
#endif
}

static bool readSensor(float& temperatureC, float& humidityPct) {
    humidityPct = NAN;
#if defined(SENSOR_SHT3X)
    temperatureC = sht31.readTemperature();
    humidityPct  = sht31.readHumidity();
    return !isnan(temperatureC) && !isnan(humidityPct);
#elif defined(SENSOR_DHT22)
    temperatureC = dht.readTemperature();
    humidityPct  = dht.readHumidity();
    return !isnan(temperatureC) && !isnan(humidityPct);
#elif defined(SENSOR_DS18B20)
    // DS18B20 is temperature-only; humidityPct stays NaN and is left out of the payload.
    //
    // A stuck conversion returns the 85 C reset value, which passes every range check and would
    // otherwise be published as a real reading. Retry it: a genuinely cold first conversion
    // clears on the next attempt, while a wiring fault (usually a missing 4.7k pull-up on the
    // data line, sometimes a marginal supply) stays at 85 and is reported as a failed read.
    for (int attempt = 1; attempt <= DS18B20_READ_ATTEMPTS; attempt++) {
        ds18b20.requestTemperatures();  // blocks until the conversion completes
        temperatureC = ds18b20.getTempCByIndex(0);

        if (temperatureC == DEVICE_DISCONNECTED_C) return false;
        if (temperatureC != DS18B20_RESET_VALUE_C) return true;

        if (attempt < DS18B20_READ_ATTEMPTS) delay(200);
    }

    Serial.println("Sensor: DS18B20 stuck at 85.00 C (reset value) — "
                   "check the 4.7k pull-up on the data line and the 3.3 V supply");
    return false;
#endif
}

static void publishReading(float temperatureC, float humidityPct) {
    JsonDocument doc;
    doc["temperature"] = round(temperatureC * 100.0f) / 100.0f;
    if (!isnan(humidityPct)) {
        doc["humidity"] = round(humidityPct * 100.0f) / 100.0f;
    }
    doc["firmware"]    = FIRMWARE_VERSION;

    char payload[192];
    size_t len = serializeJson(doc, payload, sizeof(payload));

    if (mqtt.publish(stateTopic, (const uint8_t*)payload, len, /*retained*/ false)) {
        Serial.printf("Published: %s\n", payload);
    } else {
        Serial.println("MQTT: publish failed");
    }
}

// ── Duty cycle ───────────────────────────────────────────────────────────────

#if DEEP_SLEEP_ENABLED

static bool readSensorSettled(float& temperatureC, float& humidityPct) {
    for (int attempt = 1; attempt <= SENSOR_READ_ATTEMPTS; attempt++) {
        if (readSensor(temperatureC, humidityPct)) return true;

        Serial.printf("Sensor: read %d/%d failed\n", attempt, SENSOR_READ_ATTEMPTS);
        if (attempt < SENSOR_READ_ATTEMPTS) {
            delay(2100);  // a DHT22 will not produce a fresh sample any sooner than every 2 s
        }
    }
    return false;
}

[[noreturn]] static void sleepUntilNextReading(unsigned long cycleStart) {
    // Clean DISCONNECT: the broker discards the last will, so the retained status stays "online"
    // through the sleep instead of flapping offline every five minutes. The trade-off is that a
    // board which dies while asleep never triggers its will — frcastr has to notice it by the
    // readings going stale.
    if (mqtt.connected()) {
        mqtt.disconnect();
    }
    WiFi.disconnect(/*wifioff*/ true, /*eraseap*/ false);

    Serial.printf("Cycle done: awake %lu ms; sleeping %lu ms\n",
                  millis() - cycleStart, (unsigned long)SLEEP_INTERVAL_MS);
    Serial.flush();

    esp_sleep_enable_timer_wakeup((uint64_t)SLEEP_INTERVAL_MS * 1000ULL);
    esp_deep_sleep_start();  // does not return; the next wake re-enters setup()
}

static void runReadingCycle(unsigned long cycleStart) {
    // Association and the sensor's settling time both cost a second or two, so overlap them:
    // start the radio, let the sensor settle while it associates, then read.
    beginWifi();

    while (notYet(cycleStart + SENSOR_WARMUP_MS)) {
        delay(10);
    }

    float temperatureC = NAN, humidityPct = NAN;
    if (!readSensorSettled(temperatureC, humidityPct)) {
        // Nothing worth sending — go straight back to sleep rather than spend the awake
        // budget (and the heat) getting onto the network for no payload. Marked [[noreturn]]
        // above, so the fall-through past this point is unreachable rather than a publish of
        // whatever the failed read left behind.
        sleepUntilNextReading(cycleStart);
    }

    if (waitForWifi(cycleStart + AWAKE_BUDGET_MS) && connectMqtt()) {
        publishReading(temperatureC, humidityPct);
        mqtt.loop();
        delay(20);  // let the publish leave the socket before the DISCONNECT
    }

    sleepUntilNextReading(cycleStart);
}

#endif

// ── Lifecycle ────────────────────────────────────────────────────────────────

void setup() {
#if DEEP_SLEEP_ENABLED
    const unsigned long cycleStart = millis();
#endif

    Serial.begin(115200);
#if !DEEP_SLEEP_ENABLED
    delay(1000);
#endif
    Serial.printf("\nfrcastr sensor %s (device %s)\n", FIRMWARE_VERSION, DEVICE_ID);

    snprintf(stateTopic,  sizeof(stateTopic),  "%s/%s/state",  MQTT_TOPIC_ROOT, DEVICE_ID);
    snprintf(statusTopic, sizeof(statusTopic), "%s/%s/status", MQTT_TOPIC_ROOT, DEVICE_ID);

    beginSensor();

    mqtt.setServer(MQTT_HOST, MQTT_PORT);
    mqtt.setBufferSize(512);
    mqtt.setKeepAlive(60);

#if DEEP_SLEEP_ENABLED
    Serial.printf("Wake #%lu (duty cycle: %lu ms asleep between readings)\n",
                  (unsigned long)++wakeCount, (unsigned long)SLEEP_INTERVAL_MS);
    runReadingCycle(cycleStart);  // ends in deep sleep; never returns
#else
    ensureWifi();
    connectMqtt();

    // Publish immediately rather than waiting out the first interval.
    lastPublish = millis() - PUBLISH_INTERVAL_MS;
#endif
}

void loop() {
#if DEEP_SLEEP_ENABLED
    // Unreachable: setup() always ends in deep sleep, and a timer wake restarts at setup().
#else
    ensureWifi();
    ensureMqtt();
    mqtt.loop();

    unsigned long now = millis();
    if (mqtt.connected() && now - lastPublish >= PUBLISH_INTERVAL_MS) {
        lastPublish = now;

        float temperatureC, humidityPct;
        if (readSensor(temperatureC, humidityPct)) {
            publishReading(temperatureC, humidityPct);
        } else {
            Serial.println("Sensor: read failed; skipping publish");
        }
    }

    delay(50);
#endif
}
