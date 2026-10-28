# ⚡ PulseWatch 2.0 · one control center

One app instead of four: **GCC** (motherboard fan curves), **MSI Afterburner** (graphics card tuning),
**NVIDIA Control Panel** (driver settings) and **L-Connect 3** (Lian Li Galahad II pump, radiator fans and lighting) —
with temperatures, graphs and an hour of history. All values are real, read straight from the hardware.

## Install

Download from [Releases](https://github.com/AirZone1/PulseWatch/releases/latest):

| File | What it is |
|---|---|
| `PulseWatch-…-win-x64.msi` | Installer: Program Files + a Start-menu shortcut. Uninstall from Windows *Apps* as usual. |
| `PulseWatch-…-win-x64-portable.zip` | No install: unzip anywhere (a USB stick works too) and run `PulseWatch.exe`. Settings are kept in the `Data` folder next to it. |

Both include the .NET runtime — nothing else to install for the app itself.

**Before the first start**
- The **PawnIO** driver for motherboard sensors and motherboard fan control: `winget install namazso.PawnIO`
- A current NVIDIA driver (for NVIDIA cards).
- PulseWatch asks for administrator rights — fan, GPU and USB control need them.
- The files are not code-signed yet, so Windows SmartScreen may warn: *More info → Run anyway*.

**Supported systems:** Windows 10/11 x64. Linux and macOS are not supported yet.

## Using it

- **System and settings → "Start with Windows"** creates a scheduled task that starts PulseWatch at sign-in, minimized to the tray and without a UAC prompt.
- Closing the window keeps PulseWatch running in the tray (otherwise the fan curves stop). Quit from the tray menu or the System page.
- **Language:** System and settings → עברית / English (the whole UI, including right-to-left layout for Hebrew).

## Pages

| Page | Replaces | What it does |
|---|---|---|
| **Overview** | — | CPU/GPU temperatures and load, pump and radiator, every fan, graphs from one minute to one hour |
| **Cooling and fans** | GCC Smart Fan / L-Connect | Per channel: auto / fixed speed / curve. Drag-to-edit curves, several source sensors (the hottest wins), hysteresis and ramp up/down, "identify fan", rename and hide |
| **Graphics card · OC** | MSI Afterburner | Power limit, temperature limit, core/memory offsets in Afterburner units, **a 127-point voltage/frequency (V/F) curve editor** with two views (voltage per clock / Afterburner), undervolt in mV over the whole curve or a marked range (drag the line by hand), a voltage ceiling kept flat even as the curve shifts with temperature, 5 OC profiles, re-apply at start, live monitoring and "what limits the card now" |
| **NVIDIA settings** | NVIDIA Control Panel | Global 3D settings (power management, Low Latency, Max FPS, V-Sync, texture filtering…), digital vibrance per display, one-click presets |
| **Lighting · Lian Li** | L-Connect 3 | Effects for the pump head and fans, colour by temperature, motherboard ARGB sync |
| **Sensors and graphs** | HWMonitor | Every sensor with min/max, search and filters, renaming, ★ to graph, CSV export |
| **Automation · OSD** | RivaTuner / Afterburner profiles | Cooling and OC profile per game (restored when the game exits), Windows notifications for high temperatures, hotkeys (Ctrl+Alt+1–4 profiles, Ctrl+Alt+F1–F5 OC profiles, Ctrl+Alt+O OSD), an always-on-top OSD (rows or one line, 3 sizes, any screen and corner, pick the values) with FPS measured through ETW like PresentMon (DirectX 9–12, foreground game or per screen; not over exclusive fullscreen) |
| **System and settings** | — | Specs, drive health, vendor-app management, start with Windows, thermal safety net, power plan, HDD and NVMe read rates |

**More hardware** (through LibreHardwareMonitor 0.9.6, detected automatically — sensors, and fan/pump control where the device allows it):

| Kind | Supported |
|---|---|
| AIOs and fan controllers | NZXT Kraken (V2/V3) and Grid V3 · MSI MAG/MEG Core Liquid · Aquacomputer Octo, Quadro, D5 Next, Aquastream, Farbwerk, High Flow Next, MPS · Arctic · AeroCool P7-H1 · Razer PWM hub · T-Balancer · Heatmaster · Lian Li Galahad II Trinity (own driver) |
| Power supplies | Corsair HXi / RMi / AXi (USB) · MSI — output power, temperature, fan |
| Graphics cards | NVIDIA (full tuning) · AMD Radeon and Intel Arc / integrated (sensors and fans) |
| Motherboards | the 332 models LibreHardwareMonitor knows (ASUS, MSI, ASRock, Gigabyte…) with their own header and sensor names, ASUS EC sensors (VRM, chipset, water); any other SuperIO board with generic names; Gigabyte X870I AORUS PRO ICE with a hand-made profile |

Planned (experimental, needs testers with the hardware): AMD Radeon and Intel Arc tuning, Corsair Commander Core/XT, Lian Li UNI HUB and Galahad II LCD.

**Unified profiles** (top bar and tray): Silent / Balanced / Performance / Max — each keeps its own setting for every fan channel.

**Graphs:** click a name under a graph to hide its line, double-click to isolate it; the vertical axis follows the visible lines.

## Performance

Measured on the development PC with the built-in profiler (a `PERF` line in the log every minute):

| State | CPU | Memory |
|---|---|---|
| In the tray | ~0.05% of the PC | ~45–60 MB |
| Window open | ~0.15–0.25% | ~180 MB |

How: the NVIDIA card is read directly (NVML + NvAPI, ~1.5 ms instead of 86 ms through LibreHardwareMonitor); sensors that drive fans are sampled every second, everything else less often (an old HDD's SMART read takes 270 ms, so it is read every few minutes and never wakes a sleeping disk); pages are released from memory while the window is hidden. Optional parts (FPS counter, fan-controller scan, AMD/Intel GPU support) only run when they are needed.

## Safety

- **Fan curves are the protection:** a fan on a curve follows its curve all the way to 100%.
- **Thermal safety net** for what does not react to heat, at the hardware limit (default: the CPU's and the card's own maximum temperature): CPU at its limit → radiator fans on a fixed speed go to 100%; graphics card at its limit → its fans on fixed/auto and the system fans that follow it go to 100%. The pump always stays where you set it. Back to normal 6 °C below the limit.
- **Missing sensor** → the channel that follows it goes to 100%.
- **On exit:** motherboard and GPU fans return to BIOS/driver control; the Galahad pump goes to 100% and its fans to the chosen value (default 60%), since the AIO has no automatic mode of its own.
- **Vendor apps:** while Afterburner / L-Connect / GCC run, PulseWatch switches to read-only for their hardware (two programs on one fan make it jump). "Close and take over" closes them; "Give back" starts them again.
- **NVIDIA backup:** before the first change the driver settings are backed up to `%AppData%\PulseWatch\nvidia-settings-backup.nvidia`.

## Code layout (`native/`)

```
Core/   SensorHub.cs       all hardware access on one thread: sampling, history (3600 samples), fan engine, lighting, safety net, V/F cap keeper
        FanChannels.cs     fan channels: SuperIO (motherboard), NVML (GPU), Galahad II (pump + radiator), other LHM controllers
        Nvml.cs            NVIDIA NVML: fans, power/temperature limits, clock offsets, throttle reasons
        NvApiPrivate.cs    NvAPI V/F curve and thermal sensors (hot spot, memory junction)
        NvidiaSettings.cs  NvAPI DRS (driver settings) + digital vibrance
        LianLiTrinity.cs   Galahad II Trinity USB HID protocol (0416:7373)
        BoardProfiles.cs   per-board header / sensor names and voltage dividers
        Automation.cs      temperature alerts, per-game profiles, hotkeys
        FpsMonitor.cs      FPS through Windows event tracing (DXGI / D3D9 present events)
        Config.cs          settings, curves and profiles → %AppData%\PulseWatch\config.json
        SystemTools.cs     conflicting apps, startup task, power plans
Controls/ TimeChart, FanCurveEditor, VfCurveEditor, RingGauge   (drawn directly, no chart libraries)
Views/  one page per tab, plus the OSD window
```

## Building from source

- .NET 8 SDK, then `update_native.bat` (builds into `bin\native_win64` and starts it), or
  `cd native && dotnet publish -c Release -o ..\bin\native_win64`.
- Release packages (MSI + portable ZIP) are built by GitHub Actions for every version tag (`v2.0.1`, …) — see
  `.github/workflows/build.yml` and `installer/`.

## Credits

LibreHardwareMonitor + PawnIO (sensors and SuperIO) · NVIDIA NVML / NvAPI via NvAPIWrapper · HidSharp ·
the Galahad II protocol as documented by the open-source lian-li-linux project. See `THIRD-PARTY-NOTICES.md`.
