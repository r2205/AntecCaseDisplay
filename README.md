# AntecCaseDisplay

A Windows tray app that drives the CPU/GPU temperature LCD on the **Antec Flux
Pro** case, reading the temperatures from **HWiNFO64** via its shared memory
interface. Replacement for Antec's iUnity software.

## Features

- Lives in the system tray; click the icon for settings
- Reads any HWiNFO temperature/fan/clock/power sensor — pick from a live
  drop-down, no JSON editing required
- Multi-sensor matching with Average / Max / Min / First aggregation
- Adjustable refresh rate (200 ms – 10 s slider)
- Optional integer-only display (no decimals)
- Threshold alerts via tray notifications, with a per-alert cooldown
- Optional log file (auto-rotates at 5 MB)
- Light / Dark / System theme
- Optional "start with Windows" and "start minimised"
- Pause / Resume from the tray menu without quitting

If you just want the simple no-GUI version, check out the `v1.0-cli` tag
(commit `e602170`).

## Requirements

- Windows 10 or 11 x64
- [.NET 8 SDK](https://dotnet.microsoft.com/download) (to build from source)
- [HWiNFO64](https://www.hwinfo.com/) running, with **Settings → Safety →
  Enable Shared Memory Support** ticked. (Shared memory is time-limited on the
  free edition; unlimited on HWiNFO64 Pro.)
- The Antec Flux Pro internal USB cable plugged into a motherboard USB 2.0
  header so Windows enumerates the display as a HID device

## Build

```powershell
dotnet build -c Release
```

The output exe is in `AntecCaseDisplay\bin\Release\net8.0-windows\`.

To produce a single self-contained exe:

```powershell
dotnet publish AntecCaseDisplay\AntecCaseDisplay.csproj -c Release -r win-x64 `
  --self-contained true /p:PublishSingleFile=true
```

## Run

1. Start HWiNFO64 (and keep it running in the background).
2. Launch `AntecCaseDisplay.exe`.
   - On first run a default `appsettings.json` is written next to the exe.
   - The app starts minimised to the tray. Click the tray icon (the blue "A")
     to open settings.

### Settings window

- **CPU / GPU display slot** — for each of the two slots:
  - *Sensor type*: Temperature, Fan, Clock, Usage, Power, ...
  - *Sensor*: live drop-down of every matching sensor HWiNFO is currently
    reporting. Picking one auto-fills the regex below.
  - *Pattern (regex)*: edit by hand to match multiple sensors (e.g. all CPU
    core temps).
  - *Aggregation*: Average / Max / Min / First — how to combine multiple
    matched sensors into one number.
  - *Scale*: multiplier applied before sending. Use `0.01` to fit fan RPM into
    the 0–99 display range.
  - *Alert above*: tray notification fires when this value is exceeded
    (blank = disabled).
- **Update behaviour**:
  - *Refresh interval* slider (200 ms – 10 s)
  - *Reconnect interval* (how long to wait before retrying when HWiNFO or the
    display disappears)
  - *Round to whole degrees* — sends X.0 instead of X.Y
  - *Verbose logging* — one log line per frame
- **Alerts** — enable, with a cooldown to avoid spam
- **Logging** — write events to a file (auto-rotates at 5 MB, keeps one
  backup as `name.log.1`)
- **Appearance and startup** — Light / Dark / System theme, start with
  Windows (HKCU `Run` key), start minimised

### Tray menu

- **Open settings…** (left-click does the same)
- **Pause / Resume** — stops or restarts the worker without quitting (the
  display will keep showing the last frame until the firmware times it out)
- **Quit**

### Picking the right CPU/GPU sensor

Open settings, choose `Temperature` in the sensor-type drop-down, then the
sensor drop-down lists every temperature sensor HWiNFO is reporting (e.g.
`CPU (Tctl/Tdie)`, `GPU Temperature`, `GPU Hot Spot`, `CPU CCD1 (Tdie)`, ...).
Pick one and the regex below is filled in automatically. To match several
sensors and average them, edit the regex by hand, e.g. `^Core \d+`.

### Elevation

HWiNFO64 is often run as administrator (required for some sensors). Shared
memory created by an elevated process is not visible to an unelevated reader.
If the status bar shows `HWiNFO: not connected`, run AntecCaseDisplay as
administrator too — or start HWiNFO64 unelevated.

## Display protocol notes

The display accepts 12-byte frames over the HID interrupt OUT endpoint:

```
[0]  0x55
[1]  0xAA
[2]  0x01
[3]  0x01
[4]  0x06
[5]  CPU tens digit
[6]  CPU ones digit
[7]  CPU tenths digit  (forced to 0 when "Round to whole degrees" is on)
[8]  GPU tens digit
[9]  GPU ones digit
[10] GPU tenths digit
[11] checksum = (sum of bytes [0..10]) & 0xFF
```

When a value is unavailable the three digit bytes are `0xEE 0xEE 0xEE`, which
the display renders as dashes. On Windows the HID stack prepends a report ID
byte, so we actually write 13 bytes (`0x00` + the frame above).

## Credits

Protocol details were worked out by the Linux community — in particular
[nishtahir/antec-flux-pro-display](https://github.com/nishtahir/antec-flux-pro-display),
[AKoskovich/antec_flux_pro_display_service](https://github.com/AKoskovich/antec_flux_pro_display_service),
and [Reikooters/antec-flux-pro-display](https://github.com/Reikooters/antec-flux-pro-display).
The HWiNFO shared memory format follows the reverse engineering notes at
<https://gist.github.com/namazso/0c37be5a53863954c8c8279f66cfb1cc>.

This is an unofficial project and is not affiliated with Antec or HWiNFO.
