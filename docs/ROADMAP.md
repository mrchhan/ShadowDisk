# Roadmap

Each stage ships only when the previous one is stable and tested.

## Stage 1: Read-only inventory (v0.1, done)
- Disks, partitions, volumes, unallocated space; bar, pie and table views
- Safety notices and "why this would be blocked" explanations
- Report export, activity log, demo mode, installer

## Stage 2: Planning with a simulated backend (v0.2, done)
- Draft a change (shrink, extend, create volume) and see a **preview** layout next to the current one
- Use Windows' own limits (`Get-PartitionSupportedSize`) for minimum and maximum sizes
- Validation against alignment, MBR limits, protected partitions, BitLocker and health
- All planning runs against a mock backend; nothing is applied

## Stage 3: Carefully scoped real operations (v0.3, released; more in later versions)
Progress: practice disks and "create a new volume" on practice disks are built (see docs/OPERATIONS.md).

Only operations Windows supports natively, added **one at a time**, each with documented behaviour,
preconditions, rollback limits and tests on disposable virtual disks (VHDX):
1. Create a new volume in unallocated space
2. Extend a volume into adjacent unallocated space
3. Shrink a volume
MBR↔GPT conversion and merging partitions are not planned.

## Stage 5: Give space to C: (built in v0.3 preview: parts 1–4)
Goal: give free space from a later data volume (e.g. E:) to the Windows volume C:, which Windows'
own tools can't do because a volume can only grow into space directly after it.

ShadowDisk does this **without moving data block by block**. It only uses operations Windows
supports, plus a verified file copy, and moves the Recovery partition with Microsoft's reagentc tool.
See [OPERATIONS.md](OPERATIONS.md#give-space-to-c-stage-5-part-4--built).

## Stage 4: Health, polish and recovery guidance
- ✅ Detailed drive-health counters (`MSFT_StorageReliabilityCounter`), clearly separate from scans
- ✅ Optional read-only surface scan: cancellable, with a warning about stressing failing drives
- Accessibility review, dark mode, code-signed installer, recovery guidance pages
