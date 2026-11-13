# Changelog

## Unreleased (Stage 3 preview)
- **Practice disks:** create and remove small virtual disk files for safely trying real changes.
- **Create a new volume for real**, on practice disks only: re-checks the disk just before writing,
  final confirmation (backup tick + type CREATE), one attempt with no retry, read-back afterwards.
  See docs/OPERATIONS.md.
- Physical disks remain read-only.

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
