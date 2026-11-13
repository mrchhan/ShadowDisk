using ShadowDisk.Core.Layout;
using ShadowDisk.Core.Mapping;
using ShadowDisk.Core.Model;
using ShadowDisk.Core.Safety;

namespace ShadowDisk.Core.Planning;

/// <summary>
/// Checks a planned change against the current layout and size limits and, if it passes, builds the
/// proposed layout for the preview. Nothing here touches a disk.
/// </summary>
public static class PlanValidator
{
    public static PlanResult Validate(DiskLayout layout, PlanRequest request, SizeLimits limits)
    {
        var checks = new List<PlanCheck>();
        var target = request.TargetSizeBytes;
        var p = request.Partition;

        // 1. The basic safety rules from Stage 1 (protected partitions, dynamic disks, health…)
        if (request.Kind == PlanKind.CreateVolume)
        {
            AddAssessment(checks, SafetyAnalyzer.AssessUnallocated(layout, request.Region));
            if (InsideExtendedContainer(layout, request.Region))
                checks.Add(Fail("Free space inside an MBR extended partition is not supported in this version."));
        }
        else if (p is not null)
        {
            var action = request.Kind == PlanKind.ShrinkVolume ? PlannedAction.ShrinkVolume : PlannedAction.ExtendVolume;
            AddAssessment(checks, SafetyAnalyzer.AssessPartition(layout, p).Single(a => a.Action == action));
            if (p.Kind == PartitionKind.Logical)
                checks.Add(Fail("Logical partitions (inside an MBR extended partition) are not supported in this version."));
        }
        else
        {
            checks.Add(Fail("No partition was selected."));
        }

        // 2. Size limits
        if (!limits.IsUsable)
        {
            checks.Add(Fail(request.Kind switch
            {
                PlanKind.ShrinkVolume => "There is no room to shrink this volume: its data already fills the space Windows allows.",
                PlanKind.ExtendVolume => "There is no unallocated space directly after this volume to extend into.",
                _ => $"This unallocated space is smaller than the {ByteSize.Format(PlanLimits.MinimumNewVolume)} minimum for a new volume.",
            }));
        }
        else
        {
            checks.Add(target % PlanLimits.Alignment == 0
                ? Pass("The size is a whole number of MiB, so the partition stays aligned.")
                : Fail("The size must be a whole number of MiB so the partition stays aligned."));

            if (target < limits.MinBytes)
                checks.Add(Fail($"{ByteSize.Format(target)} is below the minimum of {ByteSize.Format(limits.MinBytes)}."));
            else if (target > limits.MaxBytes)
                checks.Add(Fail($"{ByteSize.Format(target)} is above the maximum of {ByteSize.Format(limits.MaxBytes)}."));
            else
                checks.Add(Pass($"The size is within the allowed range ({ByteSize.Format(limits.MinBytes)} – {ByteSize.Format(limits.MaxBytes)})."));
        }

        checks.Add(limits.Source switch
        {
            LimitsSource.Windows => Pass("The limits were reported by Windows."),
            LimitsSource.FreeSpace => Pass("The limits come from the size of the unallocated space."),
            _ => Warn(limits.Note ?? "The limits are ShadowDisk's estimate, not Windows' exact figures."),
        });

        // 3. Plan-specific checks
        if (p is not null && request.Kind == PlanKind.ShrinkVolume && p.Volume is { } v)
        {
            var footprint = PlanLimits.DataFootprint(p);
            if (target >= footprint)
            {
                checks.Add(Pass($"The {ByteSize.Format(v.UsedBytes)} of data on {p.DisplayName} fits in the new size."));
                var freeAfter = target - footprint;
                if (freeAfter < target / 10)
                    checks.Add(Warn($"Only {ByteSize.Format(freeAfter)} would be left free on {p.DisplayName}. Windows and apps need free space to work well."));
            }
            else
            {
                checks.Add(Fail($"The {ByteSize.Format(v.UsedBytes)} of data on {p.DisplayName} would not fit in {ByteSize.Format(target)}."));
            }
        }
        if (p?.Volume?.Encryption == EncryptionState.Encrypted)
            checks.Add(Warn("BitLocker is on. Save your recovery key somewhere other than this PC first."));
        if (p?.Volume?.Encryption == EncryptionState.Unknown && request.Kind != PlanKind.CreateVolume)
            checks.Add(Warn("BitLocker status is unknown. Use “Restart as administrator” to check it."));
        if (p?.IsBoot == true)
            checks.Add(Warn("Windows runs from this volume. A full backup is essential before any real change."));

        var risks = BuildRisks(request);
        var valid = checks.All(c => c.Outcome != CheckOutcome.Failed);
        if (!valid)
            return new PlanResult(request, limits, checks, null, null,
                "This plan can't be used as it is. See the checks below.", risks);

        var (proposed, region) = BuildProposed(layout, request);
        var (changedOffset, changedLength) = ChangedRange(request, region);
        return new PlanResult(request, limits, checks, proposed, region, Summarize(layout, request, region), risks,
            changedOffset, changedLength);
    }

