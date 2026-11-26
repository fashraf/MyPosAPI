# MyPOS order light (ESP32-S3-DevKitC-1)

Blinks green on the board's RGB LED every time an order is placed.

**How it works:** when a sale commits, the API puts it in an in-memory order feed. The board asks
`GET /api/device/order-feed` every few seconds with its device key. That endpoint reads memory only and never queries the
database, and board polls are not written to the audit trail.

## 1. Give the server a device key (once per environment)
On the server, in PowerShell as Administrator (staging shown; for production use `POSprod`):

```powershell
$f = 'C:\Project\POS\POSStageing\config\mypos.json'
$c = Get-Content $f -Raw | ConvertFrom-Json
$key = -join ((48..57) + (97..122) | Get-Random -Count 32 | ForEach-Object { [char]$_ })
$c | Add-Member -NotePropertyName orderLightKey -NotePropertyValue $key -Force
$c | Add-Member -NotePropertyName orderLightPollSeconds -NotePropertyValue 10 -Force
$c | ConvertTo-Json | Set-Content -Encoding UTF8 $f
"Order light key: $key"
```

The key reaches the API on the next API deploy. To change how often boards check, edit `orderLightPollSeconds`; boards
pick up the new interval on their next check, with no reflashing.

## 2. Flash the board
1. Arduino IDE 2.x → Boards Manager → install **esp32** by Espressif → board **ESP32S3 Dev Module**.
2. Library Manager → install **ArduinoJson** (v7).
3. Open `order-light.ino`, fill in `WIFI_SSID`, `WIFI_PASSWORD`, `API_BASE` and `DEVICE_KEY`.
4. Upload, then open Serial Monitor at 115200.

## 3. What you see
| LED | Meaning |
|---|---|
| green ×2 | a new order (one burst per order, up to 5 in a row) |
| dim blue blinking | connecting to Wi-Fi |
| red flash | API not reachable or wrong key (Serial Monitor says which); keeps retrying |
| off | idle, all good |

The LED pin is GPIO 48 on DevKitC-1 v1.0 and GPIO 38 on v1.1. If nothing lights, change `LED_PIN`.

## Test without a board
```bash
curl -H "X-Device-Key: YOUR-KEY" http://144.91.103.111:8080/api/device/order-feed
```
