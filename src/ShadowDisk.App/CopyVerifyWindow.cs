using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using ShadowDisk.App.Services;
using ShadowDisk.Core;
using ShadowDisk.Core.Copying;

namespace ShadowDisk.App;

/// <summary>A drive the copy tool can use. Letter like "G".</summary>
public sealed record CopyDrive(string Letter, string? Label, long FreeBytes, long UsedBytes)
{
    public override string ToString() =>
        $"{Letter}:{(Label is { Length: > 0 } l ? $" ({l})" : "")} — {ByteSize.Format(UsedBytes)} used, {ByteSize.Format(FreeBytes)} free";
}

/// <summary>
/// "Copy & verify" tool (Stage 5, part 1). Copies all files from one drive into a NEW folder on another
/// drive, then checks every file's SHA-256 checksum. Only reads the source; never overwrites; never deletes.
/// </summary>
public sealed class CopyVerifyWindow : Window
{
    private readonly ComboBox _source = new() { Width = 380 };
    private readonly ComboBox _destination = new() { Width = 380 };
    private readonly TextBlock _target = new() { Foreground = Muted, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 4, 0, 10) };
    private readonly ProgressBar _bar = new() { Height = 18, Minimum = 0, Maximum = 1, Margin = new Thickness(0, 6, 0, 4) };
    private readonly TextBlock _status = new() { TextWrapping = TextWrapping.Wrap };
    private readonly TextBox _details = new()
    {
        IsReadOnly = true, Height = 150, TextWrapping = TextWrapping.NoWrap, FontFamily = new FontFamily("Consolas"), FontSize = 12,
        VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Auto, Margin = new Thickness(0, 8, 0, 8),
    };
    private readonly Button _start = new() { Content = "Start copy & verify", MinWidth = 160, Padding = new Thickness(12, 5, 12, 5), Margin = new Thickness(0, 0, 8, 0) };
    private readonly Button _cancel = new() { Content = "Cancel", MinWidth = 100, Padding = new Thickness(12, 5, 12, 5), IsEnabled = false, Margin = new Thickness(0, 0, 8, 0) };
    private readonly Button _open = new() { Content = "Open copy folder", MinWidth = 140, Padding = new Thickness(12, 5, 12, 5), IsEnabled = false };
    private CancellationTokenSource? _cts;
    private string? _lastFolder;

    private static Brush Muted => Theme.Muted;

    public CopyVerifyWindow(IReadOnlyList<CopyDrive> drives)
    {
        Title = "ShadowDisk — copy & verify";
        Width = 640;
        SizeToContent = SizeToContent.Height;
        ResizeMode = ResizeMode.NoResize;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        FontFamily = new FontFamily("Segoe UI");
        Theme.Apply(this);
        Icon = Application.Current.MainWindow?.Icon;
        FontSize = 13;

        var panel = new StackPanel { Margin = new Thickness(20) };
        panel.Children.Add(new Controls.SpinningDisk
        {
            Width = 34, Height = 34, FollowsActivity = true, IsSpinning = true,
            HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 0, 0, 10),
        });
        panel.Children.Add(new TextBlock
        {
            Text = "Copies every file from one drive into a NEW folder on another drive, then reads each copy back and compares " +
                   "SHA-256 checksums. The source drive is only read. Nothing is overwritten or deleted.",
            TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 12),
        });

        _source.ItemsSource = drives;
        _destination.ItemsSource = drives;
        panel.Children.Add(Row("Copy from:", _source));
        panel.Children.Add(Row("Copy to:", _destination));
        panel.Children.Add(_target);
        panel.Children.Add(_bar);
        panel.Children.Add(_status);
        panel.Children.Add(_details);

        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        var close = new Button { Content = "Close", MinWidth = 90, Padding = new Thickness(12, 5, 12, 5), Margin = new Thickness(8, 0, 0, 0), IsCancel = true };
        buttons.Children.Add(_start);
        buttons.Children.Add(_cancel);
        buttons.Children.Add(_open);
        buttons.Children.Add(close);
        panel.Children.Add(buttons);
        Content = panel;

        _source.SelectionChanged += (_, _) => UpdateTarget();
        _destination.SelectionChanged += (_, _) => UpdateTarget();
        _start.Click += async (_, _) => await StartAsync();
        _cancel.Click += (_, _) => { _cts?.Cancel(); _status.Text = "Cancelling…"; };
        _open.Click += (_, _) => { if (_lastFolder is { } f && Directory.Exists(f)) Process.Start(new ProcessStartInfo(f) { UseShellExecute = true }); };
        close.Click += (_, _) => Close();
        Closing += (_, e) =>
        {
            if (_cts is not null)
            {
                e.Cancel = true;
                MessageBox.Show(this, "A copy is running. Click Cancel first, then close.", "ShadowDisk", MessageBoxButton.OK, MessageBoxImage.Information);
            }
        };
        UpdateTarget();
    }

    private static StackPanel Row(string label, Control control)
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 6) };
        row.Children.Add(new TextBlock { Text = label, Width = 90, VerticalAlignment = VerticalAlignment.Center });
        row.Children.Add(control);
        return row;
    }

    private string? TargetFolder =>
        _source.SelectedItem is CopyDrive s && _destination.SelectedItem is CopyDrive d
            ? $@"{d.Letter}:\ShadowDisk copy of {s.Letter} {DateTime.Now.ToString("yyyy-MM-dd HHmm", CultureInfo.InvariantCulture)}"
            : null;

    private void UpdateTarget()
    {
        if (_source.SelectedItem is CopyDrive s && _destination.SelectedItem is CopyDrive d)
        {
            if (s.Letter == d.Letter)
            {
                _target.Text = "Choose two different drives.";
                _start.IsEnabled = false;
                return;
            }
            _target.Text = $"The copy will go into a new folder: {TargetFolder}" +
                           (d.FreeBytes < s.UsedBytes ? $"\nWarning: {d.Letter}: may not have enough free space." : "");
            _start.IsEnabled = _cts is null;
        }
        else
        {
            _target.Text = "Choose the drive to copy from and the drive to copy to.";
            _start.IsEnabled = false;
        }
    }

    /// <summary>Unfinished ShadowDisk copies of this source on the destination drive, newest first.</summary>
    private static List<string> FindUnfinished(CopyDrive source, CopyDrive destination)
    {
        try
        {
            return Directory.GetDirectories($@"{destination.Letter}:", $"ShadowDisk copy of {source.Letter} *")
                .Where(d => VerifiedCopy.IsUnfinishedCopyOf(d, $@"{source.Letter}:"))
                .OrderByDescending(d => d, StringComparer.Ordinal)
                .ToList();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return [];
        }
    }

    private async Task StartAsync()
    {
        if (_source.SelectedItem is not CopyDrive s || _destination.SelectedItem is not CopyDrive d || TargetFolder is not { } newFolder) return;

        var folder = newFolder;
        var resume = false;
        var unfinished = FindUnfinished(s, d);
        if (unfinished.Count > 0)
        {
            var answer = MessageBox.Show(this,
                $"An unfinished copy of {s.Letter}: is already on {d.Letter}: in this folder:\n\n    {Path.GetFileName(unfinished[0])}\n\n" +
                "Yes — resume it (files already copied are kept; everything is still verified)\n" +
                "No — start a new copy in a new folder\n" +
                "Cancel — do nothing",
                "ShadowDisk — resume copy?", MessageBoxButton.YesNoCancel, MessageBoxImage.Question, MessageBoxResult.Yes);
            if (answer == MessageBoxResult.Cancel) return;
            if (answer == MessageBoxResult.Yes)
            {
                folder = unfinished[0];
                resume = true;
            }
            if (unfinished.Count > (resume ? 1 : 0))
                _details.Text = $"Note: {unfinished.Count - (resume ? 1 : 0)} other unfinished copy folder(s) of {s.Letter}: on {d.Letter}: can be deleted.";
        }
        _lastFolder = folder;
        _target.Text = (resume ? "Resuming the unfinished copy in: " : "The copy will go into a new folder: ") + folder;
        _status.Foreground = Theme.Text;
        _status.FontWeight = FontWeights.Normal;
        var cts = new CancellationTokenSource();
        _cts = cts;
        _start.IsEnabled = _source.IsEnabled = _destination.IsEnabled = _open.IsEnabled = false;
        _cancel.IsEnabled = true;
        if (unfinished.Count == 0) _details.Text = "";
        AppServices.Log.Info($"Copy & verify {(resume ? "(resume) " : "")}: {s.Letter}: → {folder}");

        var progress = new Progress<CopyProgress>(p =>
        {
            _bar.Value = p.Fraction;
            _status.Text = p.Phase switch
            {
                CopyPhase.Scanning => "Listing files…",
                CopyPhase.Copying when resume => $"Resuming: {p.FilesDone + 1:N0} of {p.FilesTotal:N0} — {ByteSize.Format(p.BytesDone)} of {ByteSize.Format(p.BytesTotal)} (already-copied files are only checked)\n{p.CurrentFile}",
                CopyPhase.Copying => $"Copying {p.FilesDone + 1:N0} of {p.FilesTotal:N0} — {ByteSize.Format(p.BytesDone)} of {ByteSize.Format(p.BytesTotal)}\n{p.CurrentFile}",
                CopyPhase.Verifying => $"Verifying {p.FilesDone + 1:N0} of {p.FilesTotal:N0} — checking checksums\n{p.CurrentFile}",
                _ => "Finished.",
            };
        });

        CopyReport report;
        var activity = Controls.DiskActivity.Begin();
        try
        {
            report = await new VerifiedCopy(AppServices.Log).CopyAndVerifyAsync($@"{s.Letter}:\", folder, progress, cts.Token, resume);
        }
        finally
        {
            activity.Dispose();
            cts.Dispose();
            _cts = null;
            _cancel.IsEnabled = false;
            _source.IsEnabled = _destination.IsEnabled = true;
        }

        _status.Text = report.Message;
        _status.Foreground = report.IsVerified ? Theme.Success : Theme.Danger;
        _status.FontWeight = FontWeights.SemiBold;
        var lines = new List<string> { $"Files copied: {report.FilesCopied:N0}   Verified: {report.FilesVerified:N0}   Data: {ByteSize.Format(report.BytesCopied)}" +
                                       (report.FilesAlreadyCopied > 0 ? $"   Already copied earlier: {report.FilesAlreadyCopied:N0}" : "") };
        if (report.Failures.Count > 0) { lines.Add(""); lines.Add("PROBLEMS:"); lines.AddRange(report.Failures); }
        if (report.Skipped.Count > 0) { lines.Add(""); lines.Add("Skipped (not copied):"); lines.AddRange(report.Skipped); }
        _details.Text = string.Join(Environment.NewLine, lines);
        _open.IsEnabled = Directory.Exists(folder);
        UpdateTarget();
        // Keep showing the folder this run actually used (UpdateTarget would suggest a new, time-stamped name).
        _target.Text = (report.IsVerified ? "Verified copy is in: " : "Copy folder: ") + folder;
    }
}