    private static (DiskLayout Layout, DiskRegion? Region) BuildProposed(DiskLayout layout, PlanRequest request)
    {
        var disk = layout.Disk;
        var parts = disk.Partitions.ToList();
        Partition changed;

        if (request.Kind == PlanKind.CreateVolume)
        {
            var (offset, _) = PlanLimits.ForNewVolume(layout, request.Region);
            var number = parts.Count == 0 ? 1 : parts.Max(x => x.PartitionNumber) + 1;
            changed = new Partition(
                DiskNumber: disk.Number,
                PartitionNumber: number,
                OffsetBytes: offset,
                SizeBytes: request.TargetSizeBytes,
                Kind: PartitionKind.Basic,
                GptType: disk.Style == PartitionStyle.Gpt ? StorageMapper.GptBasicData : null,
                MbrType: disk.Style == PartitionStyle.Mbr ? 0x07 : null,
                IsBoot: false, IsSystem: false, IsActive: false, IsHidden: false,
                Volume: new Volume(null, "New Volume", "NTFS", request.TargetSizeBytes, request.TargetSizeBytes,
                    ReportedHealth.Healthy, EncryptionState.NotEncrypted));
            parts.Add(changed);
        }
        else
        {
            var p = request.Partition!;
            var overhead = PlanLimits.Overhead(p);
            var volume = p.Volume is { } v
                ? v with
                {
                    SizeBytes = request.TargetSizeBytes - overhead,
                    FreeBytes = Math.Max(0, request.TargetSizeBytes - overhead - v.UsedBytes),
                }
                : null;
            changed = p with { SizeBytes = request.TargetSizeBytes, Volume = volume };
            parts[parts.IndexOf(p)] = changed;
        }

        var proposed = DiskLayoutBuilder.Build(disk with { Partitions = parts.OrderBy(x => x.OffsetBytes).ToList() });
        return (proposed, proposed.Regions.FirstOrDefault(r => ReferenceEquals(r.Partition, changed)));
    }

    /// <summary>
    /// The part of the disk the plan affects: for a shrink, the space released at the end of the volume;
    /// for an extend, the space added after it; for a new volume, the new partition.
    /// </summary>
    private static (long Offset, long Length) ChangedRange(PlanRequest request, DiskRegion? proposed)
    {
        var target = request.TargetSizeBytes;
        return request.Kind switch
        {
            PlanKind.ShrinkVolume => (request.Partition!.OffsetBytes + target, request.Partition.SizeBytes - target),
            PlanKind.ExtendVolume => (request.Partition!.EndBytes, target - request.Partition.SizeBytes),
            _ => (proposed?.OffsetBytes ?? request.Region.OffsetBytes, target),
        };
    }

    private static string Summarize(DiskLayout current, PlanRequest request, DiskRegion? proposed)
    {
        var target = ByteSize.Format(request.TargetSizeBytes);
        switch (request.Kind)
        {
            case PlanKind.ShrinkVolume:
            {
                var p = request.Partition!;
                var freed = ByteSize.Format(p.SizeBytes - request.TargetSizeBytes);
                return $"Shrink {p.DisplayName} from {ByteSize.Format(p.SizeBytes)} to {target}. " +
                       $"{freed} would become unallocated space directly after it. " +
                       $"Your files would stay on {p.DisplayName}; Windows may move some of them within the volume.";
            }
            case PlanKind.ExtendVolume:
            {
                var p = request.Partition!;
                return $"Extend {p.DisplayName} from {ByteSize.Format(p.SizeBytes)} to {target}, " +
                       $"using {ByteSize.Format(request.TargetSizeBytes - p.SizeBytes)} of the unallocated space directly after it.";
            }
            default:
            {
                var remaining = request.Region.SizeBytes - request.TargetSizeBytes;
                return $"Create a new NTFS volume of {target} in the unallocated space " +
                       $"starting at {ByteSize.Format(proposed?.OffsetBytes ?? request.Region.OffsetBytes)}. " +
                       (remaining >= ByteSize.MiB ? $"About {ByteSize.Format(remaining)} would stay unallocated." : "It would use all of that space.");
            }
        }
    }

    private static IReadOnlyList<string> BuildRisks(PlanRequest request)
    {
        var risks = new List<string>
        {
            "No disk tool can guarantee that data is never lost. A power cut, a failing drive or a Windows error during a real change can cause data loss.",
            "Back up anything important first, and open a few files from the backup to make sure it works.",
        };
        switch (request.Kind)
        {
            case PlanKind.ShrinkVolume:
                risks.Add("Shrinking can make Windows move files inside the volume. It can take a long time and must not be interrupted.");
                break;
            case PlanKind.ExtendVolume:
                risks.Add("An extension can't always be reversed exactly later; how far a volume can shrink back depends on where Windows has put files.");
                break;
            case PlanKind.CreateVolume:
                risks.Add("Creating a volume formats only the unallocated space, but make sure the right disk is selected.");
                break;
        }
        if (request.Partition?.IsBoot == true)
            risks.Add("Windows runs from this volume, so a failure during a real change could stop the PC from starting.");
        return risks;
    }

    private static bool InsideExtendedContainer(DiskLayout layout, DiskRegion region) =>
        layout.Disk.Partitions.Any(c => c.Kind == PartitionKind.Extended &&
                                        region.OffsetBytes >= c.OffsetBytes && region.EndBytes <= c.EndBytes);

    private static void AddAssessment(List<PlanCheck> checks, ActionAssessment a)
    {
        if (a.PassesBasicChecks) checks.Add(Pass("The basic safety checks pass for this partition and disk."));
        foreach (var reason in a.Reasons) checks.Add(Fail(reason));
    }

    private static PlanCheck Pass(string text) => new(CheckOutcome.Passed, text);
    private static PlanCheck Warn(string text) => new(CheckOutcome.Warning, text);
    private static PlanCheck Fail(string text) => new(CheckOutcome.Failed, text);
}
