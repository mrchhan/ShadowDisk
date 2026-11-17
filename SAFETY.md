# Safety principles

ShadowDisk is built around one rule: **your data matters more than any feature.**

No partition tool can guarantee that data is never lost. Power cuts, failing hardware, encryption,
unsupported file systems and Windows limitations can all cause an operation to fail. ShadowDisk
will never claim otherwise.

## Rules every version follows

1. **Read-only by default.** Inspecting disks never changes them.
2. **Plans are not actions.** Dragging a chart or typing a size will only ever change a *preview*,
   which is clearly labelled as a preview.
3. **Validate first.** Before any real change: the physical disk, partition, file system, current
   layout, requested size and what Windows itself supports are all checked again.
4. **Explain, then confirm.** You see what will change and what could go wrong, then confirm
   explicitly. Every operation is confirmed separately.
5. **Backups are never faked.** ShadowDisk will recommend a backup. It will only say a backup is
   verified if it has actually checked it.
6. **No surprise erasing.** Format, delete, initialize, overwrite and convert actions only happen
   when that exact action is requested and confirmed.
7. **No silent retries, no half-applied plans.** If something fails, you are told, and the real
   disk state is read back and shown.
8. **Protected partitions stay protected.** EFI System and Microsoft Reserved partitions are never
   offered for changes. Dynamic disks are not supported at all.
9. **Health is not a scan.** The status Windows reports is shown as exactly that. The surface scan
   is read-only (the disk is opened for reading only), cancellable, never re-reads an area that
   failed, stops by itself after many unreadable areas, and warns that scanning can stress a
   failing drive.
10. **Development never touches real disks.** Tests use simulated disks or disposable virtual disks.

## Version 0.3

Real changes are enabled on ShadowDisk practice disks (virtual disk files ShadowDisk creates).
Real disks stay read-only unless you switch on **Real disk changes** for the current session; even
then only data volumes can be changed. The Windows volume (C:) may only be **extended** (never shrunk,
deleted or given another letter), and System Reserved, EFI and MSR stay protected. A Recovery partition
is only moved by the give-space wizard, and only after Windows Recovery is turned off and its image is
confirmed safe on C:. A sector-to-sector **move** never touches the first drive or C:, hides the
partition from Windows while it moves, checks every piece by reading it back, and keeps a journal after
every piece so it can be resumed after a pause or power cut. Each operation
is documented in [docs/OPERATIONS.md](docs/OPERATIONS.md) before it is enabled.

## Versions 0.1 and 0.2

Version 0.2 adds change *planning*, which only produces previews. It still contains **no code that writes to disks**. The storage interface it uses
(`IStorageInventory`) has no write methods.

## If something goes wrong

See [docs/IF-SOMETHING-GOES-WRONG.md](docs/IF-SOMETHING-GOES-WRONG.md): resuming an interrupted move,
what a half-finished change leaves behind, drive letters, Windows Recovery and failing disks.

## Reporting a problem

Please open an issue. Do not include full serial numbers or file names. ShadowDisk's report masks
serial numbers for you.
