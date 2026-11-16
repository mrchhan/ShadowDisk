using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using ShadowDisk.App.Services;
using ShadowDisk.Core;
using ShadowDisk.Core.Copying;
using ShadowDisk.Core.Planning;
using ShadowDisk.Core.Wizard;

namespace ShadowDisk.App;

/// <summary>
/// "Give space to the drive before" wizard (Stage 5, parts 3 and 4: also moves a Recovery partition). The user chooses how much to give, confirms once,
/// then runs each step with "Run next step". The delete step asks for its own typed confirmation.
/// </summary>
internal static class OperationPolicyText
{
    public static bool IsWindows(GiveSpacePlan plan) => Core.Operations.OperationPolicy.IsExtendableWindowsVolume(plan.Target);
}

public sealed class GiveSpaceWindow : Window
{
    private readonly GiveSpacePlan _plan;
    private readonly Func<long, GiveSpaceWizard> _createWizard;
    private readonly string _target;
    private readonly string? _donor;
    private readonly Slider _slider = new() { SmallChange = 1, LargeChange = 1024, IsMoveToPointEnabled = true, Margin = new Thickness(0, 4, 0, 4) };
    private readonly TextBlock _amount = new() { FontWeight = FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap };
    private readonly StackPanel _stepsPanel = new() { Margin = new Thickness(0, 10, 0, 6) };
    private readonly ProgressBar _bar = new() { Height = 16, Minimum = 0, Maximum = 1, Visibility = Visibility.Collapsed };
    private readonly TextBlock _status = new() { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 4, 0, 8) };
    private readonly Button _start = new() { Content = "Start…", MinWidth = 120, Padding = new Thickness(12, 5, 12, 5), Margin = new Thickness(0, 0, 8, 0) };
    private readonly Button _next = new() { Content = "Run next step", MinWidth = 140, Padding = new Thickness(12, 5, 12, 5), Margin = new Thickness(0, 0, 8, 0), Visibility = Visibility.Collapsed };
    private readonly Button _cancel = new() { Content = "Cancel copy", MinWidth = 110, Padding = new Thickness(12, 5, 12, 5), Margin = new Thickness(0, 0, 8, 0), IsEnabled = false, Visibility = Visibility.Collapsed };
    private GiveSpaceWizard? _wizard;
    private CancellationTokenSource? _cts;
    private bool _running;

    public bool ChangedDisk { get; private set; }

    public GiveSpaceWindow(GiveSpacePlan plan, Func<long, GiveSpaceWizard> createWizard)
    {
        _plan = plan;
        _createWizard = createWizard;
        _target = plan.Target.DisplayName;
        _donor = plan.Donor?.DisplayName;

        Title = $"ShadowDisk — give space to {_target}";
        Width = 640;
        SizeToContent = SizeToContent.Height;
        ResizeMode = ResizeMode.NoResize;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        FontFamily = new FontFamily("Segoe UI");
        FontSize = 13;

        var panel = new StackPanel { Margin = new Thickness(20) };
        var inTheWay = (plan.Recovery, _donor) switch
        {
            (not null, not null) => $"the Recovery partition and {_donor} are",
            (not null, null) => "the Recovery partition is",
            _ => $"{_donor} is",
        };
        var how = new List<string>();
        if (_donor is not null)
            how.Add($"moves {_donor}'s files to a new volume further along the disk and checks every file");
        if (plan.Recovery is not null)
            how.Add("turns Windows Recovery off, moves the Recovery partition to just after the bigger " + _target + " and turns Windows Recovery back on");
        panel.Children.Add(new TextBlock
        {
            Text = $"Windows can only grow {_target} into free space directly after it, and {inTheWay} in the way. " +
                   $"This wizard {string.Join(", and ", how)}. " +
                   (_donor is null ? "" : $"{_donor} keeps its letter. ") + "Each step is checked again before it runs.",
            TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 10),
        });
        var facts = $"{_target} {ByteSize.Format(plan.Target.SizeBytes)}";
        if (plan.Recovery is { } rec) facts += $"    Recovery {ByteSize.Format(rec.SizeBytes)}";
        if (plan.Donor is { } d)
            facts += $"    {_donor} {ByteSize.Format(d.SizeBytes)} ({ByteSize.Format(plan.DonorUsedBytes)} of files; they need about {ByteSize.Format(plan.NeededForDonorFiles)})";
        panel.Children.Add(new TextBlock
        {
            Text = facts, Foreground = new SolidColorBrush(Color.FromRgb(0x4B, 0x55, 0x63)), TextWrapping = TextWrapping.Wrap,
        });
        foreach (var note in plan.Notes ?? [])
            panel.Children.Add(new TextBlock
            {
                Text = "⚠  " + note, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 6, 0, 0),
                Foreground = new SolidColorBrush(Color.FromRgb(0x92, 0x40, 0x0E)),
            });

        panel.Children.Add(new TextBlock { Text = $"How much space to give to {_target}?", Margin = new Thickness(0, 12, 0, 0) });
        _slider.Minimum = plan.MinGive / (double)ByteSize.MiB;
        _slider.Maximum = plan.MaxGive / (double)ByteSize.MiB;
        _slider.Value = _slider.Maximum;
        _slider.ValueChanged += (_, _) => UpdateAmount();
        panel.Children.Add(_slider);
        panel.Children.Add(_amount);
        panel.Children.Add(new ScrollViewer
        {
            Content = _stepsPanel, MaxHeight = 320, VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
        });
        panel.Children.Add(_bar);
        panel.Children.Add(_status);

        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        var close = new Button { Content = "Close", MinWidth = 90, Padding = new Thickness(12, 5, 12, 5), IsCancel = true };
        buttons.Children.Add(_start);
        buttons.Children.Add(_next);
        buttons.Children.Add(_cancel);
        buttons.Children.Add(close);
        panel.Children.Add(buttons);
        Content = panel;

        _start.Click += (_, _) => Start();
        _next.Click += async (_, _) => await RunNextAsync();
        _cancel.Click += (_, _) => _cts?.Cancel();
        close.Click += (_, _) => Close();
        Closing += (_, e) =>
        {
            if (_running)
            {
                e.Cancel = true;
                MessageBox.Show(this, "A step is running. Wait for it to finish (or cancel the copy) before closing.", "ShadowDisk",
                    MessageBoxButton.OK, MessageBoxImage.Information);
            }
            else if (_wizard is { IsFinished: false } w && w.Steps.Any(s => s.State == Core.Wizard.StepState.Done))
            {
                var answer = MessageBox.Show(this,
                    $"The wizard has not finished. Next step: “{w.NextStep?.Title}”.\n\nIf you close now, nothing more is changed, " +
                    "but you will need to finish the remaining steps yourself (or start again). Close anyway?",
                    "ShadowDisk", MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No);
                if (answer != MessageBoxResult.Yes) e.Cancel = true;
            }
        };
        UpdateAmount();
        ShowSteps();
    }

    private long GiveBytes => PlanLimits.AlignDown((long)Math.Round(_slider.Value) * ByteSize.MiB);

    private void UpdateAmount()
    {
        _amount.Text = _plan.Describe(GiveBytes);
    }

    private void ShowSteps()
    {
        _stepsPanel.Children.Clear();
        if (_wizard is null) return;
        _stepsPanel.Children.Add(new TextBlock { Text = "Steps", FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 0, 0, 4) });
        var number = 1;
        foreach (var step in _wizard.Steps)
        {
            var (symbol, color) = step.State switch
            {
                Core.Wizard.StepState.Done => ("✓", "#047857"),
                Core.Wizard.StepState.Skipped => ("–", "#6B7280"),
                Core.Wizard.StepState.Failed => ("✗", "#B91C1C"),
                _ => (step == _wizard.NextStep ? "▶" : "○", step == _wizard.NextStep ? "#B45309" : "#6B7280"),
            };
            var row = new StackPanel { Margin = new Thickness(0, 2, 0, 2) };
            row.Children.Add(new TextBlock
            {
                Text = $"{symbol}  {number++}. {step.Title}",
                Foreground = (Brush)new BrushConverter().ConvertFromString(color)!,
                FontWeight = step == _wizard.NextStep ? FontWeights.SemiBold : FontWeights.Normal,
            });
            if (step.Message is { } m)
                row.Children.Add(new TextBlock { Text = m, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(22, 0, 0, 0), FontSize = 12,
                                                 Foreground = new SolidColorBrush(Color.FromRgb(0x4B, 0x55, 0x63)) });
            _stepsPanel.Children.Add(row);
        }
        _next.IsEnabled = !_wizard.IsFinished && !_running;
        _next.Content = _wizard.NextStep?.State == Core.Wizard.StepState.Failed ? "Try this step again" : "Run next step";
    }

    private void Start()
    {
        var give = GiveBytes;
        var facts = new List<string> { $"{_target} grows by {ByteSize.Format(give)}." };
        if (_donor is not null)
            facts.Add($"{_donor}'s files are copied to a new volume and every file is checked before the old {_donor} is deleted.");
        if (_plan.Recovery is not null)
            facts.Add("Windows Recovery is turned off with Microsoft's reagentc tool, the Recovery partition is recreated after " +
                      $"{_target}, and Windows Recovery is turned back on. Its image is checked to be safe on the Windows drive first.");
        facts.Add("Each step is confirmed by you with “Run next step”; deleting a volume asks for its own confirmation.");
        facts.Add("No disk tool can guarantee that data is never lost. A power cut or a failing drive during a step can cause data loss.");
        var backup = OperationPolicyText.IsWindows(_plan)
            ? "I have a full backup of this PC (for example a system image) and my important files."
            : _donor is null ? $"I have a backup of {_target}'s important files (or this is a test disk)."
            : $"I have a separate backup of {_donor}'s important files (or this is a test disk).";
        var confirm = new ConfirmDeleteWindow(
            _donor is null ? "This will change the disk in several steps." : $"This will change the disk in several steps and, after verifying a copy of its files, delete and recreate {_donor}.",
            facts, "GIVE SPACE", backup, "Start giving space", "Start")
        { Owner = this };
        if (confirm.ShowDialog() != true) return;

        _wizard = _createWizard(give);
        AppServices.Log.Info($"Give space: started — {ByteSize.Format(_wizard.GiveBytes)} to {_target}" +
                             (_plan.Recovery is null ? "" : ", moving Recovery") + (_donor is null ? "." : $", moving {_donor}."));
        _slider.IsEnabled = false;
        _start.Visibility = Visibility.Collapsed;
        _next.Visibility = Visibility.Visible;
        ShowSteps();
        _status.Text = "Click “Run next step” to begin.";
    }

    private async Task RunNextAsync()
    {
        if (_wizard is null || _wizard.IsFinished) return;
        var step = _wizard.NextStep!;
        var isCopy = step.Kind == GiveSpaceStepKind.CopyAndVerify;
        _running = true;
        _next.IsEnabled = false;
        var cts = new CancellationTokenSource();
        _cts = cts;
        _cancel.Visibility = isCopy ? Visibility.Visible : Visibility.Collapsed;
        _cancel.IsEnabled = isCopy;
        _bar.Visibility = isCopy ? Visibility.Visible : Visibility.Collapsed;
        _status.Text = $"Running: {step.Title}… Don't turn off the PC or unplug the disk.";

        var progress = new Progress<CopyProgress>(p =>
        {
            _bar.Value = p.Fraction;
            _status.Text = p.Phase switch
            {
                CopyPhase.Copying => $"Copying {p.FilesDone:N0} of {p.FilesTotal:N0} — {ByteSize.Format(p.BytesDone)} of {ByteSize.Format(p.BytesTotal)}",
                CopyPhase.Verifying => $"Verifying {p.FilesDone:N0} of {p.FilesTotal:N0} — checking checksums",
                CopyPhase.Scanning => "Listing files…",
                _ => "Finishing…",
            };
        });

        try
        {
            await _wizard.RunNextAsync(ConfirmDelete, progress, cts.Token);
            if (step.State == Core.Wizard.StepState.Done && step.Kind != GiveSpaceStepKind.CopyAndVerify) ChangedDisk = true;
        }
        finally
        {
            _running = false;
            cts.Dispose();
            _cts = null;
            _cancel.IsEnabled = false;
        }

        _status.Text = _wizard.IsFinished
            ? $"Finished. {_target} now has the extra space" + (_donor is null ? "." : $" and {_donor} holds its files under the same letter. Check both in Explorer.")
            : step.State == Core.Wizard.StepState.Failed
                ? "This step did not complete; the wizard has stopped. Read the message above. You can try the step again or close and finish later."
                : "Done. Click “Run next step” to continue.";
        if (_wizard.IsFinished) AppServices.Log.Info($"Give space: finished — {_target} extended.");
        ShowSteps();
    }

    private bool ConfirmDelete(string confirmText)
    {
        var letter = _plan.Donor!.Volume!.DriveLetter!;
        var copy = VerifiedCopyRegistry.Find($@"{letter}:\");
        var dialog = new ConfirmDeleteWindow(
            $"Delete the old {letter}: now? Its files have been copied and verified.",
            [
                copy is not null ? $"Verified copy: {copy.Files:N0} files, {ByteSize.Format(copy.Bytes)}, now on the temporary volume." : "Verified copy: see the copy step.",
                $"After this, {letter}:'s old space becomes free and is given to {_target} in a later step.",
                "This cannot be undone.",
            ],
            confirmText,
            "I have opened a few files on the temporary volume and they work.")
        { Owner = this };
        return dialog.ShowDialog() == true;
    }
}
