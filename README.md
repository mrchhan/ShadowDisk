<p align="center"><img src="docs/icon-256.png" width="96" alt="ShadowDisk icon"></p>

# ShadowDisk

**A free, open-source disk and partition manager for Windows, built with data safety first.**

ShadowDisk shows what is really on your drives and lets you change partitions the careful way:
every change is previewed first, confirmed by typing, checked afterwards, and can be practised on a
throwaway virtual disk before you touch a real one.

> **Real changes are switched off by default.** ShadowDisk changes only its own **practice disks**
> until you turn on **Real disk changes** for the current session. Keep a backup of anything
> important. See [SAFETY.md](SAFETY.md) and [If something goes wrong](docs/IF-SOMETHING-GOES-WRONG.md).

## Why ShadowDisk?

Most partition tools ask you to trust them. ShadowDisk shows you everything and checks every step.

- **Free, with nothing held back:** no trial limits, upgrade prompts or bundled extras.
- **Open source:** the code, the [safety rules](SAFETY.md) and the exact steps of every
  [operation](docs/OPERATIONS.md) are public.
- **Practise first:** create small virtual disks and try any change on them before a real disk.
- **Checked, not assumed:** limits are re-read just before every change, moved data is read back and
  compared piece by piece, and the disk is read again afterwards.
- **Survives a power cut:** a partition move keeps a journal after every piece and can be resumed.
- **Hard limits:** Windows (C:) and the first drive never move; C: can only be extended.
- **Plain language:** when something can't be done, ShadowDisk says why.

It is new and deliberately cautious: it does not clone disks, migrate Windows to a new SSD, shrink
or move C:, or run from a boot USB. Sector-to-sector moves are for NTFS drives.

## Features (v0.3)

**See your disks**
- Every physical disk with model, exact size, GPT/MBR, connection type and sector sizes
- Partitions in disk order (EFI, Microsoft Reserved and Recovery clearly marked as protected)
- Used data, free space inside volumes and unallocated space, as a **bar**, **pie** or table
- **Disk health** (Windows' status, temperature and wear where reported) and a read-only
  **surface scan** for bad sectors

**Change partitions** (practice disks always; real disks only with the session switch)
- Shrink, extend and create volumes; delete a volume; change a drive letter
- **Move partition:** drag a whole drive block along the bar, or let a drive take in the free space
  in front of it
- **Drag the divider** between two drives to move space between them, with a choice of method and an
  estimated time for each:
  - **SECTOR TO SECTOR:** only the used NTFS blocks move, directly on the disk (fast, no extra drive)
  - **DATA COPY:** files are copied out and back and every file's checksum is verified
- **Give space to C:**, including moving the Windows Recovery partition out of the way safely
- **Copy & verify:** copy a drive's files to another drive and check every file

**Built-in protection**
- Preview of the current and proposed layout before anything is applied
- Typed confirmations (`EXTEND D`, `MOVE G`, `DELETE X`…), one attempt, no silent retries
- Pre-checks before multi-step changes, so nothing is half-done for nothing
- Charger warning, and no moves on battery
- **Resume move** after a pause, crash or power cut
- Timestamped activity log in `%LOCALAPPDATA%\ShadowDisk\logs` and a text report export

**Looks**: the Shadow theme (or Light), with a spinning disk that turns while work runs.

## Install

1. Download `ShadowDisk-Setup-x.y.z.exe` from the [Releases](https://github.com/mrchhan/ShadowDisk/releases) page.
2. Run it. You can install for yourself only (no admin prompt) or for all users.
3. Start **ShadowDisk** from the Start menu. **ShadowDisk (demo disks)** opens with simulated disks.

No separate .NET install is needed.

> Windows SmartScreen may warn about an "unrecognized app" because the installer is not yet
> code-signed. Choose **More info → Run anyway** if you downloaded it from this repository.

**Supported:** Windows 10 (1809 or later) and Windows 11, 64-bit.
**Tested on:** a Windows laptop (HDD, MBR) and ShadowDisk practice disks (GPT and MBR).
Please report your results in [Issues](https://github.com/mrchhan/ShadowDisk/issues).

## Build from source

Requirements: [.NET 10 SDK](https://dotnet.microsoft.com/download), Visual Studio 2026 (optional;
open `ShadowDisk.slnx`), and [Inno Setup 6](https://jrsoftware.org/isinfo.php) for the installer.

```powershell
dotnet test  tests\ShadowDisk.Core.Tests        # unit tests (simulated disks only)
dotnet run   --project src\ShadowDisk.App        # run the app
dotnet run   --project src\ShadowDisk.App -- --demo
powershell -ExecutionPolicy Bypass -File build\build-installer.ps1 -Version 0.3.0
```

Every push to GitHub builds and tests the app and produces the installer automatically. Pushing a
tag such as `v0.3.0` publishes a GitHub Release with the installer attached.

## Project layout

| Folder | What it is |
|---|---|
| `src/ShadowDisk.Core` | Disk model, layout maths, safety rules, wizards, move engine and journal. No Windows calls, fully unit-tested |
| `src/ShadowDisk.Windows` | Windows Storage Management API (`MSFT_Disk`, `MSFT_Partition`, `MSFT_Volume`), diskpart, raw disk access for moves |
| `src/ShadowDisk.App` | WPF user interface |
| `tests/ShadowDisk.Core.Tests` | Unit tests using simulated disks only |
| `installer/` | Inno Setup installer script |

More detail: [docs/ARCHITECTURE.md](docs/ARCHITECTURE.md) · Plans: [docs/ROADMAP.md](docs/ROADMAP.md)

## Licence

MIT. Free to use, share and modify. See [LICENSE](LICENSE).
ShadowDisk is provided "as is", without warranty. Keep backups of anything important.

<img src="docs/aishadow-logo.png" width="40" alt="Shadow Creative Studio logo" align="middle"> Made by Hassan Malik · **Shadow Creative Studio**
