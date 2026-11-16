using System.Collections.ObjectModel;
using System.IO;
using System.Windows;
using Microsoft.Win32;
using ShadowDisk.App.Services;
using ShadowDisk.Core;
using ShadowDisk.Core.Copying;
using ShadowDisk.Core.Inventory;
using ShadowDisk.Core.Layout;
using ShadowDisk.Core.Logging;
using ShadowDisk.Core.Model;
using ShadowDisk.Core.Operations;
using ShadowDisk.Core.Planning;
using ShadowDisk.Core.Reporting;
using ShadowDisk.Core.Safety;
using ShadowDisk.Core.Recovery;
using ShadowDisk.Core.Wizard;
using ShadowDisk.Windows;

namespace ShadowDisk.App.ViewModels;

public sealed class MainViewModel : ObservableObject
{
    public const string SourceLive = "This PC (live, read-only)";
    public const string SourceDemo = "Demo disks (simulated)";

    private readonly UserSettings _settings = UserSettings.Load();
    private readonly OperationPolicy _policy = PracticeDiskService.Policy;
    private readonly PracticeDiskService _practice = new();
    private CancellationTokenSource? _refreshCts;
    private InventorySnapshot? _snapshot;
    private DiskItemViewModel? _selectedDisk;
    private RegionRowViewModel? _selectedRow;
    private DiskRegion? _selectedRegion;
    private string _source;
    private string _view;
    private bool _isBusy;
    private string _statusText = "Starting…";
    private string? _bannerText;
    private PlanViewModel? _plan;

    public MainViewModel()
    {
        _source = App.StartInDemoMode ? SourceDemo : SourceLive;
        _view = ViewOptions.Contains(_settings.View) ? _settings.View : "Bar";
        IsElevated = Elevation.IsElevated();

        RefreshCommand = new RelayCommand(() => _ = RefreshAsync(), () => !IsBusy);
        ExportReportCommand = new RelayCommand(ExportReport, () => _snapshot is not null && !IsBusy);
        OpenDiskManagementCommand = new RelayCommand(OpenDiskManagement);
        RestartElevatedCommand = new RelayCommand(RestartElevated, () => !IsElevated);
        OpenLogFolderCommand = new RelayCommand(OpenLogFolder);
        CreatePracticeDiskCommand = new RelayCommand(() => _ = CreatePracticeDiskAsync(), () => !IsBusy);
        ToggleRealDiskChangesCommand = new RelayCommand(ToggleRealDiskChanges, () => !IsBusy);
        OpenCopyVerifyCommand = new RelayCommand(OpenCopyVerify, () => !IsBusy && _snapshot is { IsSimulated: false });
        DeleteVolumeCommand = new RelayCommand(() => _ = DeleteVolumeAsync(), () => !IsBusy && HasVolumeActions);
        ChangeLetterCommand = new RelayCommand(() => _ = ChangeLetterAsync(), () => !IsBusy && HasVolumeActions);
        GiveSpaceCommand = new RelayCommand(() => _ = GiveSpaceAsync(), () => !IsBusy && HasVolumeActions);
        RemovePracticeDisksCommand = new RelayCommand(() => _ = RemovePracticeDisksAsync(), () => !IsBusy);

        foreach (var e in AppServices.Log.Entries) LogEntries.Add(e);
        AppServices.Log.EntryAdded += e => Application.Current?.Dispatcher.InvokeAsync(() => LogEntries.Add(e));
        AppServices.Log.Info($"ShadowDisk {AppServices.Version} started ({(IsElevated ? "administrator" : "standard user")}). Read-only mode.");
    }

    public string ReadOnlyStatement => SafetyAnalyzer.ReadOnlyStatement;
    public string WindowTitle => $"ShadowDisk {AppServices.Version} — disk & partition manager";
    public bool IsElevated { get; }

    public IReadOnlyList<string> SourceOptions { get; } = [SourceLive, SourceDemo];
    public IReadOnlyList<string> ViewOptions { get; } = ["Bar", "Pie", "Table only"];

    public ObservableCollection<DiskItemViewModel> Disks { get; } = [];
    public ObservableCollection<LogEntry> LogEntries { get; } = [];

