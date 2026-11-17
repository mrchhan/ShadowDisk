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

## Give space to C: (Stage 5, part 4 — built)

Same wizard as part 3 (**Give space from the next volume…**), extended so a **Recovery partition**
between the selected volume and the next one can be moved too. Select C: and click the button.

**Why it's needed:** on a disk laid out as `System | C: | Recovery | E:`, C: can only grow into space
directly after it, and both Recovery and E: are in the way.

**Why not move partitions block by block** (as many partition tools do): if power fails while a
partition is being moved, the half-moved partition can be lost. ShadowDisk uses only operations Windows
supports, each of which completes or fails as a whole, plus a file copy verified before anything is deleted.

**Before it starts, the plan is refused if:**
* the Recovery partition is bigger than 2 GiB (it may hold the PC maker's factory image), or
* `reagentc /info` does not say Windows Recovery is on **that** partition (it may hold other tools), or
  Windows Recovery is off (so its contents can't be confirmed), or
* the target is a start-up partition (System Reserved / EFI). The Windows volume itself may only be
  **extended**: shrinking, deleting or relettering C: stays blocked, in the policy and again in the Windows backend.

**Steps** (each one re-reads the disk, runs once, and is read back):
1. Shrink E: if needed, create a temporary volume at the end, copy & verify E:'s files, delete the old E:
   (typed `DELETE E`). Same as part 3.
2. **Turn off Windows Recovery** — `reagentc /disable`, which moves `Winre.wim` into
   `C:\Windows\System32\Recovery`. ShadowDisk then checks that reagentc reports it off **and** that the image
   file is there. If not, it stops and the Recovery partition is not touched.
3. **Delete the old Recovery partition** (only while the image is confirmed on C:).
4. **Extend C:**, leaving exactly enough room after it for the new Recovery partition.
5. **Create a new Recovery partition** directly after C: with diskpart, as Microsoft KB5028997 does
   (`id=27` on MBR; the Recovery GUID and `gpt attributes=0x8000000000000001` on GPT; quick NTFS format).
   It is at least 1 GiB, so later Windows updates to WinRE have room.
6. **Turn Windows Recovery back on** — `reagentc /enable` moves the image onto the new partition. ShadowDisk
   checks where reagentc reports it.
7. Give the temporary volume E:'s letter.

**Full MBR disks:** an MBR disk holds at most four partitions. With `System Reserved | C: | Recovery | E:` there
is no slot for the temporary volume, so steps 2–3 run **first** and Windows Recovery stays off during the copy.
The wizard says so before you start.

**If the space after Recovery is already free** (for example after shrinking E: earlier), there is nothing to
copy: only steps 2–6 run.

**If a step fails while Windows Recovery is off,** the wizard stops and says so. Windows still starts normally;
the image is safe in `C:\Windows\System32\Recovery`. Run `reagentc /enable` as administrator to turn Windows
Recovery back on (it then lives on C:), or try the step again.

**Practice first:** “New practice disk” → **No** creates an MBR practice disk shaped like this
(`Practice System | Practice Windows | Recovery | Practice data`). Windows Recovery is *simulated* on practice
disks; reagentc is only used for the real Windows disk.

**Time:** dominated by copying E:'s used data twice (copy, then checksum).

**Programs installed on E:** files are copied one by one and E: keeps its letter, but Windows file permissions
(ACLs), hard links and similar file-system details are not copied. Programs installed on E: may need repairing or
reinstalling afterwards. Documents, photos and other plain files are not affected. The wizard says this before
you start.

## Drag to rebalance (move space between neighbouring drives)

In **Bar** view, the line between two neighbouring data volumes has a grip. Drag it, like in other partition
tools: the bar shows where the line will be and both new sizes. **Nothing changes on release**: the wizard opens
with that amount chosen, and you confirm and run every step yourself.

* **Drag right** (the left drive grows) → the give-space wizard (above): the right drive's files move further
  along the disk as a verified copy.
* **Drag left** (the right drive grows) → Windows can't grow a volume towards the start of the disk, so:
  1. shrink the left drive;
  2. shrink the right drive (keeping its files) to make room at its end;
  3. create a temporary volume there and **copy & verify** the right drive's files into it;
  4. delete the old right drive (typed `DELETE X`, only with the verified copy);
  5. create the new, bigger right drive starting just after the smaller left drive;
  6. **copy the files back and verify** them again;
  7. delete the temporary volume (typed confirmation, only with the verified copy back);
  8. extend the new drive over the rest and give it its old letter.

  At every moment a verified copy of the files exists. The right drive must be less than about half full
  (its files must fit at the end of its own space). Its files are copied twice, so this takes a while.

Since the drag opens the **method choice** (next section), these data-copy steps are now the **DATA COPY** option;
**SECTOR TO SECTOR** does the same change by moving the right drive's blocks, without a second copy of its files.

The Windows volume (C:) can only grow; its line can only be dragged towards the end of the disk, and that uses the
give-space wizard (with the Recovery move where needed).

## Move partition — SECTOR TO SECTOR or DATA COPY

Like other partition tools, a whole drive block can now move along the disk. Three ways to start:

* **On the bar:** drag a drive's block into the free space next to it. Locked drives show a hatch and a lock while you drag.
* **On the bar:** drag the divider between two drives (see above).
* **From the selected drive:** “Move this partition…” (a slider for the new start, and *Take in the free space in front
  of it* — the drive moves left and then grows by the same amount).

**Never moves:** the first drive and the Windows drive (C:), System/EFI/MSR/Recovery partitions, BitLocker-encrypted
drives (turn BitLocker off first), non-NTFS drives (sector to sector is NTFS-only), drives without a letter, and drives
on disks with an extended partition.

The window shows the steps, then a **method choice with an estimated time for each**, based on a short read-only speed
test of the disk (128 MB):

* **SECTOR TO SECTOR (recommended)** — only the used blocks move (NTFS's own map of used space), directly on the disk.
  No extra drive, no renaming, no room needed for a copy. Estimated from the used space and the measured speed.
* **DATA COPY** — the verified-copy wizards (files out and back, every file checked). Slower and needs free space for a
  copy; available when two drives share space (divider drag), not for changing where a single drive starts.

**Power caution:** charge a laptop fully and keep the charger plugged in until the move finishes; the window shows the
live power state. Moving is blocked on battery.

**How a sector-to-sector move protects the data**
1. Fresh read of the disk; same disk, same layout, same partition, the new place is free; policy checks.
2. The drive is locked (refused if any file is open) and NTFS's map of used space is read; then it is dismounted.
3. A **journal** is saved (`%LOCALAPPDATA%\ShadowDisk\moves`), then the partition is **hidden** from Windows (a
   ShadowDisk partition type on GPT, type 0x7F on MBR) so nothing can mount or “repair” it half-moved. The original
   type is in the journal before it is changed.
4. Used ranges are copied in pieces no larger than the move distance (max 8 MiB), in the safe order (first to last when
   moving left, last to first when moving right), so a piece never overwrites data that has not been copied yet.
   Each piece is written, flushed to the disk, **read back and compared**, and the journal is saved after every piece.
   Raw access is limited to the move's area; anything outside is refused.
5. The NTFS boot sector and its backup get the new start; the partition table gets the new start and the original type
   in one write; Windows mounts it again and ShadowDisk gives it back its letter.

**Pause, power cut or crash:** a paused or interrupted move stays hidden from Windows. When ShadowDisk starts it shows
a red banner, **Resume move…**, which continues from the last checked piece (a piece is simply copied again). Every
earlier piece is still correct in its new place, and every later piece is still in its old place.

**Divider drags with SECTOR TO SECTOR:** shrink one drive (Windows), move the right drive (above), extend the other
drive (Windows). Each step re-reads the disk first; it stops at the first step that does not succeed and says which
steps are done.

## Disk health and surface scan (Stage 4 — built, read-only)

**Disk health…** (toolbar) opens a window for the selected disk.

**What the drive reports** — read from Windows' `MSFT_PhysicalDisk` and `MSFT_StorageReliabilityCounter`
(the data behind `Get-StorageReliabilityCounter`): Windows' health status, temperature, hours powered on,
read/write errors (total and uncorrected), wear (SSDs) and start/stop cycles. ShadowDisk turns them into a plain
verdict: *No problems reported*, *Worth keeping an eye on*, or *Needs attention — back up now* (for example
uncorrected errors, Windows reporting the drive Unhealthy, or wear of 90% or more). Many RAID controllers and USB
adapters don't pass these counters on; that is shown as "not reported", not as a problem. **This is the drive's own
report, not a scan.**

**Surface scan** — reads the whole disk once through `\\.\PhysicalDriveN`, opened **read-only** and without the
Windows cache, 1 MiB at a time, and times every read:
* **Unreadable** — Windows returned an error (for example a CRC error). That area is **never read again**: re-reading
  weak areas is what stresses a failing drive most.
* **Slow** — the read took 500 ms or more, often a sign the drive is struggling with that area.
* After **20 unreadable areas** the scan stops by itself and says to copy important files off the disk instead.
* Stop any time; **Continue scan** carries on from where it stopped (while the window stays open).
* Each problem shows which volume it is in. **Save results…** writes a text report.

Nothing is ever written or repaired. A clean scan does not guarantee the disk won't fail later.

## Not yet enabled
* Shrinking, deleting or relettering the Windows volume (C:); any change to System Reserved, EFI or MSR.
* Moving FAT32/exFAT drives, BitLocker drives, logical drives, or the first drive and C: (sector to sector).
* Resuming the give-space wizard after ShadowDisk is closed (finish the remaining steps by hand, or start again).
