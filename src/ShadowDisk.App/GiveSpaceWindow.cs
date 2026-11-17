using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using ShadowDisk.App.Services;
using ShadowDisk.Core;
using ShadowDisk.Core.Copying;
using ShadowDisk.Core.Planning;
using ShadowDisk.Core.Wizard;

namespace ShadowDisk.App;

/// <summary>Everything the move-space window shows for one direction (see <see cref="SpaceMoveSetups"/>).</summary>
public sealed record SpaceMoveSetup(
    string Title,
    string Intro,
    string Facts,
    IReadOnlyList<string> Notes,
    string Question,
    long MinGive,
    long MaxGive,
    long? InitialGive,
    Func<long, string> Describe,
    Func<long, IReadOnlyList<string>> StartFacts,
    string StartHeading,
    string BackupText,
    string FinishedText,
    Func<long, ISpaceMoveWizard> CreateWizard);

/// <summary>Builds the window text for both directions of moving space between neighbouring volumes.</summary>
public static class SpaceMoveSetups
{
    private const string NoGuarantee = "No disk tool can guarantee that data is never lost. A power cut or a failing drive during a step can cause data loss.";
    private const string OneByOne = "Each step is confirmed by you with “Run next step”; deleting a volume asks for its own confirmation.";

    /// <summary>Give space to <c>plan.Target</c> from what follows it (parts 3 and 4: next volume and/or Recovery).</summary>
    public static SpaceMoveSetup ForGiveSpace(GiveSpacePlan plan, Func<long, GiveSpaceWizard> create, long? initialGive = null)
    {
        var target = plan.Target.DisplayName;
        var donor = plan.Donor?.DisplayName;
        var inTheWay = (plan.Recovery, donor) switch
        {
            (not null, not null) => $"the Recovery partition and {donor} are",
            (not null, null) => "the Recovery partition is",
            _ => $"{donor} is",
        };
        var how = new List<string>();
        if (donor is not null) how.Add($"moves {donor}'s files to a new volume further along the disk and checks every file");
        if (plan.Recovery is not null)
            how.Add("turns Windows Recovery off, moves the Recovery partition to just after the bigger " + target + " and turns Windows Recovery back on");
        var facts = $"{target} {ByteSize.Format(plan.Target.SizeBytes)}";
        if (plan.Recovery is { } rec) facts += $"    Recovery {ByteSize.Format(rec.SizeBytes)}";
        if (plan.Donor is { } d)
            facts += $"    {donor} {ByteSize.Format(d.SizeBytes)} ({ByteSize.Format(plan.DonorUsedBytes)} of files; they need about {ByteSize.Format(plan.NeededForDonorFiles)})";

        IReadOnlyList<string> StartFacts(long give)
        {
            var list = new List<string> { $"{target} grows by {ByteSize.Format(give)}." };
            if (donor is not null) list.Add($"{donor}'s files are copied to a new volume and every file is checked before the old {donor} is deleted.");
            if (plan.Recovery is not null)
                list.Add("Windows Recovery is turned off with Microsoft's reagentc tool, the Recovery partition is recreated after " +
                         $"{target}, and Windows Recovery is turned back on. Its image is checked to be safe on the Windows drive first.");
            list.Add(OneByOne);
            list.Add(NoGuarantee);
            return list;
        }

        var backup = Core.Operations.OperationPolicy.IsExtendableWindowsVolume(plan.Target)
            ? "I have a full backup of this PC (for example a system image) and my important files."
            : donor is null ? $"I have a backup of {target}'s important files (or this is a test disk)."
            : $"I have a separate backup of {donor}'s important files (or this is a test disk).";

        return new SpaceMoveSetup(
            $"ShadowDisk — give space to {target}",
            $"Windows can only grow {target} into free space directly after it, and {inTheWay} in the way. " +
            $"This wizard {string.Join(", and ", how)}. " + (donor is null ? "" : $"{donor} keeps its letter. ") +
            "Each step is checked again before it runs.",
            facts, plan.Notes ?? [], $"How much space to give to {target}?",
            plan.MinGive, plan.MaxGive, initialGive, plan.Describe, StartFacts,
            donor is null ? "This will change the disk in several steps."
                : $"This will change the disk in several steps and, after verifying a copy of its files, delete and recreate {donor}.",
            backup,
            $"Finished. {target} now has the extra space" + (donor is null ? "." : $" and {donor} holds its files under the same letter. Check both in Explorer."),
            give => create(give));
    }

