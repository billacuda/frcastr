// frcastr ESP32-S3 temperature/humidity sensor.
//
// Publishes  frcastr/<DEVICE_ID>/state   {"temperature":21.42,"humidity":55.10,"firmware":"1.0.0"}
// Last will  frcastr/<DEVICE_ID>/status  "offline"  (retained)
//
// frcastr auto-registers the device on its first state message; no server-side setup beyond one
// MQTT data source is needed.

#include <Arduino.h>
#include <WiFi.h>
#include <PubSubClient.h>
#include <ArduinoJson.h>

#include "config.h"

#if defined(SENSOR_SHT3X)
  #include <Wire.h>
  #include <Adafruit_SHT31.h>
  static Adafruit_SHT31 sht31 = Adafruit_SHT31();
#elif defined(SENSOR_DHT22)
  #include <DHT.h>
  static DHT dht(DHT_PIN, DHT22);
#else
  #error "Define SENSOR_SHT3X or SENSOR_DHT22 in config.h"
#endif

static WiFiClient wifiClient;
static PubSubClient mqtt(wifiClient);

static char stateTopic[128];
static char statusTopic[128];

static unsigned long lastPublish = 0;
static unsigned long lastReconnectAttempt = 0;
static unsigned int reconnectBackoffMs = 1000;

// ── WiFi ─────────────────────────────────────────────────────────────────────

static void ensureWifi() {
    if (WiFi.status() == WL_CONNECTED) return;

    Serial.printf("WiFi: connecting to %s\n", WIFI_SSID);
    WiFi.mode(WIFI_STA);
    WiFi.begin(WIFI_SSID, WIFI_PASSWORD);

    // Bounded wait; the main loop retries rather than blocking forever.
    unsigned long deadline = millis() + 20000;
    while (WiFi.status() != WL_CONNECTED && millis() < deadline) {
        delay(250);
        Serial.print('.');
    }
    Serial.println();

    if (WiFi.status() == WL_CONNECTED) {
        Serial.printf("WiFi: connected, ip=%s\n", WiFi.localIP().toString().c_str());
    } else {
        Serial.println("WiFi: connect failed; will retry");
    }
}

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

// ── Sensor ───────────────────────────────────────────────────────────────────

static bool readSensor(float& temperatureC, float& humidityPct) {
#if defined(SENSOR_SHT3X)
    temperatureC = sht31.readTemperature();
    humidityPct  = sht31.readHumidity();
#elif defined(SENSOR_DHT22)
    temperatureC = dht.readTemperature();
    humidityPct  = dht.readHumidity();
#endif
    // Both libraries signal a failed read with NaN.
    return !isnan(temperatureC) && !isnan(humidityPct);
}

static void publishReading() {
    float temperatureC, humidityPct;
    if (!readSensor(temperatureC, humidityPct)) {
        Serial.println("Sensor: read failed; skipping publish");
        return;
    }

    JsonDocument doc;
    doc["temperature"] = round(temperatureC * 100.0f) / 100.0f;
    doc["humidity"]    = round(humidityPct * 100.0f) / 100.0f;
    doc["firmware"]    = FIRMWARE_VERSION;

    char payload[192];
    size_t len = serializeJson(doc, payload, sizeof(payload));

    if (mqtt.publish(stateTopic, (const uint8_t*)payload, len, /*retained*/ false)) {
        Serial.printf("Published: %s\n", payload);
    } else {
        Serial.println("MQTT: publish failed");
    }
}

// ── Lifecycle ────────────────────────────────────────────────────────────────

void setup() {
    Serial.begin(115200);
    delay(500);
    Serial.printf("\nfrcastr sensor %s (device %s)\n", FIRMWARE_VERSION, DEVICE_ID);

    snprintf(stateTopic,  sizeof(stateTopic),  "%s/%s/state",  MQTT_TOPIC_ROOT, DEVICE_ID);
    snprintf(statusTopic, sizeof(statusTopic), "%s/%s/status", MQTT_TOPIC_ROOT, DEVICE_ID);

#if defined(SENSOR_SHT3X)
    Wire.begin(I2C_SDA_PIN, I2C_SCL_PIN);
    if (!sht31.begin(SHT3X_ADDRESS)) {
        Serial.println("Sensor: SHT3x not found — check wiring and address");
    }
#elif defined(SENSOR_DHT22)
    dht.begin();
#endif

    mqtt.setServer(MQTT_HOST, MQTT_PORT);
    mqtt.setBufferSize(512);
    mqtt.setKeepAlive(60);

    ensureWifi();
    connectMqtt();

    // Publish immediately rather than waiting out the first interval.
    lastPublish = millis() - PUBLISH_INTERVAL_MS;
}

void loop() {
    ensureWifi();
    ensureMqtt();
    mqtt.loop();

    unsigned long now = millis();
    if (mqtt.connected() && now - lastPublish >= PUBLISH_INTERVAL_MS) {
        lastPublish = now;
        publishReading();
    }

    delay(50);
}
