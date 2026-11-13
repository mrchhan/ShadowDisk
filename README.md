<p align="center"><img src="docs/icon-256.png" width="96" alt="ShadowDisk icon"></p>

# ShadowDisk

**A free, open-source disk and partition inspector for Windows, built with data safety first.**

ShadowDisk shows what is really on your drives: physical disks, partitions, volumes, used data,
free space and unallocated space. You can view the layout as a bar, a pie/donut chart or a table.

> **Version 0.2 is read-only.** It never resizes, creates, deletes, formats or writes to any disk.
> Changes will be added later, one carefully tested operation at a time. See [SAFETY.md](SAFETY.md).

## Features (v0.2)

- Every physical disk with its real identity: model, size in exact bytes, GPT/MBR, connection type
  (NVMe, SATA, USB…) and sector sizes
- Partitions in disk order, including EFI, Microsoft Reserved and Recovery partitions, which are
  clearly marked as protected
- **Used data**, **free space inside each volume** and **unallocated disk space** shown separately
- Bar, pie/donut or table view (your choice is remembered). The table is always shown as a text
  alternative to the charts
- BitLocker status (needs "Restart as administrator"), dynamic disks, removable drives and
  uninitialized disks are detected and explained
- "Why can't I resize this?" explanations, so you can see which future actions Windows would
  allow and why others would be blocked
- **Plan a change (preview only):** shrink a volume, extend it, or create a new volume in
  unallocated space, and compare the current and proposed layouts. Size limits come from
  Windows where possible. Every check and risk is listed, and nothing is applied
- The **health status Windows reports** for each disk, clearly labelled as *not* a bad-sector scan
- **Demo mode** with simulated disks, to explore safely or to make screenshots
- Plain-text report export with masked serial numbers
- Timestamped activity log in `%LOCALAPPDATA%\ShadowDisk\logs`

## Install

1. Download `ShadowDisk-Setup-x.y.z.exe` from the [Releases](https://github.com/mrchhan/ShadowDisk/releases) page.
2. Run it. You can install for yourself only (no admin prompt) or for all users.
3. Start **ShadowDisk** from the Start menu. **ShadowDisk (demo disks)** opens with simulated disks.

No separate .NET install is needed.

> Windows SmartScreen may warn about an "unrecognized app" because the installer is not yet
> code-signed. Choose **More info → Run anyway** if you downloaded it from this repository.

**Supported:** Windows 10 (1809 or later) and Windows 11, 64-bit.
**Tested on:** *not yet tested on real hardware. Please report results in Issues.*

## Build from source

Requirements: [.NET 10 SDK](https://dotnet.microsoft.com/download), Visual Studio 2026 (optional;
open `ShadowDisk.slnx`), and [Inno Setup 6](https://jrsoftware.org/isinfo.php) for the installer.

```powershell
dotnet test  tests\ShadowDisk.Core.Tests        # unit tests (simulated disks only)
dotnet run   --project src\ShadowDisk.App        # run the app
dotnet run   --project src\ShadowDisk.App -- --demo
powershell -ExecutionPolicy Bypass -File build\build-installer.ps1 -Version 0.1.0
```

Every push to GitHub builds and tests the app and produces the installer automatically. Pushing a
tag such as `v0.1.0` publishes a GitHub Release with the installer attached.

## Project layout

| Folder | What it is |
|---|---|
| `src/ShadowDisk.Core` | Disk model, layout maths, safety rules, report, log. No Windows calls, fully unit-tested |
| `src/ShadowDisk.Windows` | Read-only access to the Windows Storage Management API (`MSFT_Disk`, `MSFT_Partition`, `MSFT_Volume`) |
| `src/ShadowDisk.App` | WPF user interface |
| `tests/ShadowDisk.Core.Tests` | Unit tests using simulated disks only |
| `installer/` | Inno Setup installer script |

More detail: [docs/ARCHITECTURE.md](docs/ARCHITECTURE.md) · Plans: [docs/ROADMAP.md](docs/ROADMAP.md)

## Licence

MIT. Free to use, share and modify. See [LICENSE](LICENSE).
ShadowDisk is provided "as is", without warranty. Keep backups of anything important.

<img src="docs/aishadow-logo.png" width="40" alt="AIShadow logo" align="middle"> Made by Hassan Malik · **AIShadow**
