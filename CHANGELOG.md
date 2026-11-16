# Changelog

## Unreleased (Stage 3 preview)
- **Practice disks:** create and remove small virtual disk files for safely trying real changes.
- **Create a new volume for real**, on practice disks only: re-checks the disk just before writing,
  final confirmation (backup tick + type CREATE), one attempt with no retry, read-back afterwards.
  See docs/OPERATIONS.md.
- **Shrink and extend for real**, on practice disks only: size limits re-checked just before writing,
  confirmation by typing SHRINK or EXTEND, one Windows call, read-back of the exact new size.
- Clearer plan preview: dashed outline of only the space that changes, drag handle for the size,
  and an explanation when a volume can't be extended (e.g. Recovery sits after C:).
- **Copy & verify** (Stage 5 part 1): copy a drive's files to a new folder on another drive and check
  every file's SHA-256 checksum; never overwrites or deletes.
- **New look: the Shadow theme** (default) — night-blue panels with the AIShadow electric blue and ember orange,
  a hero header with the app and AIShadow logos, grouped toolbar, rounded cards, slim scroll bars and a gradient
  progress bar. **Light theme** still available (toolbar "Theme" button; remembered).
- **A real spinning hard disk:** a drawn platter (tracks, blue and orange reflections, hub, read/write arm and
  activity light) in the header, next to each disk, in the status bar and in the dialogs. It turns slowly when idle;
  while ShadowDisk is working (refresh, changes, copy & verify, health scans, give-space steps) it spins up, the arm
  seeks across the tracks and the light blinks orange.
- **Shadow colours on the charts:** glossy electric-blue→violet used space, night-blue free space, slate system
  partitions, a rounded bar with a glass sheen, and an AIShadow blue→orange ring around the pie.
- **Animations:** a start-up intro (header slides in, toolbar and disk list rise, disks slide in one by one);
  slow aurora glows, spinning rings around the ShadowDisk icon, a shine across the title and a shimmering accent
  line in the header; cards cascade in when a disk is chosen; the bar sweeps in and each volume's used space fills
  up, the pie draws itself clockwise; the selected block or slice breathes with a soft glow; buttons glow on hover
  and press in; the list's selection bar springs open; progress bars shine; the plan preview glows; dialogs zoom
  in; health-scan cells flash as they are read. All motion follows Windows' "Show animations in Windows" setting.
- **Disk health** (Stage 4): the drive's own health counters with a plain verdict, and a read-only surface
  scan for bad (unreadable) and slow areas, with a map, stop/continue, and a saved text report.
- The give-space wizard now warns that file permissions aren't copied, so programs on the moved drive may need repair.
- **Give space to C:** (Stage 5 part 4): the give-space wizard can also move a Recovery partition — Windows
  Recovery off with reagentc, recreate the Recovery partition (≥ 1 GiB) after the extended volume, back on.
  Extending the Windows volume is now allowed (only extending). Full MBR disks remove Recovery first.
  New MBR practice disk shaped like a Windows disk, with simulated Windows Recovery.
- **Give space from the next volume** (Stage 5 part 3): a step-by-step wizard that moves the next volume's
  files (verified copy), deletes and recreates it further along the disk, and extends the selected volume.
  The recreated volume keeps its original label.
- **Delete volume** (Stage 5 part 2): only empty volumes or ones with a verified, unchanged copy from this
  session; system/Windows/Recovery partitions can never be deleted; confirm by typing DELETE <letter>.
- **Change drive letter**: files untouched; letters in use and the Windows drive are refused.
- Copy & verify can **resume** an interrupted copy.
- **Real disk changes (opt-in, per session):** create/shrink/extend on real DATA volumes after switching
  it on. C:, start-up, EFI, MSR and Recovery partitions stay protected. Extra checks: mains power,
  volume health, known BitLocker status; confirm by typing the drive letter.

## 0.2.0
- **Plan a change (preview only):** shrink a volume, extend it into free space after it, or create a
  new volume in unallocated space, and see the current and proposed layouts side by side.
- Size limits come from Windows (`GetSupportedSize`) where possible, otherwise from a cautious estimate
  that is clearly labelled.
- Every plan lists the checks it passed or failed, plus the risks of doing it for real.
- Plans can be included in the exported report, marked "preview only".
- The window now fits small screens (title bar always visible).
- Nothing is ever applied to a disk in this version.

## 0.1.0
- First public preview: read-only disk and partition inspector for Windows 10 and 11.
