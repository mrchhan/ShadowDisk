# Architecture

```
ShadowDisk.App (WPF UI)  ──►  ShadowDisk.Core  ◄──  ShadowDisk.Windows
   views, view models          model, layout,        read-only WMI queries
   bar / pie / table           safety rules,         (Storage Management API)
                               report, log,
                               simulated disks
```

* **UI code never talks to Windows storage APIs directly.** It asks an `IStorageInventory`.
* **`IStorageInventory` is read-only.** It has one method, `GetSnapshotAsync`. Future write operations
  will go behind a separate, narrow, audited interface, one operation at a time.
* **Two inventories exist:** `WindowsStorageInventory` (real disks, read-only) and
  `FixtureInventory` (simulated disks from JSON in `src/ShadowDisk.Core/Fixtures`).
* **All interpretation happens in Core.** The Windows backend only copies raw values
  (`RawDisk`, `RawPartition`, `RawVolume`, `RawEncryption`). `StorageMapper` decides what they mean,
  so the logic is unit-tested without hardware.

## Concepts

| Concept | Meaning |
|---|---|
| Physical disk | A drive Windows sees (`MSFT_Disk`) |
| Partition | A byte range on a disk with a type (`MSFT_Partition`) |
| Volume | A file system inside a partition, which may have a drive letter (`MSFT_Volume`) |
| Unallocated | Disk space of 1 MiB or more that is not inside any partition |
| Gap | Smaller slack (partition-table area, alignment) that cannot hold a partition |

A drive letter belongs to a volume. It is never treated as a partition.

## Sizes

All sizes are exact byte counts (`long`). Text uses explicit binary units (GiB = 1024³ bytes) and
truncates rather than rounding up, so the app never shows more space than exists.

## Windows APIs used (v0.1)

| Purpose | API | Admin needed |
|---|---|---|
| Disks | `root\Microsoft\Windows\Storage : MSFT_Disk` | No |
| Partitions | `MSFT_Partition` | No |
| Volumes | `MSFT_Volume` | No |
| BitLocker | `root\CIMV2\Security\MicrosoftVolumeEncryption : Win32_EncryptableVolume` (+ read-only `GetLockStatus`) | Yes |

Only `SELECT` queries and `GetLockStatus` are used. No method that changes a disk is called.