    public RelayCommand RefreshCommand { get; }
    public RelayCommand ExportReportCommand { get; }
    public RelayCommand OpenDiskManagementCommand { get; }
    public RelayCommand RestartElevatedCommand { get; }
    public RelayCommand OpenLogFolderCommand { get; }
    public RelayCommand CreatePracticeDiskCommand { get; }
    public RelayCommand ToggleRealDiskChangesCommand { get; }
    public RelayCommand OpenCopyVerifyCommand { get; }
    public RelayCommand DeleteVolumeCommand { get; }
    public RelayCommand ChangeLetterCommand { get; }
    public RelayCommand GiveSpaceCommand { get; }

    /// <summary>
    /// Stage 5 parts 3 and 4: give space to the selected volume from what follows it — the next volume,
    /// and/or a Recovery partition (which is moved with Windows Recovery turned off and back on).
    /// </summary>
    private async Task GiveSpaceAsync()
    {
        if (SelectedDisk is not { } item || SelectedRow?.Region.Partition is not { Volume: not null } target) return;
        void Info(string text) => MessageBox.Show(text, "ShadowDisk — give space", MessageBoxButton.OK, MessageBoxImage.Information);
        if (!IsElevated) { Info("This needs administrator rights. Click “Restart as administrator” first."); return; }

        var layout = item.Layout;
        var after = layout.Regions.Where(r => r.Kind != RegionKind.Gap)
            .SkipWhile(r => !ReferenceEquals(r.Partition, target)).Skip(1).Take(2).Select(r => r.Partition).ToList();
        var recoveryPart = after.FirstOrDefault() is { Kind: PartitionKind.Recovery } rp ? rp : null;
        var donor = (recoveryPart is null ? after.FirstOrDefault() : after.Skip(1).FirstOrDefault()) is { Volume: not null } dp ? dp : null;

        IRecoveryEnvironment? recovery = null;
        RecoveryStatus? recoveryStatus = null;
        SizeLimits? limits = null;
        IsBusy = true;
        try
        {
            if (recoveryPart is not null)
            {
                StatusText = "Asking Windows where Windows Recovery is (reagentc /info)…";
                recovery = _policy.IsPracticeDisk(item.Disk)
                    ? new SimulatedRecoveryEnvironment(new WindowsStorageInventory(), item.Disk.Number, item.Disk.UniqueId)
                    : new ReAgentRecoveryEnvironment();
                try
                {
                    recoveryStatus = await recovery.GetStatusAsync(CancellationToken.None);
                    AppServices.Log.Info("Windows Recovery: " + recoveryStatus.Details);
                }
                catch (Exception ex)
                {
                    AppServices.Log.Warning("Could not read Windows Recovery status: " + ex.Message);
                }
            }
            if (donor is not null)
            {
                StatusText = "Asking Windows how far the next volume can shrink…";
                limits = await new WindowsPartitionLimitsProvider().GetLimitsAsync(layout, donor, PlanKind.ShrinkVolume, CancellationToken.None);
            }
        }
        finally { IsBusy = false; StatusText = ""; }

        var plan = GiveSpacePlanner.Plan(layout, target, limits, recoveryStatus);
        if (!plan.IsPossible) { Info(string.Join("\n\n", plan.Problems)); return; }
        var power = PowerStatus.IsOnMainsPower();
        var reasons = _policy.WhyNotAllowed(item.Disk, target, power, extend: true).ToList();
        if (plan.Donor is not null) reasons.AddRange(_policy.WhyNotAllowed(item.Disk, plan.Donor, power));
        if (plan.Recovery is not null) reasons.AddRange(_policy.WhyNotAllowed(item.Disk, plan.Recovery, power));
        reasons = reasons.Distinct().ToList();
        if (reasons.Count > 0) { Info(string.Join("\n\n", reasons)); return; }

        var window = new GiveSpaceWindow(plan, give => new GiveSpaceWizard(plan, give, new WindowsStorageInventory(), NewRunner(),
            new WindowsPartitionLimitsProvider(), l => $@"{l}:\",
            (s, d, resume, p, ct) => new VerifiedCopy(AppServices.Log).CopyAndVerifyAsync(s, d, p, ct, resume), AppServices.Log, recovery))
        { Owner = Application.Current.MainWindow };
        window.ShowDialog();
        if (window.ChangedDisk) await RefreshAsync();
    }

