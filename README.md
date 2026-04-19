# AntecCaseDisplay

A small Windows app that drives the CPU/GPU temperature LCD on the **Antec Flux
Pro** case, reading the temperatures from **HWiNFO64** via its shared memory
interface. It is a replacement for Antec's iUnity software.

- Runs as a plain console app (can be wrapped as a Scheduled Task / Windows
  service)
- Temperatures come from HWiNFO64's shared memory (`Global\HWiNFO_SENS_SM2`)
- The display is driven directly over USB HID (VID `0x2022`, PID `0x0522`)

## Requirements

- Windows 10/11 x64
- [.NET 8 SDK](https://dotnet.microsoft.com/download) (to build)
- [HWiNFO64](https://www.hwinfo.com/) running, with **Settings → Safety →
  Enable Shared Memory Support** ticked. (Shared memory is time-limited on the
  free edition; unlimited on HWiNFO64 Pro.)
- The Antec Flux Pro internal USB cable plugged into a motherboard USB 2.0
  header so Windows sees the display as a HID device

## Build

```powershell
dotnet build -c Release
```

The resulting binary is in `AntecCaseDisplay/bin/Release/net8.0-windows/`.

To produce a single self-contained exe:

```powershell
dotnet publish AntecCaseDisplay/AntecCaseDisplay.csproj -c Release -r win-x64 --self-contained true /p:PublishSingleFile=true
```

## Run

1. Start HWiNFO64 and keep it running in the background (system tray).
2. Launch `AntecCaseDisplay.exe`. On first run it writes a default
   `appsettings.json` next to the exe.

You should see output like:

```
AntecCaseDisplay — HWiNFO64 -> Antec Flux Pro LCD
Config: ...\appsettings.json
CPU sensor pattern: ^CPU \(Tctl/Tdie\)$|^CPU Package$|...
GPU sensor pattern: ^GPU Temperature$|^GPU$|...
Update interval:    1000 ms
Press Ctrl+C to exit.
```

### Picking the right sensors

HWiNFO exposes dozens of temperature sensors. The defaults cover common
AMD/Intel/NVIDIA setups, but you may need to tweak them. Set
`"listSensorsOnStart": true` in `appsettings.json` and restart; the program
will print every temperature sensor it sees, e.g.:

```
  42.1 °C  CPU (Tctl/Tdie)
  38.0 °C  CPU CCD1 (Tdie)
  55.0 °C  GPU Temperature
  63.0 °C  GPU Hot Spot
```

Then copy a name into `cpuSensorPattern` / `gpuSensorPattern`. The patterns
are **case-insensitive regular expressions**; anchor them with `^...$` if you
want an exact match, or use a substring like `"CPU Package"`.

### Elevation

HWiNFO64 is often run as administrator (required for some sensors). Shared
memory created by an elevated process is not visible to an unelevated reader.
If `AntecCaseDisplay` reports that shared memory is not available, run it as
administrator too (or start HWiNFO64 unelevated).

### Configuration reference

| Key                    | Default    | Meaning                                                                    |
| ---------------------- | ---------- | -------------------------------------------------------------------------- |
| `cpuSensorPattern`     | see source | Regex matched against HWiNFO's OriginalName / UserName                     |
| `gpuSensorPattern`     | see source | Same, for GPU                                                              |
| `updateIntervalMs`     | 1000       | How often to push a new frame to the display                               |
| `reconnectIntervalMs`  | 5000       | Retry delay after HWiNFO or the display disappears                         |
| `verbose`              | false      | Print each frame being sent                                                |
| `listSensorsOnStart`   | false      | Dump all temperature sensors on startup                                    |

## Running at startup

The simplest option is **Task Scheduler**:

1. Create a basic task. Trigger: _At log on of any user_.
2. Action: start `AntecCaseDisplay.exe`.
3. In the task properties, tick _Run with highest privileges_ if HWiNFO runs
   elevated.
4. Under _Settings_, untick _Stop the task if it runs longer than_.

## Protocol notes

The display accepts 12-byte frames over the HID interrupt OUT endpoint:

```
[0]  0x55
[1]  0xAA
[2]  0x01
[3]  0x01
[4]  0x06
[5]  CPU tens digit    (24.7 °C -> 2)
[6]  CPU ones digit    (24.7 °C -> 4)
[7]  CPU tenths digit  (24.7 °C -> 7)
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
