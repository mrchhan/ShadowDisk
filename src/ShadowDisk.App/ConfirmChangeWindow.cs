using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using ShadowDisk.Core;
using ShadowDisk.Core.Model;
using ShadowDisk.Core.Planning;

namespace ShadowDisk.App;

/// <summary>
/// The final confirmation before a real disk change. Shows exactly which disk and what will change,
/// lists the risks, and only enables the button after the user ticks the backup box AND types the
/// confirmation word. Cancel is the default button.
/// </summary>
public sealed class ConfirmChangeWindow : Window
{
    /// <summary>
    /// The word the user must type. On a real disk, resizing a volume with a drive letter asks for the
    /// letter itself (e.g. "E"), so the user has to look at which drive they are changing.
    /// </summary>
    public static (string Word, string Button) WordsFor(PlanKind kind, bool isPractice, string? driveLetter) => kind switch
    {
        PlanKind.ShrinkVolume when !isPractice && driveLetter is { Length: > 0 } l => (l, $"Shrink {l}:"),
        PlanKind.ExtendVolume when !isPractice && driveLetter is { Length: > 0 } l => (l, $"Extend {l}:"),
        PlanKind.ShrinkVolume => ("SHRINK", "Shrink volume"),
        PlanKind.ExtendVolume => ("EXTEND", "Extend volume"),
        _ => ("CREATE", "Create volume"),
    };

    public ConfirmChangeWindow(PhysicalDisk disk, PlanResult plan, bool isPractice)
    {
        var letter = plan.Request.Partition?.Volume?.DriveLetter;
        var (confirmWord, buttonText) = WordsFor(plan.Request.Kind, isPractice, letter);
        Title = "Confirm a real disk change";
        Width = 620;
        SizeToContent = SizeToContent.Height;
        MaxHeight = SystemParameters.WorkArea.Height - 40;
        ResizeMode = ResizeMode.NoResize;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        FontFamily = new FontFamily("Segoe UI");
        FontSize = 13;

        var panel = new StackPanel { Margin = new Thickness(20) };

        panel.Children.Add(new Border
        {
            Background = new SolidColorBrush(Color.FromRgb(0xFE, 0xE2, 0xE2)),
            BorderBrush = new SolidColorBrush(Color.FromRgb(0xB9, 0x1C, 0x1C)),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(4),
            Padding = new Thickness(10, 8, 10, 8),
            Margin = new Thickness(0, 0, 0, 14),
            Child = new TextBlock
            {
                Text = isPractice
                    ? "This is a REAL change. Unlike a preview, it will write to the practice disk below."
                    : $"This is a REAL change to your REAL disk ({disk.FriendlyName}), not a practice disk. Unlike a preview, it will write to it.",
                FontWeight = FontWeights.SemiBold,
                Foreground = new SolidColorBrush(Color.FromRgb(0x7F, 0x1D, 0x1D)),
                TextWrapping = TextWrapping.Wrap,
            },
        });

        panel.Children.Add(Heading("Disk"));
        panel.Children.Add(Body($"Disk {disk.Number} — {disk.FriendlyName}, {ByteSize.FormatWithExact(disk.SizeBytes)}"));
        if (disk.Location is { } location)
            panel.Children.Add(Body($"Practice disk file: {System.IO.Path.GetFileName(location)}", muted: true));

        panel.Children.Add(Heading("What will change"));
        panel.Children.Add(Body(plan.Summary));

        panel.Children.Add(Heading("What could go wrong"));
        foreach (var risk in plan.Risks) panel.Children.Add(Body("• " + risk, muted: true));
        panel.Children.Add(Body("• If a step fails part-way, ShadowDisk stops, does not retry and does not try to undo anything. It reads the disk again and shows you its real state.", muted: true));

        var backup = new CheckBox
        {
            Content = new TextBlock
            {
                Text = isPractice
                    ? "This is a practice disk, or I have a backup of everything on this disk and have checked that it opens."
                    : $"I have a backup of everything on {(letter is { } l2 ? l2 + ":" : "this disk")} somewhere else, and I have checked that the backup opens.",
                TextWrapping = TextWrapping.Wrap,
            },
            Margin = new Thickness(0, 14, 0, 10),
        };
        panel.Children.Add(backup);

        panel.Children.Add(Body($"Type {confirmWord} to confirm:"));
        var confirm = new TextBox { Width = 160, HorizontalAlignment = HorizontalAlignment.Left, Padding = new Thickness(4, 3, 4, 3), Margin = new Thickness(0, 4, 0, 16) };
        System.Windows.Automation.AutomationProperties.SetName(confirm, $"Type {confirmWord} to confirm");
        panel.Children.Add(confirm);

        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        var cancel = new Button { Content = "Cancel", IsCancel = true, IsDefault = true, MinWidth = 100, Padding = new Thickness(12, 5, 12, 5), Margin = new Thickness(0, 0, 8, 0) };
        var apply = new Button
        {
            Content = buttonText,
            MinWidth = 140,
            Padding = new Thickness(12, 5, 12, 5),
            IsEnabled = false,
            Background = new SolidColorBrush(Color.FromRgb(0xB9, 0x1C, 0x1C)),
            Foreground = Brushes.White,
        };
        cancel.Click += (_, _) => DialogResult = false;
        apply.Click += (_, _) => DialogResult = true;

        void Update() => apply.IsEnabled = backup.IsChecked == true && confirm.Text.Trim() == confirmWord;
        backup.Checked += (_, _) => Update();
        backup.Unchecked += (_, _) => Update();
        confirm.TextChanged += (_, _) => Update();

        buttons.Children.Add(cancel);
        buttons.Children.Add(apply);
        panel.Children.Add(buttons);

        Content = new ScrollViewer { VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Content = panel };
        Loaded += (_, _) => cancel.Focus();
    }

    private static TextBlock Heading(string text) =>
        new() { Text = text, FontWeight = FontWeights.SemiBold, FontSize = 14, Margin = new Thickness(0, 8, 0, 4) };

    private static TextBlock Body(string text, bool muted = false) =>
        new()
        {
            Text = text,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 1, 0, 1),
            Foreground = muted ? new SolidColorBrush(Color.FromRgb(0x4B, 0x55, 0x63)) : Brushes.Black,
        };
}
