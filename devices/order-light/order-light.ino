/*
 * MyPOS order light — ESP32-S3-DevKitC-1
 *
 * Blinks the on-board RGB LED green every time a new order is placed.
 * It polls the API's in-memory order feed (GET /api/device/order-feed); the feed never queries the database.
 *
 * Power-on self-test: red, green, blue, then a short beep. If you see no colours, LED_PIN is wrong (try 38).
 *
 * LED colours
 *   green blink x2  a new order (one burst per order, up to 5 in a row) + beep
 *   cyan blink x3   "Test the light" pressed in the back office (or the /ping test)
 *   dim blue        connecting to Wi-Fi
 *   off             idle, all good
 *   red flash       could not reach the API (wrong key, server down, no internet); it keeps retrying
 *
 * Sound: the board has no speaker. Wire an ACTIVE buzzer (3.3-5 V, e.g. "KY-012") between BUZZER_PIN and GND.
 *
 * Setup (Arduino IDE 2.x):
 *   1. Boards Manager: install "esp32" by Espressif (2.0.x or 3.x). Board: "ESP32S3 Dev Module".
 *   2. Library Manager: install "ArduinoJson" by Benoit Blanchon (v7).
 *   3. Fill in the SETTINGS below, plug the board in by the port marked "USB" or "UART", press Upload.
 *   4. Tools > Serial Monitor at 115200 to watch what it does.
 */

#include <Arduino.h>   // needed when this file is used as PlatformIO src/main.cpp
#include <WiFi.h>
#include <HTTPClient.h>
#include <ArduinoJson.h>

/* ============================== SETTINGS ============================== */
const char* WIFI_SSID     = "YOUR-WIFI-NAME";
const char* WIFI_PASSWORD = "YOUR-WIFI-PASSWORD";

/* Staging: http://144.91.103.111:8080   Production: http://144.91.103.111 */
const char* API_BASE      = "http://144.91.103.111:8080";
const char* DEVICE_KEY    = "PASTE-THE-ORDER-LIGHT-KEY-HERE";   // the "orderLightKey" from the server's config\mypos.json

/* How often to check, in seconds. The server can override it ("orderLightPollSeconds" in mypos.json) without reflashing. */
uint32_t pollSeconds      = 10;
const bool USE_SERVER_POLL_SECONDS = true;

/* On-board RGB LED: GPIO 48 on DevKitC-1 v1.0, GPIO 38 on v1.1 (printed on the board's back). */
const uint8_t LED_PIN     = 48;
const uint8_t BRIGHTNESS  = 60;   // 0-255; the LED is very bright at 255

/* Buzzer: + leg to this pin, - leg to GND. Set to -1 if you have no buzzer. */
const int     BUZZER_PIN  = 4;
/* ===================================================================== */

long    lastSeq = -1;     // last invoice number seen; -1: not synced yet (first answer only sets the position, no blink)
String  bootId = "";
int     testId = -1;

void led(uint8_t r, uint8_t g, uint8_t b) {
#if ESP_ARDUINO_VERSION_MAJOR >= 3
  rgbLedWrite(LED_PIN, r, g, b);
#else
  neopixelWrite(LED_PIN, r, g, b);
#endif
}

void blinkGreen(int times) {
  for (int i = 0; i < times; i++) {
    led(0, BRIGHTNESS, 0); delay(180);
    led(0, 0, 0);          delay(180);
  }
}

void beep(int ms) {
  if (BUZZER_PIN < 0) return;
  digitalWrite(BUZZER_PIN, HIGH); delay(ms); digitalWrite(BUZZER_PIN, LOW);
}

void newOrderSignal() { beep(120); blinkGreen(2); }

void testSignal() {
  for (int i = 0; i < 3; i++) { led(0, BRIGHTNESS, BRIGHTNESS); beep(60); delay(140); led(0, 0, 0); delay(180); }
}

void selfTest() {
  led(BRIGHTNESS, 0, 0); delay(400);
  led(0, BRIGHTNESS, 0); delay(400);
  led(0, 0, BRIGHTNESS); delay(400);
  led(0, 0, 0); beep(80);
}

void flashRed() { led(BRIGHTNESS, 0, 0); delay(150); led(0, 0, 0); }

void connectWifi() {
  if (WiFi.status() == WL_CONNECTED) return;
  Serial.printf("Wi-Fi: connecting to %s", WIFI_SSID);
  WiFi.mode(WIFI_STA);
  WiFi.begin(WIFI_SSID, WIFI_PASSWORD);
  uint32_t started = millis();
  while (WiFi.status() != WL_CONNECTED) {
    led(0, 0, 12); delay(250); led(0, 0, 0); delay(250);
    Serial.print(".");
    if (millis() - started > 20000) {   // give up for now; the loop tries again
      Serial.println(" failed");
      flashRed();
      return;
    }
  }
  Serial.printf(" connected, IP %s\n", WiFi.localIP().toString().c_str());
}

void poll() {
  String url = String(API_BASE) + "/api/device/order-feed";
  if (lastSeq >= 0) url += "?since=" + String(lastSeq);

  HTTPClient http;
  http.setTimeout(8000);
  http.begin(url);
  http.addHeader("X-Device-Key", DEVICE_KEY);
  int status = http.GET();

  if (status != 200) {
    Serial.printf("API answered %d (%s)\n", status, status == 401 ? "wrong device key" : status == 503 ? "order light not set up on the server" : "server unreachable?");
    http.end();
    flashRed();
    return;
  }

  JsonDocument doc;
  DeserializationError err = deserializeJson(doc, http.getStream());
  http.end();
  if (err) { Serial.printf("Bad answer: %s\n", err.c_str()); flashRed(); return; }

  String serverBoot = doc["bootId"].as<String>();
  long   serverSeq  = doc["lastSeq"].as<long>();
  int    newOrders  = doc["newOrders"] | 0;
  if (USE_SERVER_POLL_SECONDS && doc["pollSeconds"].is<int>()) pollSeconds = doc["pollSeconds"].as<int>();

  int serverTest = doc["testId"] | 0;
  /* Test blink: only when the same server run reports a new test number (not on first contact or after a restart). */
  if (testId >= 0 && serverBoot == bootId && serverTest != testId) { Serial.println("Test signal"); testSignal(); }
  testId = serverTest;

  if (lastSeq < 0) {
    Serial.printf("Synced: last invoice %ld\n", serverSeq);
  } else if (newOrders > 0) {
    for (JsonObject o : doc["orders"].as<JsonArray>()) {
      Serial.printf("New order #%d  invoice %ld  SAR %.2f  (%s)\n",
                    (int)o["orderNo"], (long)o["invoice"], (double)o["totalSar"], (const char*)(o["orderType"] | ""));
    }
    for (int i = 0; i < min(newOrders, 5); i++) { newOrderSignal(); delay(250); }
  }
  bootId  = serverBoot;
  lastSeq = serverSeq;
}

void setup() {
  Serial.begin(115200);
  delay(300);
  if (BUZZER_PIN >= 0) { pinMode(BUZZER_PIN, OUTPUT); digitalWrite(BUZZER_PIN, LOW); }
  Serial.println("\nMyPOS order light: self-test (red, green, blue, beep)");
  selfTest();
  connectWifi();
}

void loop() {
  connectWifi();
  if (WiFi.status() == WL_CONNECTED) poll();
  delay(pollSeconds * 1000UL);
}
