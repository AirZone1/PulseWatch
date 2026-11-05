# Porting PulseWatch to Linux and macOS

**Status:** plan only, nothing here is implemented. Written 2026-11-03 against PulseWatch 2.0.2 (commit `5e8c351`). Line counts are from that version; the AMD notes also cover 2.0.3.

## Summary

- **Linux is worth doing.** Most of the app can reach parity. The real losses are the NVIDIA driver settings page and system-wide FPS.
- **macOS would be a much smaller product:** monitoring, plus maybe Mac fan control. No Mac has a motherboard fan header, an NVIDIA card or a socket for a Galahad II. Decide on it after Linux ships.
- **The hard part is not the hardware calls.** It is moving about 7,150 lines of WPF to Avalonia and splitting the one elevated process into a root daemon and a normal-user UI.
- **First milestone:** Linux monitoring plus Galahad II control, with no root and one udev rule (see [Phase 2](#phase-2--first-milestone-linux-monitoring--galahad-ii)).
- **Rough effort:** 15–23 person-weeks for full Linux, then 2–5 more for macOS. These are estimates from line counts, not measurements.

### Four findings that change the plan

1. **NvAPI does exist on Linux.** The driver ships `libnvidia-api.so`, and two Linux tools already edit the V/F curve through it: [LACT](https://github.com/ilya-zlobintsev/LACT) and [nvcurve](https://github.com/ekojsalim/nvcurve). nvcurve's [constants](https://github.com/ekojsalim/nvcurve/blob/main/nvcurve/nvapi/constants.py) use the same function IDs and structure sizes as our `NvApiPrivate.cs` (`0x21537AD4`, `0x507B4B59`, `0x23F1B133`, `0x0733E009`; sizes `0x1C28`, `0x2420`, `0x182C`; strides 28 and 36). So the V/F editor is *possible* on Linux. It stays undocumented and experimental, as it is on Windows.
2. **The development PC's own board is a problem case.** The [mainline `it87` documentation](https://docs.kernel.org/next/hwmon/it87.html) does not list the IT8696E. The [out-of-tree `it87` driver](https://github.com/frankcrawford/it87) does, but it is a DKMS module users must install themselves. Motherboard fans on the X870I may therefore not work out of the box.
3. **LibreHardwareMonitorLib cannot be used on Linux as packaged.** The 0.9.6 NuGet package only has runtime binaries for `win-x64`, `win-x86` and `win-arm64`, and its motherboard access depends on PawnIO. Linux needs its own sensor backend, which also means losing LHM's table of 332 boards.
4. **.NET 8 support ends on [10 November 2026](https://devblogs.microsoft.com/dotnet/dotnet-8-9-end-of-support/), a week after this was written.** The Windows app should move to .NET 10 (LTS, supported to November 2028) whatever happens with porting. `Avalonia.Wayland` 12.1.1 targets both `net8.0` and `net10.0`, so Avalonia does not block this.

### How far to trust this document

Each claim below falls into one of three groups:

- **Checked:** read in this repo's code or NuGet packages, or in the linked source.
- **Background:** general platform knowledge, not re-checked for this plan.
- **Test:** only real hardware can settle it. [Phase 0](#phase-0--prove-it-on-the-real-pc) covers the important ones.

Nothing here has been run on Linux or macOS. Several web sources are third-party READMEs, so treat undocumented details, especially for macOS fans, as leads.

## The code today

| Area | Lines | Portability |
|---|---:|---|
| `Core/` | 3,811 | Mixed, see below |
| `Controls/` (4 custom-drawn controls) | 1,224 | Mechanical port: same drawing model, different API |
| `Views/*.cs` | 3,225 | Logic carries over; WPF types must change |
| `Views/*.xaml`, `MainWindow.xaml`, `App.xaml`, `Themes/Dark.xaml` | 2,110 | Layout mostly ports; the theme is a rewrite |
| `MainWindow.xaml.cs`, `App.xaml.cs` | 595 | Rewrite: tray, single instance, window chrome |

`Core/` splits three ways:

| Group | Files | What has to happen |
|---|---|---|
| Portable now | `Sensors`, `L`, `BoardProfiles`, `SensorGlossary`, `LianLiTrinity`, `Log` | Nothing, or a minor `.exe` fallback fix in `AppPaths` |
| Needs a seam | `SensorHub` (68 lines touch LHM types), `FanChannels` (8), `Nvml`, `Config` (one registry read), `Automation` (uses `MainWindow`, `HwndSource`) | Hide the platform parts behind interfaces |
| Windows-only | `FpsMonitor` (ETW), `DiskPower`, `NvidiaSettings` (DRS), `SystemTools` (`sc`, `schtasks`, `powercfg`), the `Hotkeys` class | One implementation per platform, or drop the feature |

Three things already help:

- `SensorHub` owns all hardware on one thread, and the UI posts commands through `Run`.
- `FanChannel` is already an abstract class with one subclass per kind of hardware.
- Curves and the OSD use aliases such as `@cpu.temp`, so they do not depend on where a sensor comes from.

Other Windows-only pieces that are easy to miss:

- `System.Management` (WMI) in `SensorHub` and `SystemView`.
- WinForms `Screen` in `FpsMonitor`, `NvidiaSettings`, `OsdWindow`, `AutomationView` and `NvidiaView`.
- WinForms `NotifyIcon` for the tray.
- `EmptyWorkingSet` in `MainWindow`.
- The named `EventWaitHandle` in `App`, which .NET does not support on Unix (Background).

## Target architecture

```
                     unprivileged                     |            root (Linux)
 ┌───────────────────────────────────────────┐        |   ┌──────────────────────────────────┐
 │ PulseWatch.App (Avalonia)                 │  Unix  |   │ pulsewatchd (systemd service)    │
 │  pages, charts, tray, OSD, hotkeys,       │ socket |   │  SensorHub, fan engine, safety   │
 │  notifications, per-game rules            │◄──────►│   │  net, history, V/F cap keeper    │
 └───────────────────────────────────────────┘        |   │  hwmon · NVML · NvAPI · HID      │
                                                      |   └──────────────────────────────────┘
```

Proposed projects, in the new folder `src/`:

- **`PulseWatch.Core`:** plain `net10.0`, no UI. Config, curves, fan engine, sensor model, `LianLiTrinity`, board profiles, IPC contract.
- **`PulseWatch.Hardware.Windows`, `.Linux`, `.Mac`:** implementations of `ISensorSource`, `IGpuBackend` and `ISystemIntegration`, plus their `FanChannel` subclasses.
- **`PulseWatch.App`:** the Avalonia UI.
- **`pulsewatchd`:** a thin host around Core and the Linux backend.

`native/` keeps shipping unchanged until Avalonia has proven itself.

### Why a daemon on Linux

On Windows the whole app runs elevated. That does not carry over:

| Option | Verdict |
|---|---|
| Run the UI as root (`pkexec`) | **No.** A root GUI on a user's desktop is unsafe, and tray and config paths break |
| File capabilities on the binary | **No.** Getting past sysfs file permissions would take `CAP_DAC_OVERRIDE`, close to root for a whole GUI |
| udev rules that open up `pwm*` files | **Partial.** Possible for fans, but NVML writes still need root |
| Root daemon + user UI | **Yes.** LACT and lian-li-linux both use this design with a Unix socket |

Beyond permissions, fan curves keep running with no one logged in and after a UI crash, and the UI can exit fully, which helps the memory target.

**The socket is a privilege boundary.** A process that reaches it can change fan speeds and GPU limits as root, so:

- Restrict it to a `pulsewatch` group. LACT defaults to the `wheel`/`sudo` groups.
- Clamp every value in the daemon.
- Never accept a file path or a command to run.
- Never expose TCP.

### The largest refactor: UI and hub share memory

The UI reads `App.Config` in about 160 places, and the hub reads the same object live. It also uses `SensorInfo`, `FanChannel`, `Hub.Gpu` and `Hub.Aio` objects directly. A cheap path exists:

- **Config:** the UI keeps editing a local `AppConfig`. `App.SaveSoon()` is already the debounced point where edits are saved, and it can send the config to the daemon instead of writing a file. Check that every edit goes through it. The daemon then owns the hardware settings, under `/var/lib/pulsewatch`.
- **Live values:** the daemon sends a snapshot per tick only while a UI is connected. `UiActive` and `WantAllSensors` become subscription messages, so the tiered sampling and its CPU savings survive.
- **History:** sent once as a binary block on connect (3,600 floats per sensor).

Keep Phase 2 in-process but written against this contract, so Phase 3 changes the transport and not the pages.

## UI: WPF to Avalonia

Avalonia is close to WPF but not source-compatible. Expect to touch every file.

| WPF today | Avalonia | Effort |
|---|---|---|
| `OnRender(DrawingContext)` with `DrawEllipse`, `DrawRoundedRectangle`, `StreamGeometry` | `Render(DrawingContext)` with near-equivalents; `FormattedText` construction differs | Low, the logic is unchanged |
| `OnMouse*`, `CaptureMouse` | `OnPointer*`, `e.Pointer.Capture` | Low |
| `Style` + `ControlTemplate.Triggers` (43 styles, 22 triggers) | CSS-like selectors and pseudo-classes | **High: rewrite `Dark.xaml`** |
| `{l:T He=…, En=…}` markup extension | Custom markup extensions are supported | Low |
| `WindowChrome` | `ExtendClientAreaToDecorationsHint`; behaviour varies by desktop | Medium, test |
| `AllowsTransparency`, `Popup` | Different API | Low to medium |
| `FlowDirection` for Hebrew | Supported, but needs its own check | **Test early** |
| WinForms `NotifyIcon` + `ContextMenuStrip` | `TrayIcon` + `NativeMenu` | Medium, see below |
| WinForms `Screen`, `SaveFileDialog` | `Window.Screens`, `StorageProvider` | Low |
| `Segoe UI Variable` (8 references) | Bundle fonts, e.g. Noto Sans + Noto Sans Hebrew | Low, adds package size |

**Shared:** logic in the code-behind, chart math, curve hit-testing, all Hebrew and English strings, layout structure.

**Rewritten:** the theme, the window shell, the tray and the OSD.

**Port order by risk:** `RingGauge` and `TimeChart` first because they show rendering cost, then `FanCurveEditor`. `VfCurveEditor` (607 lines) and `GpuView` (780 lines) come last.

Platform limits to design around:

- **Wayland:** Avalonia's [native backend arrived in 12.1 and is still experimental](https://avaloniaui.net/blog/release-12-1). It is opt-in through `UseWayland()`, and `UsePlatformDetect()` does not pick it. Start on X11/XWayland. The [package is MIT-licensed](https://www.nuget.org/packages/avalonia.wayland/).
- **Tray:** [Avalonia's tray needs StatusNotifierItem/AppIndicator](https://docs.avaloniaui.net/controls/navigation/trayicon), and GNOME may need an extension. This matters for safety: until the daemon exists, closing the window on a desktop with no tray must not silently leave fan curves running with no visible way to stop them. Detect the tray, and minimize instead.
- **Hebrew tray menu:** the current menu sets right-to-left explicitly. `NativeMenu` may not offer that (Test).

**Memory is an open risk.** Windows today measures about 45–60 MB in the tray and about 180 MB with the window open. Avalonia with Skia has not been measured here, and `EmptyWorkingSet` has no Linux equivalent. `Process.WorkingSet64` should work on Linux, so the existing `PERF` log line can give comparable numbers. Treat this as a gate:

1. Measure at the end of Phase 2, before porting the remaining pages.
2. If tray mode is too heavy, use the daemon split: let the UI exit and leave only a small tray process.
3. Consider NativeAOT for the daemon. It needs System.Text.Json source generation, and HidSharp's compatibility is unknown (Test).

## Linux backends

| Feature | Windows today | Linux replacement | Privilege |
|---|---|---|---|
| CPU temperature | LHM | hwmon `k10temp` / `coretemp` | None |
| CPU load, clock, RAM | LHM | `/proc/stat`, `cpufreq`, `/proc/meminfo` | None |
| CPU power | LHM | `powercap` `energy_uj` ([restricted since CVE-2020-8694](https://access.redhat.com/security/cve/CVE-2020-8694)) | Root (daemon) |
| Motherboard temperatures, tachs | LHM + PawnIO | hwmon, driver depends on the board | None to read |
| Motherboard fan control | LHM + PawnIO | hwmon `pwmN`, `pwmN_enable` | Root |
| NVIDIA telemetry | NVML | NVML, same calls | None |
| NVIDIA power, fans, offsets | NVML + NvAPI | NVML | Root |
| NVIDIA V/F, hot spot, VRAM | Private NvAPI | Private NvAPI via `libnvidia-api.so` | Root (per nvcurve) |
| AMD GPU | LHM, plus experimental tuning through ADL (`AmdTuning.cs`, 2.0.3) | amdgpu hwmon + sysfs | Root to write |
| NVMe temperature | LHM | hwmon `nvme` | None |
| SATA temperature | LHM SMART | hwmon `drivetemp`, or `smartctl` | None / root |
| Galahad II | HidSharp | HidSharp over hidraw | udev rule |
| Network | `NetworkInterface` | Same .NET API (Test) | None |

### Sensors and motherboard fans

Write a generic `HwmonSource` and `HwmonChannel : FanChannel`. Details that matter:

- **Stable IDs:** `hwmonN` numbers can change between boots. Build IDs from the driver name plus the device path.
- **Hand control back correctly:** save each `pwmN_enable` before the first write and restore that exact value. Automatic-mode values differ by driver. Manual mode is `1`.
- **Sleep and crashes:** expect PWM state to need re-applying after resume, using logind's `PrepareForSleep` signal (Test). Add `ExecStopPost` to the unit so firmware control returns even after a crash, with `Restart=on-failure`.
- **Alias rules:** `ResolveAliases` matches names like `Tctl` and `CCD`. `k10temp` should label `Tctl` and `Tccd1`, so the CCD rule needs extending (Test). The hwmon `nvme` label `Composite` should match the current rule (Test).
- **Board names:** LHM's per-board header names are lost, so most boards show generic names. `BoardProfiles` still works if `pwmN` lines up with today's control index (Test). Its voltage scaling needs rechecking against raw `it87` values.
- **No driver, no feature:** add a page explaining why a fan is not controllable and what to install. [CoolerControl has a similar diagnostic](https://docs.coolercontrol.org/hardware-support.html) and notes that many boards need `it87` or `nct6687`.

**The X870I / IT8696E.** Using the out-of-tree driver brings real caveats:

- Its README warns that `ignore_resource_conflict` can cause races with ACPI and, in the worst case, unexpected reboots. The kernel documentation calls it "inherently risky".
- It also reports that some Gigabyte boards have a separate fan chip where "you can measure the speed, you cannot change it". All six PWM outputs work on Windows on this board, which is encouraging but proves nothing about the Linux driver.
- Secure Boot generally requires signing DKMS modules (Background).

PulseWatch must not bundle or auto-install a kernel module.

**Other hardware LHM provides:** kernel drivers exist for several devices, such as `corsair-psu`, `nzxt-kraken3` and `aquacomputer_d5next` (Background). They appear in hwmon, so the generic backend picks them up. Coverage will differ from Windows, so the README's hardware table needs a separate Linux list.

### NVIDIA

| Function | Plan | Notes |
|---|---|---|
| P/Invoke loading | `NativeLibrary.SetDllImportResolver`: `nvml.dll` → `libnvidia-ml.so.1`, `nvapi64.dll` → `libnvidia-api.so.1` | The declarations stay as they are |
| Telemetry | Unchanged `NvmlApi` calls | |
| Fans, power limit | Unchanged, as root | |
| Temperature target | Unchanged, as root | Acoustic threshold on Linux: Test |
| Clock offsets | Use the NVML fallback already in `SetOffsets` | `NvAPIWrapper` is Windows-only. Recheck the "NVML memory = 2× Afterburner" factor on Linux |
| Voltage, fan RPM | Raw NvAPI calls in place of `NvAPIWrapper` | nvcurve lists `0x465F9BCF` for voltage |
| GPU handle | Raw `Initialize` and `EnumPhysicalGPUs` | IDs are in nvcurve's constants |
| Hot spot, VRAM | Existing `0x65FE3AAD` call | The ID appears in LACT's source (code search only, not read). If it fails, the values stay `NaN` and the UI hides them |
| V/F curve | Reuse `NvApiPrivate` and `KeepVoltageCap` | Experimental, see below |
| Undervolt without V/F | Locked clocks plus a positive offset | [The usual Linux method](https://code.mendhak.com/linux-undervolt-gpu/); a fallback if the private API breaks |
| DRS driver settings | **Hide the page** | No equivalent API known. Options are per-app `__GL_*` variables and `nvidia-application-profiles-rc` (Background) |
| Digital vibrance | `nvidia-settings`, X11 only (Background) | Not on Wayland |

Rules for the V/F curve on Linux:

- Ship it behind an "experimental" switch, labelled honestly. Both LACT and nvcurve warn about this API, and nvcurve notes it could change or disappear in any driver release.
- Read back after every write, and keep the current checks that disable the editor when a call is unavailable.
- Test on more than the RTX 3080. nvcurve's author tested on an RTX 5090, and it notes older GPUs (Pascal and earlier) may need a different write mask.

Two more points:

- **Tools overwrite each other:** LACT, nvcurve and CoolerControl all write to the same GPU. nvcurve's README warns about this directly. Add them to `Conflicts` so PulseWatch goes read-only, as it does for Afterburner.
- **Settings don't persist:** they are not stored on the GPU, so the daemon re-applies them at boot, using the existing `ApplyOnStartup` logic.

### AMD and Intel GPUs

The [amdgpu documentation](https://docs.kernel.org/gpu/amdgpu/thermal.html) lists what is needed:

- **Reads:** `temp*_input`, `fan1_input`, `power1_average`/`power1_input`, `in0_input`, `freq1_input`, `freq2_input`, `gpu_busy_percent`, `mem_busy_percent`.
- **Writes:** `pwm1` with `pwm1_enable`, `power1_cap`, `fan_curve`, `pp_od_clk_voltage`.

Windows PulseWatch gained experimental AMD tuning in 2.0.3 through ADL Overdrive 8 (`atiadlxx.dll`). That library is Windows-only, so `AmdTuning.cs` does not port, though `AmdGpuView` may be reusable. Overdrive usually requires a boot parameter (`amdgpu.ppfeaturemask`, Background; LACT publishes a guide). Newer cards may need `fan_curve` instead of `pwm1` (Test).

Intel: sensors only, whatever the `i915`/`xe` hwmon exposes.

**There is no AMD or Intel card on the development PC.** This work needs testers, as the README already says for Windows. LACT is MIT-licensed and a good reference.

### Storage

- **NVMe:** free through hwmon.
- **SATA:** needs the `drivetemp` module. `DiskPower` ("never wake a sleeping HDD") has no direct equivalent, and the kernel notes that `drivetemp` reads can reset the spin-down timer on some drives (Background). So the "keep asleep" option should be hidden on Linux unless a safe method is proven, not left as a promise that may not hold.
- **Drive health page:** `smartctl --json` in the daemon.

### Galahad II Trinity

`LianLiTrinity.cs` should port with no changes. It only depends on HidSharp, and the HidSharp 2.6.4 binary contains `LinuxHidManager` and `MacHidManager`. HidSharp comes in through LHM today, so it must become a direct package reference. [lian-li-linux](https://github.com/sgtaziz/lian-li-linux) lists the Galahad II Trinity as tested on Linux, which confirms the protocol works there.

Rule file, to give the logged-in user access without root:

```
# /usr/lib/udev/rules.d/70-pulsewatch.rules
SUBSYSTEM=="hidraw", ATTRS{idVendor}=="0416", ATTRS{idProduct}=="7371|7373", TAG+="uaccess"
```

The number must stay below `73` so `uaccess` is processed in time (Background).

To test:

- The `GetMaxOutputReportLength() == 64` filter.
- That report bytes arrive at the same offsets on hidraw (`buf[1] == cmd`, RPM at bytes 6–9).
- Add the lian-li-linux daemon to `Conflicts`. Two programs on one HID device garble each other's replies, as `SensorHub.Skip` already notes.

**Safety:** the AIO has no automatic mode. `PosixSignalRegistration` must run the same exit path (pump 100%, fans at the chosen value) on `SIGTERM`, since WPF's `OnExit` no longer exists. After a `SIGKILL` or crash, or before login in Phase 2, the pump simply stays at its last value. The daemon removes the login gap but not the crash case, so document this for users.

### FPS (`FpsMonitor.cs`)

Linux has no ETW equivalent, meaning no system-wide list of frames per process without touching the game.

| Option | How | Verdict |
|---|---|---|
| Hand off to [MangoHud](https://github.com/flightlessmango/MangoHud) | The user enables its overlay; PulseWatch offers a config and instructions | **Recommended first.** Little code, covers Vulkan and OpenGL, so also DXVK games, which ETW misses on Windows |
| Read MangoHud's log | Enable logging to a folder, then read the file | Possible second step for showing FPS in our OSD. Latency and cost unmeasured |
| Our own Vulkan layer | Small native library counting `vkQueuePresentKHR` | Real work: native code, per-architecture builds, Steam runtime packaging. No OpenGL. Only if there is demand |
| eBPF or DRM tracepoints | Count flips or presents from the daemon | Research only. Apps usually bypass the loader's exports and proprietary NVIDIA support is uncertain (Background) |

MangoHud with gamescope needs `gamescope --mangoapp`, and some OpenGL titles need extra settings, per its README.

### OSD

| Session | Behaviour |
|---|---|
| X11 | Works: always-on-top hint, plus click-through via the XFixes input shape (native calls) |
| XWayland | Probably works on the desktop. Over fullscreen games is uncertain (Test) |
| Native Wayland | [No general always-on-top protocol](https://gitlab.freedesktop.org/wayland/wayland/-/issues/305). `wlr-layer-shell` works on KDE and wlroots but [not on GNOME](https://github.com/bobbycomet/Conky-Studio/wiki/Compatibility/55f059e73e511ab77434e763d3fd6e642e31c298). Avalonia exposes neither |

Decision: keep the OSD as a best-effort desktop panel on X11/XWayland, and point users to MangoHud for in-game display. Do not promise parity.

### Hotkeys, autostart and the rest

| Feature | Linux plan |
|---|---|
| Hotkeys, X11 | `XGrabKey` |
| Hotkeys, Wayland | [GlobalShortcuts portal](https://pypi.org/project/global-shortcut-portal/0.2.0/) (KDE Plasma 6+, GNOME 48+, Hyprland, per secondhand sources). The desktop owns the binding, so Ctrl+Alt+1 is only a suggestion |
| Hotkeys, rejected | Reading `/dev/input` directly. It is effectively a keylogger and has no place in a tool like this |
| Autostart | `systemctl enable pulsewatchd` for the daemon; `~/.config/autostart/pulsewatch.desktop` with `--tray` for the UI |
| Single instance, show window | Unix socket in `$XDG_RUNTIME_DIR` replaces the mutex and named event |
| Notifications | `org.freedesktop.Notifications` over D-Bus |
| Per-game rules | Keep in the UI. `Strip()` removes `.exe`; Proton games need matching against `/proc/PID/cmdline` (Test) |
| Vendor apps | New list: LACT, CoolerControl, `fancontrol`, lian-li-linux, nvcurve. Stop only through the daemon, with `systemctl` |
| Power plan | `power-profiles-daemon` or `tuned` if present, otherwise hide |
| Open folder, file | `xdg-open` |
| System specs | `/proc`, `/sys/class/dmi/id`, `lspci` replace WMI |
| Config paths | `SpecialFolder.ApplicationData` maps to `~/.config`. Guard the registry call with `OperatingSystem.IsWindows()`. Hardware IDs differ, so configs do not move between systems |

## macOS

No fan headers, no NVML, no Lian Li use case. What remains:

| Feature | Plan | Risk |
|---|---|---|
| Temperatures, fan RPM | AppleSMC via IOKit | Undocumented keys that vary by chip (Background) |
| Apple GPU and CPU power | `IOReport` (private), or `powermetrics` as root | Private API (Background) |
| CPU, RAM, network | `sysctl`, `host_statistics`, .NET APIs | Low |
| **Fan control** | Not in the first release | See below |
| NVIDIA, DRS, V/F, Galahad II, motherboard fans | **Removed** | Pages hidden |
| FPS | **Removed.** No system-wide API; Metal HUD is per-app (Background) | |
| OSD | Plain floating window | Native calls needed over fullscreen spaces |
| Hotkeys | Carbon `RegisterEventHotKey`, needs no accessibility permission (Background) | Low |
| Autostart | LaunchAgent | Low |
| Notifications | Native API, needs a signed bundle | Low |

**Fan control is possible, not impossible.** Existing apps such as Macs Fan Control write through a privileged helper. The community project [macos-smc-fan](https://github.com/agoodkind/macos-smc-fan) documents an undocumented `Ftst` unlock on Apple Silicon, with a helper daemon installed through `SMAppService`. Other unofficial notes say sleep clears the unlock and that M5 differs; these could not be confirmed at a primary source. All of this is unofficial, and a macOS update can break it. A helper also needs signing, so a paid developer account (Background). Macs without fans have nothing to control, and firmware already manages thermals (Background).

**Be honest about value.** Stats, iStat Menus and Macs Fan Control already cover this ground (Background). PulseWatch's point is replacing four vendor tools on a gaming PC, and that does not apply on a Mac. Port it only if the Avalonia UI makes it cheap and someone actually wants it.

Distribution: direct download only, because private APIs rule out the App Store. Without a Developer ID ($99/year) and notarization, users must approve the app in System Settings (Background).

## Feature matrix

| Feature | Windows | Linux | macOS |
|---|---|---|---|
| CPU, RAM, network monitoring | Full | Full (CPU power via daemon) | Partial |
| Motherboard sensors | 332 named boards | Generic names; driver-dependent | — |
| Motherboard fan control | Yes | Yes, where the driver allows; daemon | — |
| Mac fans | — | — | Read; write maybe later |
| Other AIOs, controllers, PSUs | Via LHM | Via kernel drivers; different list | — |
| Galahad II + lighting | Yes | Yes, no root | — |
| NVIDIA telemetry | Yes | Yes | — |
| NVIDIA power, fans, offsets | Yes | Yes, daemon | — |
| NVIDIA V/F curve, hot spot, VRAM | Yes (private API) | Experimental | — |
| NVIDIA driver settings | Yes | **No** | — |
| Digital vibrance | Yes | X11 only | — |
| AMD GPU | Sensors, fans; experimental tuning | Sensors, fans; tuning possible | — |
| NVMe temperature | Yes | Yes | Limited |
| SATA temperature, keep HDD asleep | Yes | Temperature with module; keep-asleep **no** | — |
| Per-game profiles | Yes | Yes (Proton needs work) | Low value |
| Temperature alerts | Yes | Yes | Yes |
| Global hotkeys | Yes | X11; Wayland via portal | Yes |
| OSD | Yes | Best effort; MangoHud in games | Basic |
| FPS | DirectX via ETW | Via MangoHud only | **No** |
| Vendor-app takeover | Yes | Yes, different list | Detect only |
| Thermal safety net | Yes | Yes, in the daemon | Firmware's job |

## Packaging and CI

| Format | Use | Notes |
|---|---|---|
| `.deb`, `.rpm` | **Primary for Linux** | The only formats here that install the udev rule and systemd unit cleanly. Build both from one [nFPM](https://nfpm.goreleaser.com/) config |
| AppImage | Trying it out, Phase 2 | Cannot install rules or services itself, so it needs a documented step |
| `.tar.gz` | Same role as the portable ZIP | The `portable` marker file works as is |
| AUR | Later, community | Small effort |
| Flatpak | **Not planned for control** | The sandbox is a poor fit for a tool that writes to sysfs and needs a host daemon and udev rules. A UI-only client is possible much later |
| `.dmg` | macOS | Built on a macOS runner with `hdiutil`; signing and notarization need secrets |

Immutable distros (Bazzite and similar) need a separate answer. lian-li-linux recommends Distrobox there, which complicates USB access.

Changes to `.github/workflows/build.yml`:

- Add `linux` and `macos` jobs.
- **Move publishing into its own job.** Today's Release step does `gh release view || gh release create`. Copied into three parallel jobs, that would race.
- Add hardware-free tests. There are none today. Point the hwmon reader at a fake `/sys` tree in a fixture folder.

```yaml
  linux:
    runs-on: ubuntu-latest
    steps:
      - uses: actions/checkout@v4
      - uses: actions/setup-dotnet@v4
        with: { dotnet-version: '10.0.x' }
      - run: dotnet test tests/
      - run: dotnet publish src/PulseWatch.App -c Release -r linux-x64 --self-contained -o publish/app
      - run: dotnet publish src/pulsewatchd -c Release -r linux-x64 -o publish/daemon
      - run: nfpm package -p deb -t dist/ && nfpm package -p rpm -t dist/
      - run: packaging/linux/build-appimage.sh publish/app dist/
      - uses: actions/upload-artifact@v4
        with: { name: linux, path: dist/* }

  release:
    if: github.ref_type == 'tag'
    needs: [windows, linux]   # add macos when it exists
    runs-on: ubuntu-latest
    steps:
      - uses: actions/download-artifact@v4
        with: { path: dist }
      # gh release create (if missing), then gh release upload --clobber
```

The `windows` job must then stop publishing, and its publish path changes once the project moves. `PulseWatch.csproj` hardcodes `net8.0-windows` and `win-x64`, so Linux and macOS need the new projects and cannot reuse it.

**Licensing:** the repo has no `LICENSE` file. Distro packaging and outside contributors both need one, so choose before Phase 2. NvAPIWrapper (LGPL-3.0, Windows build only), LHM (MPL-2.0), HidSharp (Apache-2.0), Avalonia, LACT, nvcurve and lian-li-linux (all MIT) do not appear to block any choice, but check before copying code rather than protocol facts.

## Phases

One developer who knows this code; ±50%. AI assistance may shorten the mechanical parts, but hardware testing does not compress much.

### Phase 0 — prove it on the real PC

**2–3 days, no product code.** Install a Linux distro on a spare drive of the development PC. A live USB may lack the proprietary NVIDIA driver.

```bash
sensors
```

```bash
grep . /sys/class/hwmon/hwmon*/name
```

```bash
sudo modprobe it87; sudo dmesg | tail -n 20
```

```bash
ldconfig -p | grep -E 'nvidia-(api|ml)'
```

```bash
nvidia-smi -q -d TEMPERATURE,POWER,CLOCK
```

```bash
lsusb -d 0416:
```

Answer these:

1. Does mainline `it87` bind to the IT8696E? If not, does the out-of-tree driver read and control all six outputs? Test PWM by hand on a case fan, not on the header that reads the pump (Fan #1 on this board), and restore `pwmN_enable` afterwards.
2. Do `k10temp`, `nvme` and `drivetemp` give what the aliases need?
3. Does a 30-line console program using `LianLiTrinity.TryOpen()` and `Poll()` get real RPM on hidraw?
4. Does nvcurve or LACT read the same 127-point curve as Windows? Is the thermal call usable, and does it need root?
5. Does a "hello" Avalonia window with Hebrew right-to-left text and a tray icon behave on the target desktops?

Outcome: confirm or revise this document, and fix the supported-distro list.

### Phase 1 — extract Core

**1.5–2.5 weeks.** Windows only, no behaviour change.

- Move to .NET 10.
- Create `PulseWatch.Core` and `PulseWatch.Hardware.Windows`, with the interfaces from [Target architecture](#target-architecture).
- Remove WPF and WinForms from Core.
- Add tests for curve math, hysteresis and config migration.

**Done when:** the Windows app passes a manual regression on the development PC, with fans, curve, OSD and FPS all working, and the `PERF` numbers are unchanged.

### Phase 2 — first milestone: Linux monitoring + Galahad II

**5–7 weeks.** Runs as a normal user, with one udev rule and no daemon.

| Work | Estimate |
|---|---|
| Avalonia shell, theme, fonts, RTL, tray | 2–2.5 weeks |
| `TimeChart`, `RingGauge`, `FanCurveEditor` | 1 week |
| Overview, Sensors, Cooling, Lighting, reduced System page | 1–2 weeks |
| hwmon, `/proc`, NVML read-only backends | 0.5–1 week |
| `.deb`, `.rpm`, AppImage, CI | 0.5 week |

**In:**

- All sensors, graphs and CSV export.
- Full curve control for the Galahad II pump and radiator fans, driven by CPU or GPU temperature.
- Lighting.
- Hebrew and English.

**Out, shown as read-only or absent:** motherboard and GPU fan control, GPU tuning, OSD, hotkeys, per-game rules.

Why start here:

- **Low risk:** no root and no privileged code, and the only writes go to a device whose protocol already works.
- **Tests the shared work:** it forces Core extraction and the Avalonia shell, which every later phase depends on.
- **Actually usable** on the development PC from day one.
- **Answers the memory question** before most of the UI is ported.

To be fair, lian-li-linux already controls this AIO and supports more Lian Li devices. PulseWatch's advantage is one app for everything, which this milestone only starts to show.

**Done when:**

- A fresh install on the chosen distros works in Hebrew and English.
- The Galahad II follows a curve for 24 hours.
- `SIGTERM` applies the exit values.
- `PERF` numbers are recorded and compared with Windows.
- There is an explicit decision on continuing.

### Phase 3 — daemon and fan control

**3–5 weeks.**

- `pulsewatchd`, the socket protocol, permissions and the systemd unit.
- Hub moved out of the UI.
- `HwmonChannel` writes, restore-on-exit, resume handling.
- NVML writes for fans, power and temperature.
- Thermal safety net in the daemon.
- Driver diagnostics page.

**Done when:** fans survive a UI crash, return to firmware control on service stop, and come back correctly after sleep and reboot. Also test by killing the daemon and pulling a sensor: the "missing sensor → 100%" rule must still hold.

### Phase 4 — GPU pages and AMD

**3–5 weeks.**

- `GpuView` and offsets.
- `VfCurveEditor` behind the experimental switch.
- Hot spot, VRAM, voltage and RPM through raw NvAPI.
- `Conflicts` for Linux tools.
- AMD reads and fans, then AMD tuning if testers appear.

**Done when:** an undervolt survives a reboot and temperature shifts on the RTX 3080, and a driver with changed APIs disables the editor without crashing.

### Phase 5 — desktop integration

**2–3 weeks.** Per-game rules including Proton, alerts, hotkeys on X11 and the portal, best-effort OSD, MangoHud help, Automation page.

**Done when:** each behaves as documented on the supported desktops, and every limit is stated in the UI.

### Phase 6 — one UI or two

**2–4 weeks if merging.** Two UIs means every page is written twice. After Linux is stable, run the Avalonia UI on Windows and compare. If memory and feel are close, retire WPF. If not, keep both and limit new pages accordingly. Decide from measurements.

### Phase 7 — macOS, optional

**2–3 weeks read-only, 1–2 more for fan control,** plus the annual developer fee. Needs a Mac for testing. Start only after Phase 5, with real demand.

## Main risks

| Risk | Consequence | Response |
|---|---|---|
| `it87` cannot control this board safely | The headline feature fails on the developer's own PC | Phase 0 finds out first; the milestone does not depend on it |
| Avalonia is much heavier than WPF | Breaks the light-footprint requirement | Measured gate in Phase 2 |
| A driver update breaks private NvAPI | V/F editor stops working | Experimental switch, read-back, locked-clock fallback |
| Too many desktop combinations | Endless OSD, tray and hotkey bugs | Support a short list; state limits |
| Single-developer hardware | AMD and other paths ship untested | Label as experimental; recruit testers |
| Root daemon has a bug | A security problem, not just a crash | Narrow protocol, value clamping, group-only socket, review before release |
| Two codebases during the port | Windows features drift from Linux | Keep shared logic in Core; finish Phase 6 |

## Decisions needed from the owner

1. **Supported Linux targets.** Suggested: current Ubuntu LTS, Fedora and Arch; KDE and GNOME; x86-64 only.
2. **Licence** for the repo, before Phase 2.
3. **Move to .NET 10 now**, separately from porting? Recommended, given the end-of-support date.
4. **macOS:** commit, or wait for demand? Recommended: wait.
5. **V/F editor on Linux:** ship as experimental, or leave out and offer only locked clocks plus offset? Recommended: experimental.
6. **Dedicated Linux install** on the development PC for Phase 0 and all later testing.

## Sources

Looked up on 2026-11-03.

**Kernel and drivers**
- [Kernel `it87` documentation](https://docs.kernel.org/next/hwmon/it87.html)
- [Out-of-tree `it87` driver](https://github.com/frankcrawford/it87)
- [amdgpu thermal documentation](https://docs.kernel.org/gpu/amdgpu/thermal.html)
- [Red Hat on CVE-2020-8694](https://access.redhat.com/security/cve/CVE-2020-8694)

**Comparable Linux tools**
- [LACT](https://github.com/ilya-zlobintsev/LACT)
- [nvcurve](https://github.com/ekojsalim/nvcurve)
- [lian-li-linux](https://github.com/sgtaziz/lian-li-linux)
- [CoolerControl hardware support](https://docs.coolercontrol.org/hardware-support.html)
- [Undervolting NVIDIA on Linux, mendhak](https://code.mendhak.com/linux-undervolt-gpu/)
- [MangoHud](https://github.com/flightlessmango/MangoHud)

**Avalonia and .NET**
- [Avalonia 12.1 release notes](https://avaloniaui.net/blog/release-12-1)
- [Avalonia.Wayland package](https://www.nuget.org/packages/avalonia.wayland/)
- [Avalonia TrayIcon](https://docs.avaloniaui.net/controls/navigation/trayicon)
- [.NET 8 and 9 end of support](https://devblogs.microsoft.com/dotnet/dotnet-8-9-end-of-support/)

**Wayland**
- [Always-on-top protocol request](https://gitlab.freedesktop.org/wayland/wayland/-/issues/305)
- [Conky Studio compatibility notes (GNOME and layer-shell)](https://github.com/bobbycomet/Conky-Studio/wiki/Compatibility/55f059e73e511ab77434e763d3fd6e642e31c298)
- [global-shortcut-portal (portal support list)](https://pypi.org/project/global-shortcut-portal/0.2.0/)

**macOS**
- [macos-smc-fan (Apple Silicon fan control, unofficial)](https://github.com/agoodkind/macos-smc-fan)
- [Macs Fan Control release notes](https://crystalidea.com/macs-fan-control/release-notes)