    /// <summary>Give space from <c>plan.Giver</c> to the volume after it (drag the divider towards the start).</summary>
    public static SpaceMoveSetup ForTakeSpace(TakeSpacePlan plan, Func<long, TakeSpaceWizard> create, long? initialGive = null)
    {
        var giver = plan.Giver.DisplayName;
        var receiver = plan.Receiver.DisplayName;
        return new SpaceMoveSetup(
            $"ShadowDisk — give space to {receiver}",
            $"Windows can't grow {receiver} towards the start of the disk, and moving a partition block by block can lose it if " +
            $"the power fails half-way. So this wizard shrinks {giver}, moves {receiver}'s files out to the end of its space and back " +
            $"into a new, bigger {receiver} (every file checked both times), then tidies up. {receiver} keeps its letter. " +
            "Each step is checked again before it runs.",
            $"{giver} {ByteSize.Format(plan.Giver.SizeBytes)}    {receiver} {ByteSize.Format(plan.Receiver.SizeBytes)} " +
            $"({ByteSize.Format(plan.ReceiverUsedBytes)} of files; they need about {ByteSize.Format(plan.NeededForFiles)})",
            plan.Notes, $"How much space should {giver} give to {receiver}?",
            plan.MinGive, plan.MaxGive, initialGive, plan.Describe,
            give =>
            [
                $"{giver} shrinks by {ByteSize.Format(give)} and {receiver} grows by about the same.",
                $"{receiver}'s files are copied out and verified, the old {receiver} is deleted, then they are copied back into the new " +
                $"{receiver} and verified again before the temporary copy is deleted.",
                OneByOne,
                NoGuarantee,
            ],
            $"This will change the disk in several steps and, after verifying copies of its files, delete and recreate {receiver}.",
            $"I have a separate backup of {receiver}'s important files (or this is a test disk).",
            $"Finished. {receiver} now has the extra space and holds its files under the same letter, and {giver} is smaller. Check both in Explorer.",
            give => create(give));
    }
}

