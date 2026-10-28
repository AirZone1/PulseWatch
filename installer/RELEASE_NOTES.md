## PulseWatch for Windows

**Downloads**
- `PulseWatch-…-win-x64.msi` — installer (Program Files, Start-menu shortcut).
- `PulseWatch-…-win-x64-portable.zip` — no install: unzip anywhere and run `PulseWatch.exe`. Settings stay in the `Data` folder next to it.

Both include the .NET runtime — nothing else to install for the app itself.

**Before the first start**
- Motherboard fans and sensors need the PawnIO driver: `winget install namazso.PawnIO`
- PulseWatch asks for administrator rights (fan, GPU and USB control need them).
- The files are not code-signed yet, so Windows SmartScreen may warn: *More info → Run anyway*.

Windows 10/11 x64 only. Linux and macOS are not supported yet.
