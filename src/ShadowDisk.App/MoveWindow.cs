using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using ShadowDisk.App.Services;
using ShadowDisk.Core;
using ShadowDisk.Core.Moving;
using ShadowDisk.Core.Operations;
using ShadowDisk.Core.Planning;

namespace ShadowDisk.App;

/// <summary>Everything the move window needs. Either a slider for a new position (<see cref="Range"/> + <see cref="Replan"/>), or a fixed plan.</summary>
public sealed record MoveSetup(
    string Title,
    string Intro,
    string Name,
    MoveRange? Range,
    long CurrentOffset,
    Func<long, bool, SectorPlan>? Replan,
    SectorPlan? FixedPlan,
    long UsedBytes,
    int DataCopies,
    string? DataCopyUnavailable,
    Action? OpenDataCopy,
    Func<SectorPlan, IProgress<SectorProgress>, CancellationToken, Task<OperationOutcome>> Run,
    Func<CancellationToken, Task<double?>> MeasureSpeed,
    bool IsResume = false,
    long? InitialOffset = null);

/// <summary>
/// "Move partition": choose where (or take the plan from a drag), choose the method — sector to sector or data copy —
/// with an estimated time for each, read the power caution, confirm by typing, then watch it run (pause keeps the journal).
/// </summary>
public sealed class MoveWindow : Window
{
    private readonly MoveSetup _setup;
    private readonly Slider _slider = new() { SmallChange = 1, LargeChange = 1024, IsMoveToPointEnabled = true, Margin = new Thickness(0, 4, 0, 2) };
    private readonly TextBlock _where = new() { FontWeight = FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap };
    private readonly CheckBox _takeIn = new() { Margin = new Thickness(0, 6, 0, 0) };
    private readonly StackPanel _steps = new() { Margin = new Thickness(0, 4, 0, 0) };
    private readonly RadioButton _sector = new() { GroupName = "method", IsChecked = true };
    private readonly RadioButton _copy = new() { GroupName = "method" };
    private readonly TextBlock _sectorText = new() { TextWrapping = TextWrapping.Wrap };
    private readonly TextBlock _copyText = new() { TextWrapping = TextWrapping.Wrap };
    private readonly TextBlock _power = new() { TextWrapping = TextWrapping.Wrap, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 4, 0, 0) };
    private readonly ProgressBar _bar = new() { Height = 16, Minimum = 0, Maximum = 1, Visibility = Visibility.Collapsed, Margin = new Thickness(0, 10, 0, 0) };
    private readonly TextBlock _status = new() { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 6, 0, 8) };
    private readonly Button _start = new() { Content = "Start…", MinWidth = 120, Padding = new Thickness(12, 5, 12, 5), Margin = new Thickness(0, 0, 8, 0) };
    private readonly Button _pause = new() { Content = "Pause", MinWidth = 100, Padding = new Thickness(12, 5, 12, 5), Margin = new Thickness(0, 0, 8, 0), Visibility = Visibility.Collapsed };
    private readonly Border _methodCard;
    private readonly System.Windows.Threading.DispatcherTimer _powerTimer = new() { Interval = TimeSpan.FromSeconds(5) };
    private SectorPlan _plan;
    private double? _speed;
    private bool _measured;
    private bool _running;
    private bool _finished;
    private CancellationTokenSource? _cts;

    public bool ChangedDisk { get; private set; }

    public MoveWindow(MoveSetup setup)
    {
        _setup = setup;
        Title = setup.Title;
        Width = 660;
        SizeToContent = SizeToContent.Height;
        ResizeMode = ResizeMode.NoResize;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        FontFamily = new FontFamily("Segoe UI");
        FontSize = 13;
        Theme.Apply(this);
        Icon = Application.Current.MainWindow?.Icon;

        var panel = new StackPanel { Margin = new Thickness(20) };
        var head = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 10) };
        head.Children.Add(new Controls.SpinningDisk { Width = 34, Height = 34, FollowsActivity = true, IsSpinning = true, Margin = new Thickness(0, 0, 10, 0) });
        head.Children.Add(new TextBlock { Text = setup.IsResume ? "Resume an unfinished move" : "Move partition", FontSize = 18, FontWeight = FontWeights.SemiBold, VerticalAlignment = VerticalAlignment.Center });
        panel.Children.Add(head);
        panel.Children.Add(new TextBlock { Text = setup.Intro, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 10) });

        // Where to (slider mode)
        if (setup.Range is { } range && setup.Replan is not null)
        {
            panel.Children.Add(new TextBlock { Text = $"Where should {setup.Name} go? Drag to choose its new start.", Margin = new Thickness(0, 4, 0, 0) });
            _slider.Minimum = range.MinOffset / (double)ByteSize.MiB;
            _slider.Maximum = range.MaxOffset / (double)ByteSize.MiB;
            _slider.Value = Math.Clamp((setup.InitialOffset ?? setup.CurrentOffset) / (double)ByteSize.MiB, _slider.Minimum, _slider.Maximum);
            _slider.ValueChanged += (_, _) => Replan();
            panel.Children.Add(_slider);
            panel.Children.Add(_where);
            _takeIn.Content = $"Take in the free space in front of {setup.Name} (it grows by the distance it moves left)";
            _takeIn.Checked += (_, _) => Replan();
            _takeIn.Unchecked += (_, _) => Replan();
            panel.Children.Add(_takeIn);
        }

        panel.Children.Add(new TextBlock { Text = "What will happen", FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 12, 0, 0) });
        panel.Children.Add(_steps);

        // Method choice with estimates
        var methods = new StackPanel();
        methods.Children.Add(new TextBlock { Text = "Choose a method", FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 0, 0, 6) });
        methods.Children.Add(MethodRow(_sector, "SECTOR TO SECTOR (recommended)", _sectorText));
        methods.Children.Add(MethodRow(_copy, "DATA COPY", _copyText));
        _methodCard = new Border
        {
            Child = methods, Padding = new Thickness(12), Margin = new Thickness(0, 12, 0, 0), CornerRadius = new CornerRadius(8),
            BorderBrush = Theme.Get("LineBrush"), BorderThickness = new Thickness(1), Background = Theme.Get("PanelAltBrush"),
            Visibility = setup.IsResume ? Visibility.Collapsed : Visibility.Visible,
        };
        panel.Children.Add(_methodCard);
        _copy.IsEnabled = setup.DataCopyUnavailable is null && setup.OpenDataCopy is not null;
        _sector.Checked += (_, _) => UpdateButtons();
        _copy.Checked += (_, _) => UpdateButtons();

        // Power caution
        var caution = new StackPanel();
        caution.Children.Add(new TextBlock
        {
            Text = "⚡  Before you start: on a laptop, charge the battery fully and keep the charger plugged in until the move is finished. " +
                   "Don't let the PC sleep, and don't unplug the disk. A power cut while blocks are moving is the one thing that can damage the drive. " +
                   "ShadowDisk keeps a journal after every piece so it can resume, but have a backup of anything important.",
            TextWrapping = TextWrapping.Wrap, Foreground = Theme.Warning,
        });
        caution.Children.Add(_power);
        panel.Children.Add(new Border
        {
            Child = caution, Padding = new Thickness(12), Margin = new Thickness(0, 12, 0, 0), CornerRadius = new CornerRadius(8),
            BorderBrush = Theme.Warning, BorderThickness = new Thickness(1),
        });

        panel.Children.Add(_bar);
        panel.Children.Add(_status);
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        var close = new Button { Content = "Close", MinWidth = 90, Padding = new Thickness(12, 5, 12, 5), IsCancel = true };
        buttons.Children.Add(_start);
        buttons.Children.Add(_pause);
        buttons.Children.Add(close);
        panel.Children.Add(buttons);
        Content = new ScrollViewer { Content = panel, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, MaxHeight = SystemParameters.WorkArea.Height - 60 };

        _start.Click += async (_, _) => await StartAsync();
        _pause.Click += (_, _) =>
        {
            _pause.IsEnabled = false;
            _status.Text = "Pausing after the current piece… (the journal is saved after every piece)";
            _cts?.Cancel();
        };
        close.Click += (_, _) => Close();
        Closing += (_, e) =>
        {
            if (!_running) return;
            e.Cancel = true;
            MessageBox.Show(this, "The move is running. Use “Pause” and wait for it to stop before closing.", "ShadowDisk", MessageBoxButton.OK, MessageBoxImage.Information);
        };
        Closed += (_, _) => _powerTimer.Stop();
        _powerTimer.Tick += (_, _) => UpdatePower();
        _powerTimer.Start();

        _plan = setup.FixedPlan ?? setup.Replan!(setup.CurrentOffset, false);
        Replan();
        if (!_plan.IsPossible && _copy.IsEnabled) _copy.IsChecked = true;   // sector to sector can't do this one; offer the data copy
        UpdatePower();
        Loaded += async (_, _) => await MeasureAsync();
    }

    private static UIElement MethodRow(RadioButton radio, string title, TextBlock text)
    {
        var body = new StackPanel();
        body.Children.Add(new TextBlock { Text = title, FontWeight = FontWeights.SemiBold });
        body.Children.Add(text);
        radio.Content = body;
        radio.Margin = new Thickness(0, 0, 0, 8);
        radio.VerticalContentAlignment = VerticalAlignment.Top;
        return radio;
    }

    private long NewOffset => PlanLimits.AlignDown((long)Math.Round(_slider.Value) * ByteSize.MiB);

    private void Replan()
    {
        if (_setup.Replan is not null && _setup.Range is not null && !_finished)
        {
            var offset = NewOffset;
            var distance = offset - _setup.CurrentOffset;
            _takeIn.IsEnabled = -distance >= SectorPlanner.MinimumChange;
            _plan = _setup.Replan(offset, _takeIn.IsEnabled && _takeIn.IsChecked == true);
            _where.Text = distance == 0
                ? $"{_setup.Name} stays where it is. Move the slider."
                : $"New start: {ByteSize.Format(offset)} — {(distance < 0 ? "left" : "right")} by {ByteSize.Format(Math.Abs(distance))}" +
                  $" (it can go from {ByteSize.Format(_setup.Range.MinOffset)} to {ByteSize.Format(_setup.Range.MaxOffset)}).";
        }
        _steps.Children.Clear();
        if (_plan.IsPossible)
        {
            var n = 1;
            foreach (var s in _plan.Steps)
                _steps.Children.Add(new TextBlock { Text = $"{n++}. {s.Text}", TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 1, 0, 1) });
            _steps.Children.Add(new TextBlock { Text = _plan.Summary, Foreground = Theme.Muted, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 4, 0, 0) });
        }
        else
        {
            foreach (var p in _plan.Problems)
                _steps.Children.Add(new TextBlock { Text = "•  " + p, Foreground = Theme.Muted, TextWrapping = TextWrapping.Wrap });
        }
        UpdateEstimates();
        UpdateButtons();
    }

    private async Task MeasureAsync()
    {
        _sectorText.Text = _copyText.Text = "Measuring this disk's speed (a short read, nothing is written)…";
        try { _speed = await _setup.MeasureSpeed(CancellationToken.None); }
        catch (Exception ex) { AppServices.Log.Warning("Speed test failed: " + ex.Message); }
        _measured = true;
        if (_speed is { } s) AppServices.Log.Info($"Move: measured read speed {s:F0} MiB/s.");
        UpdateEstimates();
    }

    private void UpdateEstimates()
    {
        if (!_measured) return;
        var speed = _speed ?? 60;
        var rough = _speed is null ? " (rough guess: the disk speed could not be measured)" : $" (this disk reads about {speed:F0} MB/s)";
        var used = _plan.MovedUsedBytes > 0 ? _plan.MovedUsedBytes : _setup.UsedBytes;
        _sectorText.Text =
            $"Moves the drive's used blocks directly on the disk — only the {ByteSize.Format(used)} in use, not the empty space. " +
            "No extra drive, no renaming, the drive keeps its letter. Every piece is read back and compared; a journal after each piece lets it resume after a power cut. " +
            $"NTFS drives only. Estimated time: {MoveEstimate.Format(MoveEstimate.SectorMove(used, speed))}{rough}.";
        _copyText.Text = _setup.DataCopyUnavailable is { } why
            ? $"Not available here: {why}"
            : "Copies the files to a temporary volume and back, checking every file both ways, then deletes and recreates the drive under the same letter. " +
              $"Slower and needs free space for a full copy, but works file by file. Estimated time: {MoveEstimate.Format(MoveEstimate.DataCopy(used, speed, _setup.DataCopies))}{rough}.";
    }

    private void UpdatePower()
    {
        var mains = ShadowDisk.Windows.PowerStatus.IsOnMainsPower();
        var battery = ShadowDisk.Windows.PowerStatus.BatteryPercent();
        (_power.Text, _power.Foreground) = (mains, battery) switch
        {
            (false, var b) => ($"Now: running on battery{(b is { } p ? $" ({p}%)" : "")}. Plug in the charger — moving is blocked on battery.", Theme.Danger),
            (true, < 100 and var b) => ($"Now: charger plugged in ✓  Battery {b}% — best to let it charge fully first.", Theme.Warning),
            (true, _) => ("Now: charger plugged in ✓", Theme.Success),
            _ => ("Now: no battery reported (desktop PC). Make sure the power is stable.", Theme.Muted),
        };
    }

    private void UpdateButtons()
    {
        _start.IsEnabled = !_running && !_finished && (_copy.IsChecked == true ? _copy.IsEnabled : _plan.IsPossible);
        _start.Content = _setup.IsResume ? "Resume…" : _copy.IsChecked == true ? "Open data copy…" : "Start…";
    }

    private async Task StartAsync()
    {
        if (_copy.IsChecked == true)
        {
            Close();
            _setup.OpenDataCopy?.Invoke();
            return;
        }
        if (!_plan.IsPossible) return;
        var letter = _setup.Name.TrimEnd(':');
        var used = _plan.MovedUsedBytes > 0 ? _plan.MovedUsedBytes : _setup.UsedBytes;
        var facts = _plan.Steps.Select((s, i) => $"{i + 1}. {s.Text}").ToList();
        facts.Add($"While it moves, {_setup.Name} is hidden from Windows (no letter, can't be opened). It comes back under the same letter at the end.");
        facts.Add($"About {ByteSize.Format(used)} is moved. Estimated time: {MoveEstimate.Format(MoveEstimate.SectorMove(used, _speed ?? 60))}.");
        facts.Add("Keep the charger plugged in. You can pause between pieces; “Resume move” finishes it later, even after a restart.");
        facts.Add("No disk tool can guarantee that data is never lost. A failing drive or a power cut at the wrong moment can cause data loss.");
        var confirm = new ConfirmDeleteWindow(
            _setup.IsResume ? $"Resume moving {_setup.Name}?" : $"Move {_setup.Name} sector to sector?", facts, $"MOVE {letter}",
            $"I have a backup of {_setup.Name}'s important files (or this is a practice disk).", "ShadowDisk — confirm the move", _setup.IsResume ? "Resume" : "Start moving")
        { Owner = this };
        if (confirm.ShowDialog() != true) return;

        _running = true;
        _slider.IsEnabled = _takeIn.IsEnabled = _methodCard.IsEnabled = false;
        _start.Visibility = Visibility.Collapsed;
        _pause.Visibility = Visibility.Visible;
        _pause.IsEnabled = true;
        _bar.Visibility = Visibility.Visible;
        _status.Text = "Starting… Don't turn off the PC or unplug the disk.";
        var cts = _cts = new CancellationTokenSource();
        var activity = Controls.DiskActivity.Begin();
        var progress = new Progress<SectorProgress>(p =>
        {
            var stepText = $"Step {p.StepIndex + 1} of {p.StepCount}: {p.Step.Text}";
            if (p.Move is { } m)
            {
                _bar.IsIndeterminate = false;
                _bar.Value = m.BytesTotal == 0 ? 0 : m.BytesDone / (double)m.BytesTotal;
                _status.Text = $"{stepText}\n{ByteSize.Format(m.BytesDone)} of {ByteSize.Format(m.BytesTotal)} moved and checked — {m.MiBPerSecond:F0} MB/s" +
                               (m.Remaining is { } r ? $", {MoveEstimate.Format(r)} left" : "");
            }
            else
            {
                _bar.IsIndeterminate = true;
                _status.Text = stepText;
            }
        });

        OperationOutcome outcome;
        try
        {
            outcome = await _setup.Run(_plan, progress, cts.Token);
        }
        catch (Exception ex)
        {
            AppServices.Log.Error("Move stopped by an error: " + ex.Message);
            outcome = new OperationOutcome(OperationStatus.Failed,
                $"The move stopped because of an error: {ex.Message} Nothing is retried. If the move had started, ShadowDisk will offer to resume it next time it opens.", [], null);
        }
        finally
        {
            activity.Dispose();
            _running = false;
            cts.Dispose();
            _cts = null;
        }

        ChangedDisk = true;
        _finished = true;
        _pause.Visibility = Visibility.Collapsed;
        _bar.IsIndeterminate = false;
        if (outcome.Status == OperationStatus.Succeeded) _bar.Value = 1;
        _status.Foreground = outcome.Status switch
        {
            OperationStatus.Succeeded => Theme.Success,
            OperationStatus.Cancelled => Theme.Warning,
            _ => Theme.Danger,
        };
        _status.Text = outcome.Status == OperationStatus.Cancelled
            ? "Paused. " + outcome.Message
            : outcome.Message;
        AppServices.Log.Info($"Move window: {outcome.Status} — {outcome.Message}");
        UpdateButtons();
    }
}