/// <summary>
/// Wizard window for moving space between neighbouring volumes, in either direction. The user chooses how much,
/// confirms once, then runs each step with "Run next step". Every delete asks for its own typed confirmation.
/// </summary>
public sealed class GiveSpaceWindow : Window
{
    private readonly SpaceMoveSetup _setup;
    private readonly Slider _slider = new() { SmallChange = 1, LargeChange = 1024, IsMoveToPointEnabled = true, Margin = new Thickness(0, 4, 0, 4) };
    private readonly TextBlock _amount = new() { FontWeight = FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap };
    private readonly StackPanel _stepsPanel = new() { Margin = new Thickness(0, 10, 0, 6) };
    private readonly ProgressBar _bar = new() { Height = 16, Minimum = 0, Maximum = 1, Visibility = Visibility.Collapsed };
    private readonly TextBlock _status = new() { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 4, 0, 8) };
    private readonly Button _start = new() { Content = "Start…", MinWidth = 120, Padding = new Thickness(12, 5, 12, 5), Margin = new Thickness(0, 0, 8, 0) };
    private readonly Button _next = new() { Content = "Run next step", MinWidth = 140, Padding = new Thickness(12, 5, 12, 5), Margin = new Thickness(0, 0, 8, 0), Visibility = Visibility.Collapsed };
    private readonly Button _cancel = new() { Content = "Cancel copy", MinWidth = 110, Padding = new Thickness(12, 5, 12, 5), Margin = new Thickness(0, 0, 8, 0), IsEnabled = false, Visibility = Visibility.Collapsed };
    private ISpaceMoveWizard? _wizard;
    private CancellationTokenSource? _cts;
    private bool _running;

    public bool ChangedDisk { get; private set; }

    public GiveSpaceWindow(SpaceMoveSetup setup)
    {
        _setup = setup;
        Title = setup.Title;
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
        panel.Children.Add(new TextBlock { Text = setup.Intro, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 10) });
        panel.Children.Add(new TextBlock { Text = setup.Facts, Foreground = Theme.Muted, TextWrapping = TextWrapping.Wrap });
        foreach (var note in setup.Notes)
            panel.Children.Add(new TextBlock
            {
                Text = "⚠  " + note, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 6, 0, 0), Foreground = Theme.Warning,
            });

        panel.Children.Add(new TextBlock { Text = setup.Question, Margin = new Thickness(0, 12, 0, 0) });
        _slider.Minimum = setup.MinGive / (double)ByteSize.MiB;
        _slider.Maximum = setup.MaxGive / (double)ByteSize.MiB;
        _slider.Value = setup.InitialGive is { } initial
            ? Math.Clamp(initial / (double)ByteSize.MiB, _slider.Minimum, _slider.Maximum)
            : _slider.Maximum;
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
            else if (_wizard is { IsFinished: false } w && w.Steps.Any(s => s.State == StepState.Done))
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

    private void UpdateAmount() => _amount.Text = _setup.Describe(GiveBytes);

    private static bool IsCopy(GiveSpaceStep step) => step.Kind is GiveSpaceStepKind.CopyAndVerify or GiveSpaceStepKind.CopyBack;

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
                StepState.Done => ("✓", Theme.SuccessHex),
                StepState.Skipped => ("–", Theme.MutedHex),
                StepState.Failed => ("✗", Theme.DangerHex),
                _ => (step == _wizard.NextStep ? "▶" : "○", step == _wizard.NextStep ? Theme.WarningHex : Theme.MutedHex),
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
                                                 Foreground = Theme.Muted });
            _stepsPanel.Children.Add(row);
        }
        _next.IsEnabled = !_wizard.IsFinished && !_running;
        _next.Content = _wizard.NextStep?.State == StepState.Failed ? "Try this step again" : "Run next step";
    }

    private void Start()
    {
        var give = GiveBytes;
        var confirm = new ConfirmDeleteWindow(_setup.StartHeading, _setup.StartFacts(give), "GIVE SPACE", _setup.BackupText, "Start giving space", "Start")
        {
            Owner = this,
        };
        if (confirm.ShowDialog() != true) return;

        _wizard = _setup.CreateWizard(give);
        AppServices.Log.Info($"{_setup.Title.Replace("ShadowDisk — ", "")}: started — {ByteSize.Format(_wizard.GiveBytes)}.");
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
        var isCopy = IsCopy(step);
        _running = true;
        var activity = Controls.DiskActivity.Begin();   // every disk in the app spins up while the step runs
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
            if (step.State == StepState.Done && !isCopy) ChangedDisk = true;
        }
        finally
        {
            activity.Dispose();
            _running = false;
            cts.Dispose();
            _cts = null;
            _cancel.IsEnabled = false;
        }

        _status.Text = _wizard.IsFinished
            ? _setup.FinishedText
            : step.State == StepState.Failed
                ? "This step did not complete; the wizard has stopped. Read the message above. You can try the step again or close and finish later."
                : "Done. Click “Run next step” to continue.";
        if (_wizard.IsFinished) AppServices.Log.Info($"{_setup.Title.Replace("ShadowDisk — ", "")}: finished.");
        ShowSteps();
    }

    /// <summary>Asked before each delete; <paramref name="confirmText"/> is like "DELETE G".</summary>
    private bool ConfirmDelete(string confirmText)
    {
        var letter = confirmText.Split(' ').Last();
        var copy = VerifiedCopyRegistry.Find($@"{letter}:\");
        var dialog = new ConfirmDeleteWindow(
            $"Delete {letter}: now? Its files have been copied and verified.",
            [
                copy is not null ? $"Verified copy: {copy.Files:N0} files, {ByteSize.Format(copy.Bytes)}, in {copy.DestinationFolder}." : "Verified copy: see the copy step.",
                $"After this, {letter}:'s space becomes free and is used in a later step.",
                "This cannot be undone.",
            ],
            confirmText,
            "I have opened a few files in the verified copy and they work.")
        { Owner = this };
        return dialog.ShowDialog() == true;
    }
}
