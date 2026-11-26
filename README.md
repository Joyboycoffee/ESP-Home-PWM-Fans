# ESPHome PWM Fan Controller

A high-performance, low-overhead desktop hardware monitor and automated PWM fan controller for Windows, communicating wirelessly with ESP8266 (ESP-01) or ESP32 microcontrollers running ESPHome.

Built with **C# .NET 9 WPF**, designed for near-zero resource consumption (<0.1% CPU usage, ~40 MB RAM), native CPU/GPU hardware sensing, dual system tray widgets, and an interactive Bento-grid dashboard.

---

## Key Features

- **Near-Zero Footprint (<0.1% CPU, ~40 MB RAM):**
  - Embedded `LibreHardwareMonitorLib` sensing with SuperIO and storage polling disabled to avoid kernel polling overhead.
  - Periodic memory working set optimization for negligible footprint.
- **Native Dual Hardware Sensing:**
  - **CPU:** AMD Ryzen (Zen 3 / Zen 4 / Zen 5) SMU / MSR direct polling (`Core (Tctl/Tdie)`, `CPU Package`, `CCD1 Tdie`) with fallback to Zen APU SoC and motherboard SuperIO.
  - **GPU:** Dedicated NVIDIA GeForce GPU monitoring via NVAPI (`GPU Core`).
- **Dual Live Taskbar Tray Widgets:**
  - Dynamic GDI+ rendering with clean anti-aliasing directly in the Windows system notification area.
  - **White Glyph:** Current CPU temperature (`CPU: {temp}°C | Fan: {speed}%`).
  - **Green Glyph:** Current GPU temperature (`GPU: {temp}°C (NVIDIA RTX 3070)`).
  - Background click interactions: Double-click to restore/minimize, right-click dark context menu for quick actions (Show, 100% Speed, Exit).
- **Intelligent Step-Curve Automation with Zero-RPM Idle Stop:**
  - Interactive staircase fan response curve with drag-and-drop thresholds.
  - **Zero-RPM Mode:** Automatically powers fans completely OFF (`turn_off`, 0% PWM duty) below 55°C for silent desktop operation.
  - **Configurable Speed Steps:** 55°C (50%), 62°C (65%), 68°C (80%), 73°C+ (100%).
- **Resilient ESPHome REST / Web API Integration:**
  - Wi-Fi HTTP control with keep-alive heartbeat and auto-healing reconnection.
  - Seamless fallback and fail-safe 100% full-speed dispatch on emergency or application shutdown.
- **Modern Bento Dashboard:**
  - Soft-edged Bento grid layout with fluid circular gauge arcs and live operating marker.
  - 3 dynamic themes: **Plum-Obsidian** (Default dark plum), **Minimalist Warm Beige** (Light mode), and **Ultra-Dark AMOLED Pitch**.

---

## Architecture & Hardware

```
[ PC Hardware (Ryzen 5 7600X / RTX 3070) ]
                   │
                   ▼ (LibreHardwareMonitorLib)
[ ESPHomeFanController.exe (.NET 9 WPF) ]
                   │
                   ▼ Wi-Fi REST API (HTTP Basic Auth)
[ ESP-01 / ESP32 (ESPHome Firmware) ]
                   │
                   ▼ 25kHz PWM (GPIO2 / D4)
[ 4-Pin PWM Fans (Silent PC Cooling) ]
```

---

## Project Structure

```
├── Firmware/
│   └── ESP01.yaml                 # ESPHome YAML configuration (25kHz PWM, safe boot timeout)
├── Software/
│   ├── ESPHomeFanController.exe   # Single-file self-contained Windows executable
│   └── ESPHomeFanController/      # C# .NET 9 WPF Source Code
│       ├── Controls/              # Bento UI components (CircularGauge, FanCurveGraph, ToggleSwitch)
│       ├── Models/                # AppSettings, CurvePoint models
│       ├── Services/              # HardwareMonitorService, EspHomeService, FanSpeedController, TrayIcon
│       ├── MainWindow.xaml        # Bento Grid UI
│       └── app.manifest           # Windows DPI awareness & execution privileges
└── README.md
```

---

## Installation & Setup

### 1. ESPHome Firmware Setup
1. Open `Firmware/ESP01.yaml` in ESPHome Dashboard or command-line.
2. Configure your WiFi credentials and static IP / DHCP.
3. Flash the firmware to your ESP-01 or ESP32 board.
4. Connect the PWM output pin (GPIO2 on ESP-01) to the 4-pin fan header PWM pin (Blue wire), and connect ground to PC PSU ground.

### 2. Running the Desktop Controller
1. Download or locate `Software/ESPHomeFanController.exe`.
2. Launch the application.
3. Enter your ESP's IP address (e.g. `192.168.1.40`) in the host bar and click **Connect**.
4. The application saves your settings to `fan_settings.json` automatically in its folder.
5. To run silently on startup without UAC prompts, schedule a task in Windows Task Scheduler set to "Run with highest privileges".

---

## Building from Source

### Prerequisites
- Windows 10/11 (x64)
- [.NET 9.0 SDK](https://dotnet.microsoft.com/download/dotnet/9.0)

```powershell
# Clone the repository
git clone https://github.com/Joyboycoffee/ESP-Home-PWM-Fans.git
cd ESP-Home-PWM-Fans

# Build in Release mode
dotnet build Software/ESPHomeFanController/ESPHomeFanController.csproj -c Release

# Publish single-file executable
dotnet publish Software/ESPHomeFanController/ESPHomeFanController.csproj -c Release -r win-x64 --self-contained true /p:PublishSingleFile=true /p:EnableCompressionInSingleFile=true /p:IncludeNativeLibrariesForSelfExtract=true -o Software/ESPHomeFanController/bin/Publish
```

---

## License

This project is licensed under the MIT License.