    /// <summary>Delete / change letter apply to a real (not demo) data partition with a volume.</summary>
    public bool HasVolumeActions => !IsSimulated && SelectedRow?.Region.Partition is { Volume: not null };

    private static void ShowOutcome(OperationOutcome outcome)
    {
        var title = outcome.Status switch
        {
            OperationStatus.Succeeded => "Change applied",
            OperationStatus.PartiallyApplied => "Change only partly applied",
            OperationStatus.Failed => "Change failed",
            OperationStatus.Cancelled => "Change cancelled",
            _ => "Change not started",
        };
        MessageBox.Show(outcome.Message + "\n\nWhat happened:\n• " + string.Join("\n• ", outcome.Steps), "ShadowDisk — " + title,
            MessageBoxButton.OK, outcome.Status == OperationStatus.Succeeded ? MessageBoxImage.Information : MessageBoxImage.Warning);
    }

    private OperationRunner NewRunner() => new(new WindowsStorageInventory(), new WindowsDiskOperations(_policy), _policy,
        AppServices.Log, new WindowsPartitionLimitsProvider(), PowerStatus.IsOnMainsPower);

    private static string? CheckFiles(Partition p) =>
        p.Volume?.DriveLetter is { Length: > 0 } l
            ? DeleteSafety.WhyNotSafeToDelete($@"{l}:\", VerifiedCopyRegistry.Find($@"{l}:\"))
            : "The volume has no drive letter, so ShadowDisk can't check its files. Give it a letter first.";

    private async Task DeleteVolumeAsync()
    {
        if (SelectedDisk?.Disk is not { } disk || SelectedRow?.Region.Partition is not { Volume: { } volume } p) return;
        void Info(string text) => MessageBox.Show(text, "ShadowDisk — delete volume", MessageBoxButton.OK, MessageBoxImage.Information);

        if (!IsElevated) { Info("Deleting a volume needs administrator rights. Click “Restart as administrator” first."); return; }
        if (OperationRunner.WhyKindNotDeletable(p) is { } kind) { Info(kind); return; }
        var policyReasons = _policy.WhyNotAllowed(disk, p, PowerStatus.IsOnMainsPower());
        if (policyReasons.Count > 0) { Info(string.Join("\n\n", policyReasons)); return; }

        IsBusy = true;
        StatusText = $"Checking the files on {p.DisplayName}…";
        string? unsafeReason;
        try { unsafeReason = await Task.Run(() => CheckFiles(p)); }
        finally { IsBusy = false; StatusText = ""; }
        if (unsafeReason is not null) { Info(unsafeReason); return; }

        var letter = volume.DriveLetter!;
        var copy = VerifiedCopyRegistry.Find($@"{letter}:\");
        var facts = new List<string>
        {
            $"Disk {disk.Number} — {disk.FriendlyName}",
            $"Volume: {p.DisplayName}{(volume.Label is { } lb ? $" ({lb})" : "")}, {ByteSize.FormatWithExact(p.SizeBytes)}",
            copy is not null
                ? $"Its files ({copy.Files:N0} files, {ByteSize.Format(copy.Bytes)}) have a verified copy in: {copy.DestinationFolder}"
                : "The volume holds no files of yours.",
            "After deleting, its space becomes unallocated. This cannot be undone.",
        };
        var confirmText = $"DELETE {letter}";
        var dialog = new ConfirmDeleteWindow($"This permanently deletes {letter}: and everything on it.", facts, confirmText,
            copy is not null ? $"I have checked the verified copy in {copy.DestinationFolder} and can open my files there."
                             : $"I understand {letter}: will be removed.")
        { Owner = Application.Current.MainWindow };
        if (dialog.ShowDialog() != true) { AppServices.Log.Info("Delete: cancelled at the confirmation screen. Nothing was changed."); return; }

        IsBusy = true;
        StatusText = $"Deleting {letter}:…";
        OperationOutcome outcome;
        try
        {
            outcome = await NewRunner().DeleteAsync(
                new DeleteOrder(disk.Number, disk.UniqueId, LayoutFingerprint.Of(disk), p.PartitionNumber, p.OffsetBytes, p.SizeBytes),
                CheckFiles, CancellationToken.None);
        }
        finally { IsBusy = false; }
        ShowOutcome(outcome);
        await RefreshAsync();
    }

    private async Task ChangeLetterAsync()
    {
        if (SelectedDisk?.Disk is not { } disk || SelectedRow?.Region.Partition is not { Volume: { } volume } p) return;
        if (!IsElevated)
        {
            MessageBox.Show("Changing a drive letter needs administrator rights. Click “Restart as administrator” first.", "ShadowDisk",
                MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        var dialog = new ChooseLetterWindow(p.DisplayName, volume.DriveLetter) { Owner = Application.Current.MainWindow };
        if (dialog.ShowDialog() != true || dialog.ChosenLetter is not { } letter) return;

        IsBusy = true;
        OperationOutcome outcome;
        try
        {
            outcome = await NewRunner().ChangeDriveLetterAsync(
                new DriveLetterOrder(disk.Number, disk.UniqueId, p.PartitionNumber, p.OffsetBytes, volume.DriveLetter, letter), CancellationToken.None);
        }
        finally { IsBusy = false; }
        ShowOutcome(outcome);
        await RefreshAsync();
    }

    private void OpenCopyVerify()
    {
        if (_snapshot is null) return;
        var drives = _snapshot.Disks
            .SelectMany(d => d.Partitions)
            .Where(p => p.Volume is { DriveLetter: { Length: > 0 } })
            .Select(p => new CopyDrive(p.Volume!.DriveLetter!, p.Volume.Label, p.Volume.FreeBytes, p.Volume.UsedBytes))
            .OrderBy(d => d.Letter, StringComparer.Ordinal)
            .ToList();
        new CopyVerifyWindow(drives) { Owner = Application.Current.MainWindow }.ShowDialog();
    }

    /// <summary>Session-only: every start of ShadowDisk begins with real-disk changes switched off.</summary>
    public bool RealDiskChangesEnabled => _policy.AllowRealDisks;
    public string RealDiskButtonText => RealDiskChangesEnabled ? "Real disk changes: ON" : "Real disk changes: OFF";

    private void ToggleRealDiskChanges()
    {
        if (_policy.AllowRealDisks)
        {
            _policy.AllowRealDisks = false;
            AppServices.Log.Info("Real disk changes switched OFF.");
        }
        else
        {
            if (!IsElevated)
            {
                MessageBox.Show("Changing a real disk needs administrator rights. Click “Restart as administrator” first.", "ShadowDisk",
                    MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }
            var answer = MessageBox.Show(
                "Switch on changes to REAL disks for this session?\n\n" +
                "• No disk tool can guarantee your data is safe. A power cut, a failing drive or a Windows error during a change can cause data loss.\n" +
                "• Back up everything on the volume you will change, somewhere else, and check that the backup opens.\n" +
                "• Only data volumes can be changed. The Windows drive (C:), its start-up partition, EFI, MSR and Recovery partitions stay protected.\n" +
                "• Laptops must be plugged in.\n\n" +
                "This switch turns itself off when ShadowDisk closes.",
                "ShadowDisk — real disk changes", MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No);
            if (answer != MessageBoxResult.Yes) return;
            _policy.AllowRealDisks = true;
            AppServices.Log.Warning("Real disk changes switched ON for this session.");
        }
        OnPropertyChanged(nameof(RealDiskChangesEnabled));
        OnPropertyChanged(nameof(RealDiskButtonText));
        UpdatePlan();
    }
    public RelayCommand RemovePracticeDisksCommand { get; }

    public bool IsSimulated => _snapshot?.IsSimulated ?? Source == SourceDemo;

    public string Source
    {
        get => _source;
        set
        {
            if (Set(ref _source, value))
            {
                OnPropertyChanged(nameof(IsSimulated));
                _ = RefreshAsync();
            }
        }
    }

    public string View
    {
        get => _view;
        set
        {
            if (!Set(ref _view, value)) return;
            _settings.View = value;
            _settings.Save();
            OnPropertyChanged(nameof(ShowBar));
            OnPropertyChanged(nameof(ShowPie));
        }
    }

    public bool ShowBar => View == "Bar";
    public bool ShowPie => View == "Pie";

    public bool IsBusy
    {
        get => _isBusy;
        private set => Set(ref _isBusy, value);
    }

    public string StatusText
    {
        get => _statusText;
        private set => Set(ref _statusText, value);
    }

    public string? BannerText
    {
        get => _bannerText;
        private set
        {
            if (Set(ref _bannerText, value)) OnPropertyChanged(nameof(HasBanner));
        }
    }

    public bool HasBanner => !string.IsNullOrEmpty(BannerText);

    public DiskItemViewModel? SelectedDisk
    {
        get => _selectedDisk;
        set
        {
            if (!Set(ref _selectedDisk, value)) return;
            OnPropertyChanged(nameof(HasDisk));
            SelectedRow = value?.Rows.FirstOrDefault(r => r.Region.Partition?.Volume?.DriveLetter is not null)
                          ?? value?.Rows.FirstOrDefault();
        }
    }

    public bool HasDisk => SelectedDisk is not null;

    /// <summary>Row selected in the table. Kept in sync with the region selected in the bar or pie.</summary>
    public RegionRowViewModel? SelectedRow
    {
        get => _selectedRow;
        set
        {
            if (!Set(ref _selectedRow, value)) return;
            if (!Equals(_selectedRegion, value?.Region))
            {
                _selectedRegion = value?.Region;
                OnPropertyChanged(nameof(SelectedRegion));
            }
            UpdatePlan();
            OnPropertyChanged(nameof(HasVolumeActions));
        }
    }

    public DiskRegion? SelectedRegion
    {
        get => _selectedRegion;
        set
        {
            if (!Set(ref _selectedRegion, value)) return;
            var row = SelectedDisk?.Rows.FirstOrDefault(r => ReferenceEquals(r.Region, value));
            if (!ReferenceEquals(row, _selectedRow))
            {
                _selectedRow = row;
                OnPropertyChanged(nameof(SelectedRow));
                UpdatePlan();
                OnPropertyChanged(nameof(HasVolumeActions));
            }
        }
    }

    /// <summary>Planning panel for the selected partition or unallocated space. Preview only.</summary>
    public PlanViewModel? Plan
    {
        get => _plan;
        private set
        {
            _plan?.Cancel();
            if (Set(ref _plan, value)) OnPropertyChanged(nameof(HasPlanPanel));
        }
    }

    public bool HasPlanPanel => Plan is not null;

    private void UpdatePlan()
    {
        if (SelectedDisk is null || SelectedRow is null)
        {
            Plan = null;
            return;
        }
        IPartitionLimitsProvider limits = IsSimulated ? new EstimatedLimitsProvider() : new WindowsPartitionLimitsProvider();
        Plan = new PlanViewModel(SelectedDisk.Layout, SelectedRow.Region, limits, _policy, IsElevated, IsSimulated,
            IsSimulated ? null : new Func<DiskLayout, PlanResult, Task>(ApplyPlanAsync));
    }

    public async Task RefreshAsync()
    {
        _refreshCts?.Cancel();
        var cts = _refreshCts = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        var previousDisk = SelectedDisk?.Disk.Number;
        IsBusy = true;
        StatusText = "Reading disk information…";
        IStorageInventory inventory = Source == SourceDemo ? FixtureInventory.DemoAll() : new WindowsStorageInventory();

        try
        {
            var snapshot = await inventory.GetSnapshotAsync(cts.Token);
            if (cts.IsCancellationRequested) return;
            _snapshot = snapshot;
            OnPropertyChanged(nameof(IsSimulated));

            Disks.Clear();
            foreach (var d in snapshot.Disks) Disks.Add(new DiskItemViewModel(d, !snapshot.IsSimulated && _policy.IsPracticeDisk(d)));
            SelectedDisk = Disks.FirstOrDefault(d => d.Disk.Number == previousDisk) ?? Disks.FirstOrDefault();

            var warnings = snapshot.Warnings.ToList();
            if (snapshot.IsSimulated)
                warnings.Insert(0, "DEMO MODE: these are simulated disks, not the disks in this PC.");
            BannerText = warnings.Count == 0 ? null : string.Join("  ", warnings);
            StatusText = $"{snapshot.Disks.Count} disk(s) read at {snapshot.TakenAt:HH:mm:ss} from {snapshot.SourceDescription}.";
            AppServices.Log.Info($"Inventory refreshed from {snapshot.SourceDescription}: {snapshot.Disks.Count} disk(s).");
            foreach (var w in snapshot.Warnings) AppServices.Log.Warning(w);
        }
        catch (OperationCanceledException)
        {
            StatusText = "Reading disk information was cancelled or timed out.";
            AppServices.Log.Warning("Inventory refresh cancelled or timed out after 60 seconds.");
        }
        catch (Exception ex)
        {
            // Reading failed: show nothing rather than stale or partial data.
            _snapshot = null;
            Disks.Clear();
            SelectedDisk = null;
            BannerText = "Could not read disk information from Windows: " + ex.Message +
                         "  You can switch the source to “Demo disks (simulated)” to explore the app.";
            StatusText = "Disk information unavailable.";
            AppServices.Log.Error($"Inventory refresh failed: {ex.GetType().Name}: {ex.Message}");
        }
        finally
        {
            if (ReferenceEquals(_refreshCts, cts)) IsBusy = false;
        }
    }

    // ---------------- Stage 3: real operations (practice disks only) ----------------

    private async Task ApplyPlanAsync(DiskLayout layout, PlanResult plan)
    {
        var disk = layout.Disk;
        if (SelectedDisk?.Disk.Number != disk.Number) return;
        var kindText = plan.Request.Kind switch
        {
            PlanKind.ShrinkVolume => "Shrink",
            PlanKind.ExtendVolume => "Extend",
            _ => "Create volume",
        };

        var isPractice = _policy.IsPracticeDisk(disk);
        var dialog = new ConfirmChangeWindow(disk, plan, isPractice) { Owner = Application.Current.MainWindow };
        if (dialog.ShowDialog() != true)
        {
            AppServices.Log.Info($"{kindText}: cancelled at the confirmation screen. Nothing was changed.");
            return;
        }

        IsBusy = true;
        StatusText = "Applying the change. Don't close ShadowDisk or turn off the PC…";
        OperationOutcome outcome;
        try
        {
            var runner = new OperationRunner(new WindowsStorageInventory(), new WindowsDiskOperations(_policy), _policy,
                AppServices.Log, new WindowsPartitionLimitsProvider(), PowerStatus.IsOnMainsPower);
            var fingerprint = LayoutFingerprint.Of(disk);
            if (plan.Request.Kind == PlanKind.CreateVolume && plan.ProposedRegion?.Partition is { } created)
            {
                outcome = await runner.CreateVolumeAsync(new CreateVolumeOrder(disk.Number, disk.UniqueId, fingerprint,
                    created.OffsetBytes, created.SizeBytes, isPractice ? "Practice" : "New Volume"), CancellationToken.None);
            }
            else if (plan.Request.Partition is { } p)
            {
                outcome = await runner.ResizeAsync(new ResizeOrder(disk.Number, disk.UniqueId, fingerprint,
                    p.PartitionNumber, p.OffsetBytes, p.SizeBytes, plan.Request.TargetSizeBytes), CancellationToken.None);
            }
            else
            {
                return;
            }
        }
        finally
        {
            IsBusy = false;
        }

        var title = outcome.Status switch
        {
            OperationStatus.Succeeded => "Change applied",
            OperationStatus.PartiallyApplied => "Change only partly applied",
            OperationStatus.Failed => "Change failed",
            OperationStatus.Cancelled => "Change cancelled",
            _ => "Change not started",
        };
        var icon = outcome.Status == OperationStatus.Succeeded ? MessageBoxImage.Information : MessageBoxImage.Warning;
        MessageBox.Show(outcome.Message + "\n\nWhat happened:\n• " + string.Join("\n• ", outcome.Steps), "ShadowDisk — " + title, MessageBoxButton.OK, icon);
        await RefreshAsync();
    }

    private async Task CreatePracticeDiskAsync()
    {
        if (!IsElevated)
        {
            MessageBox.Show("Creating a practice disk needs administrator rights. Click “Restart as administrator” first.", "ShadowDisk",
                MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        var answer = MessageBox.Show(
            "ShadowDisk will create a practice disk: a virtual disk file in\n" + PracticeDiskService.Folder +
            "\n\nWhich kind?\n\n" +
            "• Yes — an empty 1 GiB practice disk (GPT).\n\n" +
            "• No — a practice disk shaped like a Windows PC disk: MBR with four partitions, “Practice System”, " +
            "“Practice Windows” (a stand-in for C:), a Recovery partition and “Practice data” (about 6 GiB; the file only grows as it is used). " +
            "Use it to try “Give space from the next volume…” on the Practice Windows drive. Windows Recovery on it is simulated.\n\n" +
            "• Cancel — don't create anything.\n\nYour real disks are not touched. Remove practice disks any time with “Remove practice disks”.",
            "ShadowDisk — new practice disk", MessageBoxButton.YesNoCancel, MessageBoxImage.Question, MessageBoxResult.Cancel);
        if (answer == MessageBoxResult.Cancel) return;

        IsBusy = true;
        try
        {
            var progress = new Progress<string>(s => StatusText = s);
            var path = answer == MessageBoxResult.Yes
                ? await _practice.CreateAsync(1024, progress)
                : await _practice.CreateWindowsLikeAsync(progress);
            AppServices.Log.Info($"Practice disk created: {System.IO.Path.GetFileName(path)}.");
            if (Source != SourceLive) Source = SourceLive; // also refreshes
        }
        catch (Exception ex)
        {
            AppServices.Log.Error($"Practice disk could not be created: {ex.Message}");
            MessageBox.Show("The practice disk could not be created:\n" + ex.Message, "ShadowDisk", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        finally
        {
            IsBusy = false;
        }
        await RefreshAsync();
        SelectedDisk = Disks.LastOrDefault(d => d.IsPractice) ?? SelectedDisk;
    }

    private async Task RemovePracticeDisksAsync()
    {
        var files = _practice.ListFiles();
        if (files.Count == 0)
        {
            MessageBox.Show("There are no practice disks to remove.", "ShadowDisk", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        if (!IsElevated)
        {
            MessageBox.Show("Removing practice disks needs administrator rights. Click “Restart as administrator” first.", "ShadowDisk",
                MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        var answer = MessageBox.Show(
            $"Remove {files.Count} practice disk(s)? Their files are deleted, together with anything stored on them. Your real disks are not touched.",
            "ShadowDisk — remove practice disks", MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.No);
        if (answer != MessageBoxResult.Yes) return;

        IsBusy = true;
        try
        {
            foreach (var f in files)
            {
                StatusText = "Removing " + System.IO.Path.GetFileName(f) + "…";
                await _practice.RemoveAsync(f);
                AppServices.Log.Info($"Practice disk removed: {System.IO.Path.GetFileName(f)}.");
            }
        }
        catch (Exception ex)
        {
            AppServices.Log.Error($"Removing a practice disk failed: {ex.Message}");
            MessageBox.Show("A practice disk could not be removed:\n" + ex.Message, "ShadowDisk", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        finally
        {
            IsBusy = false;
        }
        await RefreshAsync();
    }

    private void ExportReport()
    {
        if (_snapshot is null) return;
        var dialog = new SaveFileDialog
        {
            Title = "Save disk report",
            FileName = $"shadowdisk-report-{DateTime.Now:yyyyMMdd-HHmm}.txt",
            Filter = "Text file (*.txt)|*.txt",
            InitialDirectory = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
        };
        if (dialog.ShowDialog() != true) return;
        try
        {
            File.WriteAllText(dialog.FileName, ReportWriter.Write(_snapshot, AppServices.Version, Plan?.Result));
            AppServices.Log.Info("Report exported.");
            StatusText = "Report saved.";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            AppServices.Log.Error($"Report export failed: {ex.Message}");
            MessageBox.Show("The report could not be saved: " + ex.Message, "ShadowDisk", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private static void OpenDiskManagement()
    {
        try
        {
            Elevation.OpenDiskManagement();
            AppServices.Log.Info("Opened Windows Disk Management.");
        }
        catch (Exception ex)
        {
            AppServices.Log.Error($"Could not open Disk Management: {ex.Message}");
        }
    }

    private void RestartElevated()
    {
        var args = Source == SourceDemo ? "--demo" : "";
        if (Elevation.TryRestartElevated(args))
        {
            AppServices.Log.Info("Restarting as administrator.");
            Application.Current.Shutdown();
        }
        else
        {
            AppServices.Log.Info("Administrator restart was cancelled.");
        }
    }

    private static void OpenLogFolder()
    {
        var dir = Path.Combine(AppServices.DataDirectory, "logs");
        Directory.CreateDirectory(dir);
        System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(dir) { UseShellExecute = true });
    }
}
