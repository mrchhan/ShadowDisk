using System.Globalization;
using ShadowDisk.App.Services;
using ShadowDisk.Core;
using ShadowDisk.Core.Layout;
using ShadowDisk.Core.Model;
using ShadowDisk.Core.Operations;
using ShadowDisk.Core.Planning;
using ShadowDisk.Core.Safety;

namespace ShadowDisk.App.ViewModels;

public sealed record PlanOption(PlanKind Kind, string Label);

public sealed class PlanCheckViewModel(PlanCheck check)
{
    public string Text => check.Text;
    public string Symbol => check.Outcome switch
    {
        CheckOutcome.Passed => "✓",
        CheckOutcome.Warning => "!",
        _ => "✗",
    };
    public string Color => check.Outcome switch
    {
        CheckOutcome.Passed => "#047857",
        CheckOutcome.Warning => "#B45309",
        _ => "#B91C1C",
    };
}

/// <summary>
/// Planning panel for one selected partition or unallocated region. Everything here is a
/// simulation: changing the size or the slider only changes the preview.
/// </summary>
public sealed class PlanViewModel : ObservableObject
{
    private readonly DiskLayout _layout;
    private readonly DiskRegion _region;
    private readonly IPartitionLimitsProvider _limitsProvider;
    private readonly OperationPolicy _policy;
    private readonly bool _isElevated;
    private readonly Func<DiskLayout, PlanResult, Task>? _apply;
    private CancellationTokenSource? _limitsCts;

    private PlanOption? _selectedOption;
    private SizeLimits? _limits;
    private bool _isLoadingLimits;
    private long _targetBytes;
    private string _sizeText = "";
    private PlanResult? _result;

    public PlanViewModel(DiskLayout layout, DiskRegion region, IPartitionLimitsProvider limitsProvider,
        OperationPolicy policy, bool isElevated, bool isSimulated, Func<DiskLayout, PlanResult, Task>? apply)
    {
        _layout = layout;
        _region = region;
        _limitsProvider = limitsProvider;
        _policy = policy;
        _isElevated = isElevated;
        _isSimulated = isSimulated;
        _apply = apply;
        ApplyCommand = new RelayCommand(() => _ = ApplyAsync(), () => CanApply && !_isApplying);
        HandleDragCommand = new DelegateCommand<long>(DragTo, _ => _limits?.IsUsable == true);

        var options = new List<PlanOption>();
        if (region.Partition is { } p)
        {
            foreach (var a in SafetyAnalyzer.AssessPartition(layout, p).Where(a => a.PassesBasicChecks))
            {
                if (a.Action == PlannedAction.ShrinkVolume) options.Add(new PlanOption(PlanKind.ShrinkVolume, $"Shrink {p.DisplayName}"));
                if (a.Action == PlannedAction.ExtendVolume) options.Add(new PlanOption(PlanKind.ExtendVolume, $"Extend {p.DisplayName}"));
            }
        }
        else if (region.Kind == RegionKind.Unallocated && SafetyAnalyzer.AssessUnallocated(layout, region).PassesBasicChecks)
        {
            options.Add(new PlanOption(PlanKind.CreateVolume, "Create a new volume here"));
        }
        Options = options;

        UseMinimumCommand = new RelayCommand(() => { if (_limits is { } l) TargetBytes = l.MinBytes; }, () => _limits?.IsUsable == true);
        UseMaximumCommand = new RelayCommand(() => { if (_limits is { } l) TargetBytes = l.MaxBytes; }, () => _limits?.IsUsable == true);
    }

    public DiskLayout CurrentLayout => _layout;
    public IReadOnlyList<PlanOption> Options { get; }
    public bool CanPlan => Options.Count > 0;

    /// <summary>
    /// For a data volume that can't be extended: the reason in plain words, e.g.
    /// "Recovery sits directly after C:…". Empty when extending is possible or not relevant.
    /// </summary>
    public string ExtendBlockedText
    {
        get
        {
            if (_region.Partition is not { Volume: not null } p || p.Kind is not (PartitionKind.Basic or PartitionKind.Logical))
                return "";
            var extend = SafetyAnalyzer.AssessPartition(_layout, p).Single(a => a.Action == PlannedAction.ExtendVolume);
            return extend.PassesBasicChecks ? "" : $"Why can't {p.DisplayName} be extended? " + string.Join(" ", extend.Reasons);
        }
    }
    public string NothingToPlanText => _region.Partition is null
        ? "No change can be planned in this space. See the reasons under “What a future version could check here”."
        : $"No change can be planned for {_region.Partition.DisplayName}. See the reasons under “What a future version could check here”.";

    private readonly bool _isSimulated;
    private bool _isApplying;

    public RelayCommand ApplyCommand { get; }

    public string ApplyButtonText => Result?.Request.Kind switch
    {
        PlanKind.ShrinkVolume => $"Shrink {_region.Partition?.DisplayName} for real…",
        PlanKind.ExtendVolume => $"Extend {_region.Partition?.DisplayName} for real…",
        _ => "Create this volume for real…",
    };

