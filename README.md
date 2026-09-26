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
- Optional **dashboard window** for a second monitor: pick any HWiNFO
  readings and see them as live tiles with sparklines, colour thresholds and
  usage bars
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

- **Dashboard window** — see [Dashboard](#dashboard) below

### Tray menu

- **Open settings…** (left-click does the same)
- **Show / hide dashboard**
- **Pause / Resume** — stops or restarts the worker without quitting (the
  display will keep showing the last frame until the firmware times it out)
- **Quit**

### Dashboard

An optional window that shows whichever HWiNFO readings you choose — handy on
a second monitor to keep an eye on temperatures, clocks, power and
utilisation while gaming. Each tile shows the current value and unit, the
lowest/highest value HWiNFO has recorded, a sparkline of recent history, and
(optionally) a bar. Tiles turn amber or red when a reading reaches the
thresholds you set.

To set it up, open settings → **Dashboard window**:

1. Pick a device (e.g. `GPU [#0]: NVIDIA GeForce RTX 4080`) and a reading
   (e.g. `GPU Temperature`), then click **Add**. Repeat for each tile you want.
2. Edit a row in the table to change the tile's caption, decimal places,
   *Amber at* / *Red at* thresholds, or *Bar max* (the bar's full-scale
   value; blank hides the bar). Temperatures default to 80 / 90 with a
   0–100 bar; usage readings get a 0–100 bar.
3. Tick **Show dashboard**.

Changes in this section show on the dashboard straight away as a preview,
like the theme picker. **Apply** or **Save & Close** keeps them; **Cancel**
(or closing the settings window) puts the dashboard back how it was.

In the dashboard window: drag anywhere to move it, double-click or press
**F11** to maximise it (fills the whole monitor when borderless), and
right-click for *Always on top*, *Borderless*, *Settings…* and *Close*. The
layout picks a column count that suits the window shape (or set a fixed
count in settings), and text scales with the tile size. Position, size and
maximised state are remembered, so it reopens on the same monitor. If it was
open when you quit, it reopens next launch.

It's designed to be light enough to leave running next to a game: it reuses
the app's single HWiNFO poll (no extra polling), updates at the configured
refresh interval, only touches text that actually changed, draws each
sparkline as one precomputed shape, and uses no transparency, blur, shadows
or animations. When minimised it only records history. For even less work,
raise the refresh interval.

Readings are matched by HWiNFO's original device and reading names, so
renaming a sensor inside HWiNFO doesn't break a tile.

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
