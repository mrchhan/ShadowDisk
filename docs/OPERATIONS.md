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

## Copy & verify (Stage 5, part 1 — built)

Copies every file from one drive into a **new, empty folder** on another drive, then reads every copy
back and compares its SHA-256 checksum with the checksum taken while reading the original.

* The source is only read. Nothing is overwritten (a non-empty destination is refused, and every file
  is created with "create new"). Nothing is deleted.
* Refused before starting if the destination is inside the source or there is not enough free space.
* Skipped and listed: `System Volume Information`, `$RECYCLE.BIN` and `Recovery` at the drive root
  (Windows recreates them), and links/junctions (not followed, so nothing is copied twice).
* Files that can't be read (e.g. locked) are listed as problems; the result is then "NOT complete".
* Timestamps are kept; on Windows, file attributes are kept (except compression/encryption flags).
* Each copied file is flushed to disk before verification.
* Cancel stops at the next file; the source is untouched and the partial copy can be deleted.
* Only a fully verified copy will ever allow the Stage 5 wizard to delete an original volume.

**Tests:** `CopyTests` cover a full verified copy, the source being unchanged, timestamps, a corrupted
copy being caught, refusing a non-empty destination, not enough space, destination inside source,
cancelling, skipped system folders, links not followed, and progress reporting.

## Delete volume (Stage 5, part 2 — built)

The first operation that removes data, so it has the strictest rules:

* Only ordinary data partitions. Never the Windows volume, the system/start-up partition (System
  Reserved, EFI), MSR, Recovery, unrecognised or dynamic-disk partitions — checked in the runner AND,
  independently, by the Windows backend (`IsBoot`/`IsSystem`) on every disk.
* Real disks need "Real disk changes: ON"; practice disks don't.
* **Files must be safe to lose.** ShadowDisk scans the volume and allows deletion only if it holds no
  user files, or if a **verified copy of that drive was made with Copy & verify in this session** and
  no file or folder on it was created or modified after that copy started. A volume without a drive
  letter can't be scanned, so it is refused.
* Confirmation: tick that the verified copy opens, and type `DELETE <letter>` (e.g. `DELETE G`).
* One call to `MSFT_Partition.DeleteObject`; success only when the read-back shows the partition gone.

## Change drive letter (Stage 5, part 2 — built)

`MSFT_Partition.RemoveAccessPath` (old letter) then `AddAccessPath` (new letter). Files are not touched.
Letters D–Z only, never one already in use. The Windows volume and system partitions are refused.
If the old letter is removed but the new one can't be added, the result is "partly applied" and the
volume simply has no letter until one is given.

## Give space to the drive before (Stage 5, part 3 — built)

For a layout `| target | donor | (free) |`, gives part of the donor's space to the target, which Windows
can't do directly. Data volumes only: the target must not be the Windows volume (that is part 4), and
the donor must be an ordinary data volume with a drive letter (Recovery and system partitions are refused).

The user chooses how much to give: at least what Windows lets the donor shrink to, at most the area
minus the donor's files plus headroom (10% or 256 MiB, whichever is larger). Then, one step per click:

1. **Shrink the donor** to the amount being given (skipped if the copy fits in free space after it).
2. **Create a temporary NTFS volume** at the end of the area, after the space being given.
3. **Copy & verify** the donor's files into it (resumable; every SHA-256 checksum must match).
4. **Delete the old donor** — only if step 3 verified and nothing changed since; typed `DELETE <letter>`.
5. **Extend the target** to the maximum Windows reports.
6. **Give the new volume the donor's old letter.**

Each step re-reads the disk and goes through the same runner checks as the individual operations.
A failed step stops the wizard; it can be run again (the copy resumes). Declining the delete changes
nothing. If the window is closed part-way, nothing further happens and the remaining steps can be done
by hand with the individual tools. (v1 does not yet resume the wizard itself after ShadowDisk restarts.)

**Tests:** `GiveSpaceTests` run the whole wizard on a simulated practice disk with real files, plus
declining the delete, a failed copy stopping before any delete, and the planner's refusals
(Windows volume, Recovery in the way, free space already after the target, donor too full).

## Planned: Give space to C: (Stage 5)

**Status:** design only. Nothing below is built. It will be built and proven on practice disks first.

**Why it's needed:** on a disk laid out as `System | C: | Recovery | E: | free`, space freed from E:
lands at the end of the disk, and C: can only grow into space directly after it. Recovery and E: are
in the way.

**Why not move partitions block by block** (the way many partition tools do): if power fails while a
partition is being moved, the half-moved partition can be lost. ShadowDisk instead uses only
operations Windows supports, each of which either completes or fails cleanly, plus a file copy that is
verified before anything is deleted.

**Steps (a guided wizard, one step at a time, each confirmed):**
1. **Check:** mains power, C: and E: healthy (`chkdsk` scan, read-only), BitLocker status known,
   enough room (E:'s used data must fit in the space a shrink can free), and a full backup confirmed.
2. **Shrink E:** to its data plus headroom. The freed space appears at the end of the disk.
3. **Create a temporary volume** ("E-new") in that freed space.
4. **Copy every file** from E: to E-new with file attributes and timestamps, then **verify**: same
   file count, same total size, and a SHA-256 checksum of every file. Any mismatch stops the wizard;
   E: is untouched and the copy can simply be deleted.
5. **Delete E:** only after verification passes, and only after the user confirms again.
6. **Move Recovery** the documented Microsoft way: `reagentc /disable` (Windows keeps WinRE inside
   C:), delete the old Recovery partition, create a new one at the end of the free space, copy WinRE
   back and `reagentc /enable`. If any part fails, WinRE stays safely inside C:, which still works.
7. **Extend C:** into the space now directly after it (Windows supports extending its own volume
   while running).
8. **Rename E-new to E:** so apps and shortcuts find their files again.

**Time:** dominated by copying E:'s *used* data (twice for verification: copy, then checksum). On a
laptop hard disk expect roughly 30–60 minutes for ~45 GB. Moving partitions with other tools also
copies all used data.

**What can go wrong, and the state it leaves:**
* Failure in steps 1–4: nothing on C: or E: has changed except E: being smaller; remove E-new and
  extend E: back.
* Failure in step 6: WinRE remains inside C: (Windows still boots and can still reach recovery
  options); ShadowDisk shows the exact `reagentc` status.
* Failure in step 7: C: keeps its old size; the free space stays unallocated and can be used later.
* Power loss: each step is a separate Windows operation that completes or fails as a whole; the
  wizard re-reads the disk on next start and shows which step was last completed.

## Not yet enabled
* Changes to the Windows volume (C:) and system partitions (Stage 5 will allow *extending* C: only).
* Give space to C: (Stage 5, designed above).
