using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using Microsoft.Win32;
using ShadowDisk.App.Services;
using ShadowDisk.Core;
using ShadowDisk.Core.Health;
using ShadowDisk.Core.Model;
using ShadowDisk.Windows;

namespace ShadowDisk.App;

/// <summary>
/// Stage 4: disk health. Top: what the drive reports about itself (read-only). Bottom: an optional,
/// read-only surface scan that reads the whole disk once, can be stopped any time and continued later.
/// </summary>
public sealed class HealthWindow : Window
{
    private readonly PhysicalDisk _disk;
    private readonly TextBlock _verdict = new() { FontSize = 15, FontWeight = FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap };
    private readonly StackPanel _findings = new() { Margin = new Thickness(0, 4, 0, 0) };
    private readonly Grid _counters = new() { Margin = new Thickness(0, 8, 0, 0) };
    private readonly UniformGrid _map = new() { Rows = 4, Columns = 100, Height = 44, Margin = new Thickness(0, 8, 0, 4) };
    private readonly ProgressBar _bar = new() { Height = 14, Minimum = 0, Maximum = 1 };
    private readonly TextBlock _scanStatus = new() { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 4, 0, 4) };
    private readonly TextBox _problems = new()
    {
        IsReadOnly = true, Height = 110, FontFamily = new FontFamily("Consolas"), FontSize = 12,
        VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
    };
    private readonly Button _start = Btn("Start scan…");
    private readonly Button _stop = Btn("Stop");
    private readonly Button _save = Btn("Save results…");
    private readonly ScanMap _scanMap;
    private readonly List<ScanProblem> _found = [];
    private readonly Border[] _cells;
    private CancellationTokenSource? _cts;
    private ScanResult? _last;
    private long _resumeFrom;
    private bool _scanning;

    private static Brush Muted => Theme.Muted;
    private static Button Btn(string text) => new() { Content = text, MinWidth = 110, Padding = new Thickness(12, 5, 12, 5), Margin = new Thickness(0, 0, 8, 0) };

    public HealthWindow(PhysicalDisk disk)
    {
        _disk = disk;
        _scanMap = new ScanMap(disk.SizeBytes, _map.Rows * _map.Columns);
        Title = $"ShadowDisk — health of Disk {disk.Number}";
        Width = 720;
        SizeToContent = SizeToContent.Height;
        ResizeMode = ResizeMode.NoResize;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        FontFamily = new FontFamily("Segoe UI");
        Theme.Apply(this);
        Icon = Application.Current.MainWindow?.Icon;
        FontSize = 13;

        var panel = new StackPanel { Margin = new Thickness(20) };
        var title = new StackPanel { Orientation = Orientation.Horizontal };
        title.Children.Add(new Controls.SpinningDisk { Width = 34, Height = 34, FollowsActivity = true, Margin = new Thickness(0, 0, 12, 0) });
        title.Children.Add(new TextBlock
        {
            Text = $"Disk {disk.Number} — {disk.FriendlyName} · {ByteSize.Format(disk.SizeBytes)}", FontSize = 16, FontWeight = FontWeights.SemiBold,
            VerticalAlignment = VerticalAlignment.Center,
        });
        panel.Children.Add(title);

        panel.Children.Add(Heading("What the drive reports"));
        _verdict.Text = "Reading…";
        panel.Children.Add(_verdict);
        panel.Children.Add(_findings);
        _counters.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(200) });
        _counters.ColumnDefinitions.Add(new ColumnDefinition());
        panel.Children.Add(_counters);

        panel.Children.Add(Heading("Surface scan (read-only)"));
        panel.Children.Add(new TextBlock
        {
            TextWrapping = TextWrapping.Wrap, Foreground = Muted,
            Text = "Reads every part of the disk once and times each read. Nothing is written, and an area that can't be read " +
                   "is not read again. A full scan of a large hard disk takes hours; you can stop it any time and continue later " +
                   "while this window is open. Scanning works the drive hard: if the drive is already failing, back up its files first.",
        });
        _cells = new Border[_map.Rows * _map.Columns];
        for (var i = 0; i < _cells.Length; i++)
        {
            _cells[i] = new Border { Background = CellBrush(BlockState.NotScanned), Margin = new Thickness(0.5) };
            _map.Children.Add(_cells[i]);
        }
        panel.Children.Add(_map);
        panel.Children.Add(Legend());
        panel.Children.Add(_bar);
        panel.Children.Add(_scanStatus);
        panel.Children.Add(_problems);

        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 10, 0, 0) };
        var close = Btn("Close");
        close.IsCancel = true;
        close.Margin = new Thickness(0);
        _stop.IsEnabled = false;
        _save.IsEnabled = false;
        buttons.Children.Add(_start);
        buttons.Children.Add(_stop);
        buttons.Children.Add(_save);
        buttons.Children.Add(close);
        panel.Children.Add(buttons);
        Content = new ScrollViewer { Content = panel, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, MaxHeight = SystemParameters.WorkArea.Height - 40 };

        _start.Click += async (_, _) => await StartScanAsync();
        _stop.Click += (_, _) => _cts?.Cancel();
        _save.Click += (_, _) => SaveResults();
        close.Click += (_, _) => Close();
        Closing += (_, e) =>
        {
            if (!_scanning) return;
            if (MessageBox.Show(this, "The scan is still running. Stop it and close?", "ShadowDisk", MessageBoxButton.YesNo, MessageBoxImage.Question)
                != MessageBoxResult.Yes) { e.Cancel = true; return; }
            _cts?.Cancel();
        };
        Loaded += async (_, _) => await LoadReportAsync();
        _scanStatus.Text = "Not scanned yet.";
    }

    private static TextBlock Heading(string text) =>
        new() { Text = text, FontWeight = FontWeights.SemiBold, FontSize = 14, Margin = new Thickness(0, 16, 0, 4) };

    private static Brush CellBrush(BlockState s) => s switch
    {
        BlockState.Good => new SolidColorBrush(Color.FromRgb(0x34, 0xA8, 0x53)),
        BlockState.Slow => new SolidColorBrush(Color.FromRgb(0xF5, 0x9E, 0x0B)),
        BlockState.Unreadable => new SolidColorBrush(Color.FromRgb(0xDC, 0x26, 0x26)),
        _ => Theme.Get("PanelAltBrush"),
    };

    private static StackPanel Legend()
    {
        var legend = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 6) };
        foreach (var (state, text) in new[] { (BlockState.Good, "Read fine"), (BlockState.Slow, "Slow to read"), (BlockState.Unreadable, "Unreadable"), (BlockState.NotScanned, "Not scanned") })
        {
            legend.Children.Add(new Border { Width = 12, Height = 12, Background = CellBrush(state), Margin = new Thickness(0, 0, 4, 0), VerticalAlignment = VerticalAlignment.Center });
            legend.Children.Add(new TextBlock { Text = text, Margin = new Thickness(0, 0, 14, 0), FontSize = 12 });
        }
        return legend;
    }

    private async Task LoadReportAsync()
    {
        DriveHealthReport report;
        try
        {
            report = await WindowsHealthReader.ReadAsync(_disk);
        }
        catch (Exception ex)
        {
            AppServices.Log.Warning($"Disk {_disk.Number} health could not be read: {ex.Message}");
            report = new DriveHealthReport(_disk.Number, _disk.FriendlyName, DriveMedia.Unknown, _disk.Health, [],
                null, null, null, null, null, null, null, null, null, null, "Windows could not read the drive's details: " + ex.Message);
        }
        var a = HealthAssessor.Assess(report);
        _verdict.Text = a.Headline;
        _verdict.Foreground = a.Verdict switch
        {
            HealthVerdict.Concern => Theme.Danger,
            HealthVerdict.Watch => Theme.Warning,
            HealthVerdict.Good => Theme.Success,
            _ => Muted,
        };
        foreach (var f in a.Findings)
            _findings.Children.Add(new TextBlock { Text = "• " + f, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 1, 0, 1) });
        if (report.Note is { } note)
            _findings.Children.Add(new TextBlock { Text = note, TextWrapping = TextWrapping.Wrap, Foreground = Muted, Margin = new Thickness(0, 4, 0, 0) });

        void Row(string label, string? value)
        {
            if (value is null) return;
            var row = _counters.RowDefinitions.Count;
            _counters.RowDefinitions.Add(new RowDefinition());
            var l = new TextBlock { Text = label, Foreground = Muted };
            var v = new TextBlock { Text = value };
            Grid.SetRow(l, row);
            Grid.SetRow(v, row);
            Grid.SetColumn(v, 1);
            _counters.Children.Add(l);
            _counters.Children.Add(v);
        }
        Row("Drive", report.Model);
        Row("Type", report.Media switch { DriveMedia.Hdd => "Hard disk (HDD)", DriveMedia.Ssd => "Solid-state (SSD)", DriveMedia.Scm => "Storage-class memory", _ => null });
        Row("Windows health", report.WindowsHealth.ToString());
        Row("Temperature", report.TemperatureC is { } t ? $"{t} °C" + (report.TemperatureMaxC is { } m ? $" (highest recorded {m} °C)" : "") : null);
        Row("Powered on", report.PowerOnHours is { } h ? $"{h:N0} hours (about {h / 24.0 / 365:0.#} years)" : null);
        Row("Read errors", report.ReadErrorsTotal is null && report.ReadErrorsUncorrected is null ? null
            : $"{report.ReadErrorsTotal?.ToString("N0") ?? "?"} total, {report.ReadErrorsUncorrected?.ToString("N0") ?? "?"} uncorrected");
        Row("Write errors", report.WriteErrorsTotal is null && report.WriteErrorsUncorrected is null ? null
            : $"{report.WriteErrorsTotal?.ToString("N0") ?? "?"} total, {report.WriteErrorsUncorrected?.ToString("N0") ?? "?"} uncorrected");
        Row("Wear", report.WearPercent is { } w ? $"{w}% of rated life used" : null);
        Row("Start/stop cycles", report.StartStopCycles?.ToString("N0"));
        Row("Head load cycles", report.LoadUnloadCycles?.ToString("N0"));
        AppServices.Log.Info($"Disk {_disk.Number} health: {a.Verdict} — {a.Headline}");
    }

    private async Task StartScanAsync()
    {
        if (!Elevation.IsElevated())
        {
            MessageBox.Show(this, "A surface scan needs administrator rights. Close this window and click “Restart as administrator”.",
                "ShadowDisk", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        var continuing = _resumeFrom > 0 && _resumeFrom < _disk.SizeBytes;
        if (!continuing)
        {
            var answer = MessageBox.Show(this,
                $"Scan all of Disk {_disk.Number} ({ByteSize.Format(_disk.SizeBytes)})?\n\n" +
                "• Read-only: nothing on the disk is changed.\n" +
                "• It can take hours on a large hard disk. The PC stays usable but the disk will be slower meanwhile.\n" +
                "• If the drive is already failing, scanning can make it worse. If you have no backup of its files, make one first.\n\n" +
                "Start the scan?",
                "ShadowDisk — surface scan", MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.No);
            if (answer != MessageBoxResult.Yes) return;
            _found.Clear();
            _scanMap.Clear();
            ShowMap();
            _problems.Text = "";
        }

        IRawDiskReader reader;
        var options = new ScanOptions();
        try
        {
            reader = new PhysicalDriveReader(_disk.Number, _disk.SizeBytes, _disk.LogicalSectorSize, options.BlockBytes);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            MessageBox.Show(this, "The disk could not be opened for reading: " + ex.Message, "ShadowDisk", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        _scanning = true;
        var activity = Controls.DiskActivity.Begin();   // the platters spin up and the arm seeks while scanning
        _start.IsEnabled = false;
        _stop.IsEnabled = true;
        _save.IsEnabled = false;
        var cts = new CancellationTokenSource();
        _cts = cts;
        var progress = new Progress<ScanProgress>(p =>
        {
            _bar.Value = p.Fraction;
            var eta = p.Remaining is { } r ? $", about {(r.TotalHours >= 1 ? $"{(int)r.TotalHours} h {r.Minutes} min" : $"{Math.Max(1, (int)r.TotalMinutes)} min")} left" : "";
            _scanStatus.Text = $"Scanning: {ByteSize.Format(p.BytesDone)} of {ByteSize.Format(p.TotalBytes)} ({p.Fraction:P0}) at {p.MiBPerSecond:0} MiB/s{eta}. " +
                               $"Unreadable: {p.Unreadable} · slow: {p.Slow}";
            ShowMap();   // the problem list is shown when the scan stops (it is filled by the scan thread)
        });
        AppServices.Log.Info($"Surface scan of Disk {_disk.Number} {(continuing ? $"continued from {ByteSize.Format(_resumeFrom)}" : "started")}.");

        ScanResult result;
        try
        {
            var from = continuing ? _resumeFrom : 0;
            result = await Task.Run(() => SurfaceScanner.Scan(reader, from, _scanMap, _found, options, progress, cts.Token));
        }
        finally
        {
            reader.Dispose();
            activity.Dispose();
            _scanning = false;
            cts.Dispose();
            _cts = null;
        }

        _last = result;
        _resumeFrom = result.Status is ScanStatus.Cancelled or ScanStatus.Failed ? result.ScannedUpToBytes : 0;
        _bar.Value = result.TotalBytes == 0 ? 1 : (double)result.ScannedUpToBytes / result.TotalBytes;
        _scanStatus.Text = result.Message;
        _scanStatus.Foreground = result.Problems.Any(p => p.Kind == ScanProblemKind.Unreadable) || result.Status == ScanStatus.StoppedTooManyErrors
            ? Theme.Danger : Theme.Text;
        ShowMap();
        ShowProblems();
        _start.Content = _resumeFrom > 0 ? "Continue scan" : "Scan again…";
        _start.IsEnabled = true;
        _stop.IsEnabled = false;
        _save.IsEnabled = true;
        AppServices.Log.Info($"Surface scan of Disk {_disk.Number}: {result.Status}. {result.Message}");
    }

    private BlockState[] _shown = [];

    private void ShowMap()
    {
        var states = _scanMap.Snapshot();
        if (_shown.Length != states.Length) _shown = new BlockState[states.Length];
        for (var i = 0; i < _cells.Length && i < states.Length; i++)
        {
            if (states[i] == _shown[i]) continue;
            _cells[i].Background = CellBrush(states[i]);
            if (states[i] != BlockState.NotScanned) Theme.Flash(_cells[i]);   // a just-read cell lights up
            _shown[i] = states[i];
        }
    }

    private void ShowProblems()
    {
        var list = _found.ToList();
        _problems.Text = list.Count == 0
            ? (_last?.Status == ScanStatus.Completed ? "No problems found." : "No problems found so far.")
            : string.Join(Environment.NewLine, list.Take(500).Select(p => p.Kind == ScanProblemKind.Unreadable
                ? $"Unreadable at {ByteSize.Format(p.OffsetBytes)} ({SurfaceScanner.WhereIs(_disk, p.OffsetBytes)}): {p.Error}"
                : $"Slow at {ByteSize.Format(p.OffsetBytes)} ({SurfaceScanner.WhereIs(_disk, p.OffsetBytes)}): {p.Milliseconds:N0} ms"));
    }

    private void SaveResults()
    {
        if (_last is null) return;
        var dialog = new SaveFileDialog
        {
            FileName = $"ShadowDisk scan Disk {_disk.Number} {DateTime.Now:yyyy-MM-dd HHmm}.txt",
            Filter = "Text file (*.txt)|*.txt",
        };
        if (dialog.ShowDialog(this) != true) return;
        File.WriteAllText(dialog.FileName, SurfaceScanner.Describe(_disk, _last));
    }
}