    /// <summary>True only for a valid plan on a ShadowDisk practice disk, run as administrator.</summary>
    public bool CanApply => ApplyBlockedReason is null;
    public bool ShowApplyArea => Result?.IsValid == true;

    /// <summary>Why the real change can't be made from here, or null if it can.</summary>
    public string? ApplyBlockedReason
    {
        get
        {
            if (Result is not { IsValid: true } r) return "The plan must pass its checks first.";
            if (_isSimulated) return "These are simulated demo disks. Switch the source to “This PC” and use a practice disk.";
            var notAllowed = _policy.WhyNotAllowed(_layout.Disk);
            if (notAllowed.Count > 0) return notAllowed[0];
            if (!_isElevated) return "Real changes need administrator rights. Use “Restart as administrator”.";
            if (_apply is null) return "Applying is not available.";
            return null;
        }
    }

    /// <summary>Called while the preview's handle is dragged. Only changes the planned size.</summary>
    public DelegateCommand<long> HandleDragCommand { get; }

    /// <summary>Where the planned partition starts: the volume itself, or the aligned start of the free space.</summary>
    private long PlanStartOffset => SelectedOption?.Kind == PlanKind.CreateVolume
        ? PlanLimits.ForNewVolume(_layout, _region).Offset
        : _region.Partition?.OffsetBytes ?? _region.OffsetBytes;

    // The space the plan changes, outlined on both bars. For a shrink this is the end of the
    // volume being released; for an extend, the space added; for a new volume, the volume itself.
    public long HighlightStart => Result?.ChangedOffsetBytes ?? 0;
    public long HighlightLength => Result?.IsValid == true ? Result.ChangedLengthBytes : 0;

    /// <summary>The drag handle sits at the planned end of the volume (preview bar only).</summary>
    public long HandleOffset => Result is { IsValid: true, ProposedRegion: { } r } ? r.EndBytes : -1;

    public string ExplainChangeText => SelectedOption?.Kind switch
    {
        PlanKind.ShrinkVolume => "The dashed orange box is the free space that would be released from the end of the volume. " +
                                 "Your data (dark blue) stays on the volume; if any files sit in that area, Windows moves them first.",
        PlanKind.ExtendVolume => "The dashed orange box is the unallocated space that would be added to the volume. Existing data is not moved.",
        PlanKind.CreateVolume => "The dashed orange box is the new volume. Only unallocated space is used; no existing partition is touched.",
        _ => "",
    };

    public RelayCommand UseMinimumCommand { get; }
    public RelayCommand UseMaximumCommand { get; }

    public PlanOption? SelectedOption
    {
        get => _selectedOption;
        set
        {
            if (!Set(ref _selectedOption, value)) return;
            OnPropertyChanged(nameof(HasOption));
            _ = LoadLimitsAsync();
        }
    }

    public bool HasOption => SelectedOption is not null;

    public bool IsLoadingLimits
    {
        get => _isLoadingLimits;
        private set => Set(ref _isLoadingLimits, value);
    }

    public string LimitsText => _limits is null
        ? ""
        : _limits.IsUsable
            ? $"Allowed size: {ByteSize.Format(_limits.MinBytes)} to {ByteSize.Format(_limits.MaxBytes)}"
            : "There is no room for this change.";

    public string LimitsNote => _limits?.Note ?? "";

    // Slider works in whole MiB.
    public double SliderMinimum => _limits?.IsUsable == true ? _limits.MinBytes / (double)ByteSize.MiB : 0;
    public double SliderMaximum => _limits?.IsUsable == true ? _limits.MaxBytes / (double)ByteSize.MiB : 0;
    public bool HasUsableLimits => _limits?.IsUsable == true;

    public double SliderValue
    {
        get => _targetBytes / (double)ByteSize.MiB;
        set => TargetBytes = (long)Math.Round(value) * ByteSize.MiB;
    }

    /// <summary>Size in GiB as typed by the user. Rounded down to a whole MiB.</summary>
    public string SizeText
    {
        get => _sizeText;
        set
        {
            if (!Set(ref _sizeText, value)) return;
            var normalized = value.Trim().Replace(',', '.');
            if (double.TryParse(normalized, NumberStyles.Float, CultureInfo.InvariantCulture, out var gib) && gib >= 0 && gib < 1_000_000)
                SetTarget(PlanLimits.AlignDown((long)(gib * ByteSize.GiB)), updateText: false);
        }
    }

    public long TargetBytes
    {
        get => _targetBytes;
        set => SetTarget(value, updateText: true);
    }

    public PlanResult? Result
    {
        get => _result;
        private set
        {
            if (!Set(ref _result, value)) return;
            OnPropertyChanged(nameof(HasResult));
            OnPropertyChanged(nameof(Checks));
            OnPropertyChanged(nameof(Risks));
            OnPropertyChanged(nameof(Summary));
            OnPropertyChanged(nameof(ProposedLayout));
            OnPropertyChanged(nameof(ProposedRegion));
            OnPropertyChanged(nameof(HasPreview));
            OnPropertyChanged(nameof(VerdictText));
            OnPropertyChanged(nameof(VerdictColor));
            OnPropertyChanged(nameof(HighlightStart));
            OnPropertyChanged(nameof(HighlightLength));
            OnPropertyChanged(nameof(HandleOffset));
            OnPropertyChanged(nameof(ExplainChangeText));
            OnPropertyChanged(nameof(CanApply));
            OnPropertyChanged(nameof(ShowApplyArea));
            OnPropertyChanged(nameof(ApplyBlockedReason));
            OnPropertyChanged(nameof(ApplyButtonText));
        }
    }

