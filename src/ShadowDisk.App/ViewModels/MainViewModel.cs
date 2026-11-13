using System.Collections.ObjectModel;
using System.IO;
using System.Windows;
using Microsoft.Win32;
using ShadowDisk.App.Services;
using ShadowDisk.Core.Inventory;
using ShadowDisk.Core.Layout;
using ShadowDisk.Core.Logging;
using ShadowDisk.Core.Model;
using ShadowDisk.Core.Planning;
using ShadowDisk.Core.Reporting;
using ShadowDisk.Core.Safety;
using ShadowDisk.Windows;

namespace ShadowDisk.App.ViewModels;

public sealed class MainViewModel : ObservableObject
{
    public const string SourceLive = "This PC (live, read-only)";
    public const string SourceDemo = "Demo disks (simulated)";

    private readonly UserSettings _settings = UserSettings.Load();
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

        foreach (var e in AppServices.Log.Entries) LogEntries.Add(e);
        AppServices.Log.EntryAdded += e => Application.Current?.Dispatcher.InvokeAsync(() => LogEntries.Add(e));
        AppServices.Log.Info($"ShadowDisk {AppServices.Version} started ({(IsElevated ? "administrator" : "standard user")}). Read-only mode.");
    }

    public string ReadOnlyStatement => SafetyAnalyzer.ReadOnlyStatement;
    public string WindowTitle => $"ShadowDisk {AppServices.Version} — read-only disk inspector";
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
        Plan = new PlanViewModel(SelectedDisk.Layout, SelectedRow.Region, limits);
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
            foreach (var d in snapshot.Disks) Disks.Add(new DiskItemViewModel(d));
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
