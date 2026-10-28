# Third-party notices

PulseWatch uses these components. Their licenses apply to them; the packaged builds include their binaries unchanged.

| Component | Use | License |
|---|---|---|
| [LibreHardwareMonitorLib](https://github.com/LibreHardwareMonitor/LibreHardwareMonitor) | CPU, motherboard (SuperIO), memory and storage sensors, fan control | MPL-2.0 |
| [HidSharp](https://www.zer7.com/software/hidsharp) (via LibreHardwareMonitorLib) | USB HID access (Lian Li Galahad II) | Apache-2.0 |
| [NvAPIWrapper.Net](https://github.com/falahati/NvAPIWrapper) | NVIDIA driver settings, performance states | LGPL-3.0 |
| NVIDIA NVML / NvAPI | GPU telemetry and tuning (part of the NVIDIA driver, not redistributed) | NVIDIA |
| [PawnIO](https://pawnio.eu/) | Kernel driver LibreHardwareMonitor uses for motherboard sensors (installed separately, not redistributed) | see project |

The Lian Li Galahad II Trinity USB protocol was implemented with the help of the open-source
lian-li-linux community documentation; no code from it is included.
