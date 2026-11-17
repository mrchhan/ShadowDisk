# If something goes wrong

Stay calm and **don't run other disk tools, "repair" prompts or formatting** on the affected disk
until you have read this. In almost every case your files are still on the disk. Most mistakes happen
when people try several fixes at once.

First, open ShadowDisk's **Activity log** (bottom left) or the **Log folder** (toolbar). Each step
ShadowDisk took, and the exact message of anything that failed, is in
`%LOCALAPPDATA%\ShadowDisk\logs`. Please attach that log if you open an
[Issue](https://github.com/mrchhan/ShadowDisk/issues).

## A move was interrupted (power cut, crash, closed window, paused)

**What you see:** the drive has disappeared from Explorer, and ShadowDisk shows a red banner
*"A move did not finish…"* with a **Resume move…** button.

**What happened:** while a drive moves, ShadowDisk hides it from Windows on purpose, so nothing can
open or "repair" it half-moved. After every piece it saves a journal in
`%LOCALAPPDATA%\ShadowDisk\moves`. Every piece before the stop is already correct in the new place;
every piece after it is still in the old place.

**What to do:**
1. Plug in the charger and close other programs.
2. Open ShadowDisk **as administrator** and click **Resume move…**. It continues from the last checked piece.
3. When it finishes, the drive comes back under its old letter. Open a few files to check.

**Don't:** delete the journal file, format the "unknown" partition Windows may show in Disk
Management, or let `chkdsk` run on it before the move is resumed.

If Resume itself reports an error, stop and open an Issue with the log and the journal file. Don't try again and again.

## A multi-step change stopped half-way

Examples: *give space* or *drag the divider* stopped after the shrink.

ShadowDisk always tells you which steps are done (they stay done) and which are not. The usual result
is just some **unallocated space**: no data is lost. You can:

- give it to the drive before it: select that drive → **Plan a change → Extend**; or
- let the drive after it **take in the free space in front of it** (Move this partition…); or
- leave it and run the same change again later.

With DATA COPY changes, a **verified copy of the files always exists** at every moment, in the
temporary volume if it is still there. Don't delete a temporary volume yourself: ShadowDisk
only deletes it after checking the copy.

## A drive came back with a different letter, or none

Open **Disk Management** (toolbar button) → right-click the drive → *Change Drive Letter and Paths…*,
or use ShadowDisk's **Change drive letter…**.

## Windows Recovery stopped working after "Give space to C:"

ShadowDisk turns Windows Recovery off before moving the Recovery partition and turns it on again
afterwards. If it stays off, open a **Command Prompt as administrator** and run:

```
reagentc /info
reagentc /enable
```

`reagentc /info` should then say *Windows RE status: Enabled*.

## The disk shows errors or the surface scan finds bad sectors

**Stop changing partitions on that disk.** Copy your important files to another drive first
(ShadowDisk's **Copy & verify…** checks every copy), then replace the disk. Moving data around on a
failing disk is how people lose it.

## Getting help

Open an [Issue](https://github.com/mrchhan/ShadowDisk/issues) with:
- what you were doing (a screenshot of the ShadowDisk window helps);
- the log file from `%LOCALAPPDATA%\ShadowDisk\logs`;
- for an interrupted move, the journal file from `%LOCALAPPDATA%\ShadowDisk\moves`.

ShadowDisk is free and provided "as is", without warranty. Keep a backup of anything important.
