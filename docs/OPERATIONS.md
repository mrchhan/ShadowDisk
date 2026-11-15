# Real operations

Every operation that can change a disk is documented here **before** it is enabled.
Operations are enabled **only on ShadowDisk practice disks** until they have been proven there.

## Practice disks

A practice disk is a dynamically expanding virtual disk file (`.vhd`) created by ShadowDisk in
`%LOCALAPPDATA%\ShadowDisk\practice`. Windows attaches it and shows it as a normal disk
("File Backed Virtual"). ShadowDisk initialises it as GPT.

* Created and removed with Windows' `diskpart`, which selects the virtual disk **by file path only**.
  No script ever selects a disk by number.
* A disk counts as a practice disk only when Windows reports it as a file-backed virtual disk **and**
  its file is a `.vhd` directly inside the practice folder.
* Removing practice disks detaches them and deletes their files, including anything stored on them.
* Needs administrator rights.

## Create a new volume (enabled on practice disks, v0.3 preview)

**What it does:** creates one basic partition in unallocated space, quick-formats it as NTFS with the
label "Practice", then gives it a drive letter.

**Preconditions (all checked immediately before writing):**
1. ShadowDisk is running as administrator.
2. The disk is re-read and has the same number, Windows identifier (`UniqueId`) and layout as when
   the plan was made. Any difference → refused, nothing written.
3. The disk is a ShadowDisk practice disk, not the Windows disk, online and writable.
4. The plan passes every check again on the fresh layout: unallocated space, at least 16 MiB,
   1 MiB-aligned start and size, room for the GPT backup table, MBR four-primary limit.
5. The user ticked the backup box and typed `CREATE` on the confirmation screen.
6. The Windows backend checks **again, independently**, that the live disk is the same practice disk
   (bus type, file location, identifier) before its first call.

**Windows calls, each made once:**
`MSFT_Disk.CreatePartition` (no drive letter) → `MSFT_Volume.Format` (NTFS, quick) →
`MSFT_Partition.AddAccessPath` (assign a letter).

**Failure handling and rollback limits:**
* No retries. Cancellation is only possible before the first Windows call.
* If a later step fails, earlier steps are **not undone**. The result is reported as
  "partly applied", the disk is read again, and the real state is shown. On a practice disk the
  simplest fix is to remove it and create a new one.
* Afterwards the disk is always re-read; success is only reported if the new partition exists at the
  planned offset with the planned size and an NTFS volume.

**Tests:** `OperationTests` (fake backend, no real disks) cover success with read-back, refusal of
physical disks and non-practice virtual disks, layout changed since planning, different disk with the
same number, size no longer fitting, cancellation, failure without retry, a crash in the backend, and
a partial result (partition created, format failed).

**Manual test on Windows (practice disk only):**
1. Restart ShadowDisk as administrator and click **New practice disk**. A ~1 GiB "Msft Virtual Disk"
   appears, marked "Practice disk".
2. Select its unallocated space → Plan a change → Create a new volume → choose a size.
3. Click **Create this volume for real…**; check the confirmation screen names the practice disk.
   Cancel once (nothing should change), then repeat and confirm.
4. ShadowDisk reports the result and refreshes; Windows Disk Management shows the same new volume.
5. Try the same on a physical disk: the Apply button must not appear (only an explanation).
6. Click **Remove practice disks**; the disk disappears and its file is deleted.

## Shrink and extend a volume (enabled on practice disks, v0.3 preview)

**What it does:** one call to `MSFT_Partition.Resize` (the API behind `Resize-Partition`), which
resizes the partition and its NTFS/ReFS file system together. Shrinking releases space at the END of
the volume; Windows may first move files out of that area. Extending uses unallocated space directly
after the volume. The start of the volume never moves.

**Preconditions (all checked immediately before writing):**
1. ShadowDisk is running as administrator.
2. Same disk (number + `UniqueId`), same layout, and the partition is at the same offset with the same
   size as when the plan was made.
3. The disk is a ShadowDisk practice disk, not the Windows disk, online and writable.
4. The size limits are asked for **again** (from Windows where possible) and the plan must pass every
   check against them: NTFS/ReFS only, not protected, data fits, 1 MiB aligned, within the range,
   and for extend, unallocated space directly after the volume.
5. The user ticked the backup box and typed `SHRINK` or `EXTEND` on the confirmation screen.
6. The Windows backend independently re-checks the live disk and the partition's position.

**Failure handling and rollback limits:**
* One call, no retries, no cancellation once Windows has been asked.
* Windows' resize either completes or reports an error; ShadowDisk does not try to undo or correct it.
* Success is only reported when the read-back shows the partition at the same offset with exactly the
  planned size. A different size is reported as "partly applied"; a missing partition is reported with
  advice to check Disk Management before doing anything else.
* An extend can be reversed by a shrink only as far as Windows allows at that time.

**Tests:** `OperationTests` cover shrink and extend success with read-back, shrink below the fresh
limit refused, physical disk refused, partition changed since planning refused, failure without retry,
Windows resizing to a different size (partial), and a same-size request doing nothing.

**Manual test (practice disk only):** create a practice disk and a 200 MiB volume, then
extend it to about 600 MiB, then shrink it to about 300 MiB, checking each result in Disk Management
and that a test file copied onto the volume still opens afterwards.

## Real disks (opt-in, data volumes only)

Create, shrink and extend can be used on real disks only after the user switches on
**Real disk changes** (toolbar). The switch:
* needs administrator rights and a warning to be accepted;
* is **never saved** — every start of ShadowDisk begins with it off;
* turns the header badge red ("REAL DISK CHANGES ON — back up first").

Even with the switch on, these stay protected: the volume Windows runs from (C:), the system /
start-up partition (System Reserved or EFI), MSR, Recovery and unrecognised partitions, dynamic disks.

Extra checks for real disks, on top of every practice-disk check:
* the laptop is on mains power (desktops that can't report power are allowed);
* Windows reports the disk and the target volume as healthy;
* BitLocker status of the target volume is known (and it is not locked);
* the Windows backend re-checks, live, that the partition is not Windows' boot or system partition;
* to confirm a resize the user types the **drive letter** (e.g. `E`) and ticks that a backup exists.

Suggested first real test: copy everything on the data volume to another drive (for example a folder
on C: if it has room), check a few files open, then shrink the data volume by a small amount
(e.g. 10 GiB) and extend it back.

## Not yet enabled
* Changes to the Windows volume (C:) and system partitions.
* Moving partitions (Stage 5).