    public bool HasResult => Result is not null;
    public bool HasPreview => Result?.ProposedLayout is not null;
    public DiskLayout? ProposedLayout => Result?.ProposedLayout;
    public DiskRegion? ProposedRegion => Result?.ProposedRegion;
    public string Summary => Result?.Summary ?? "";
    public IReadOnlyList<PlanCheckViewModel> Checks => Result?.Checks.Select(c => new PlanCheckViewModel(c)).ToList() ?? [];
    public IReadOnlyList<string> Risks => Result?.Risks ?? [];
    public string VerdictText => Result is null ? "" : Result.IsValid
        ? "This plan passes the checks. It is a preview only: nothing will be changed."
        : "This plan is blocked. Adjust the size or see the checks below.";
    public string VerdictColor => Result?.IsValid == true ? "#047857" : "#B91C1C";

    private void SetTarget(long bytes, bool updateText)
    {
        if (bytes < 0) bytes = 0;
        var changed = _targetBytes != bytes;
        _targetBytes = bytes;
        if (changed)
        {
            OnPropertyChanged(nameof(TargetBytes));
            OnPropertyChanged(nameof(SliderValue));
        }
        if (updateText)
        {
            // Truncate (never round up) so the box never shows more than the plan really uses.
            _sizeText = (Math.Floor(bytes / (double)ByteSize.GiB * 100) / 100).ToString("0.00", CultureInfo.InvariantCulture);
            OnPropertyChanged(nameof(SizeText));
        }
        Recalculate();
    }

    private async Task LoadLimitsAsync()
    {
        _limitsCts?.Cancel();
        var cts = _limitsCts = new CancellationTokenSource();
        _limits = null;
        Result = null;
        NotifyLimits();
        if (SelectedOption is not { } option) return;

        IsLoadingLimits = true;
        try
        {
            SizeLimits limits;
            if (option.Kind == PlanKind.CreateVolume)
                limits = PlanLimits.ForNewVolume(_layout, _region).Limits;
            else
                limits = await _limitsProvider.GetLimitsAsync(_layout, _region.Partition!, option.Kind, cts.Token);
            if (cts.IsCancellationRequested) return;

            _limits = limits;
            NotifyLimits();
            AppServices.Log.Info($"Planning preview: {option.Kind} limits {ByteSize.Format(limits.MinBytes)}–{ByteSize.Format(limits.MaxBytes)} ({limits.Source}).");

            // Start from a sensible suggestion: the middle of the allowed range.
            var start = limits.IsUsable
                ? PlanLimits.AlignDown(limits.MinBytes + (limits.MaxBytes - limits.MinBytes) / 2)
                : _region.Partition?.SizeBytes ?? 0;
            if (limits.IsUsable) start = Math.Clamp(start, limits.MinBytes, limits.MaxBytes);
            SetTarget(start, updateText: true);
        }
        catch (OperationCanceledException)
        {
            // A newer selection replaced this one.
        }
        catch (Exception ex)
        {
            AppServices.Log.Error($"Could not work out size limits: {ex.GetType().Name}: {ex.Message}");
            _limits = new SizeLimits(0, 0, LimitsSource.Estimated, "ShadowDisk could not work out the size limits for this change.");
            NotifyLimits();
        }
        finally
        {
            if (ReferenceEquals(_limitsCts, cts)) IsLoadingLimits = false;
        }
    }

    private void Recalculate()
    {
        if (SelectedOption is not { } option || _limits is null)
        {
            Result = null;
            return;
        }
        Result = PlanValidator.Validate(_layout, new PlanRequest(option.Kind, _region, _targetBytes), _limits);
    }

    private void NotifyLimits()
    {
        OnPropertyChanged(nameof(LimitsText));
        OnPropertyChanged(nameof(LimitsNote));
        OnPropertyChanged(nameof(SliderMinimum));
        OnPropertyChanged(nameof(SliderMaximum));
        OnPropertyChanged(nameof(HasUsableLimits));
    }

    public void Cancel() => _limitsCts?.Cancel();

    private void DragTo(long endOffset)
    {
        if (_limits is not { IsUsable: true } limits) return;
        var size = PlanLimits.AlignDown(endOffset - PlanStartOffset);
        TargetBytes = Math.Clamp(size, limits.MinBytes, limits.MaxBytes);
    }

    private async Task ApplyAsync()
    {
        if (!CanApply || Result is not { IsValid: true } plan || _apply is null) return;
        _isApplying = true;
        try
        {
            await _apply(_layout, plan);
        }
        finally
        {
            _isApplying = false;
        }
    }
}
