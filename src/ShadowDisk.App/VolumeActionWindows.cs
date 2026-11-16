using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace ShadowDisk.App;

/// <summary>
/// Final confirmation for deleting a volume. The button only enables after the backup box is ticked and
/// the user types the exact confirmation text (e.g. "DELETE G"). Cancel is the default.
/// </summary>
public sealed class ConfirmDeleteWindow : Window
{
    public ConfirmDeleteWindow(string heading, IEnumerable<string> facts, string confirmText, string checkboxText,
        string title = "Confirm deleting a volume", string buttonText = "Delete volume")
    {
        Title = title;
        Width = 600;
        SizeToContent = SizeToContent.Height;
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
            Margin = new Thickness(0, 0, 0, 12),
            Child = new TextBlock { Text = heading, FontWeight = FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap,
                                    Foreground = new SolidColorBrush(Color.FromRgb(0x7F, 0x1D, 0x1D)) },
        });
        foreach (var fact in facts)
            panel.Children.Add(new TextBlock { Text = fact, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 2, 0, 2) });

        var check = new CheckBox { Content = new TextBlock { Text = checkboxText, TextWrapping = TextWrapping.Wrap }, Margin = new Thickness(0, 14, 0, 10) };
        panel.Children.Add(check);
        panel.Children.Add(new TextBlock { Text = $"Type {confirmText} to confirm:" });
        var box = new TextBox { Width = 180, HorizontalAlignment = HorizontalAlignment.Left, Padding = new Thickness(4, 3, 4, 3), Margin = new Thickness(0, 4, 0, 16) };
        System.Windows.Automation.AutomationProperties.SetName(box, $"Type {confirmText} to confirm");
        panel.Children.Add(box);

        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        var cancel = new Button { Content = "Cancel", IsCancel = true, IsDefault = true, MinWidth = 100, Padding = new Thickness(12, 5, 12, 5), Margin = new Thickness(0, 0, 8, 0) };
        var ok = new Button { Content = buttonText, MinWidth = 140, Padding = new Thickness(12, 5, 12, 5), IsEnabled = false,
                              Background = new SolidColorBrush(Color.FromRgb(0xB9, 0x1C, 0x1C)), Foreground = Brushes.White };
        cancel.Click += (_, _) => DialogResult = false;
        ok.Click += (_, _) => DialogResult = true;
        void Update() => ok.IsEnabled = check.IsChecked == true && string.Equals(box.Text.Trim(), confirmText, StringComparison.OrdinalIgnoreCase);
        check.Checked += (_, _) => Update();
        check.Unchecked += (_, _) => Update();
        box.TextChanged += (_, _) => Update();
        buttons.Children.Add(cancel);
        buttons.Children.Add(ok);
        panel.Children.Add(buttons);
        Content = panel;
        Loaded += (_, _) => cancel.Focus();
    }
}

/// <summary>Lets the user pick a free drive letter (D–Z, not used by any drive Windows knows about).</summary>
public sealed class ChooseLetterWindow : Window
{
    private readonly ComboBox _letters = new() { Width = 120 };

    public string? ChosenLetter => _letters.SelectedItem as string;

    public static IReadOnlyList<string> FreeLetters()
    {
        var used = DriveInfo.GetDrives().Select(d => char.ToUpperInvariant(d.Name[0])).ToHashSet();
        return Enumerable.Range('D', 'Z' - 'D' + 1).Select(c => (char)c).Where(c => !used.Contains(c)).Select(c => c.ToString()).ToList();
    }

    public ChooseLetterWindow(string volumeName, string? currentLetter, string? suggested = null)
    {
        Title = "Change drive letter";
        Width = 420;
        SizeToContent = SizeToContent.Height;
        ResizeMode = ResizeMode.NoResize;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        FontFamily = new FontFamily("Segoe UI");
        FontSize = 13;

        var panel = new StackPanel { Margin = new Thickness(20) };
        panel.Children.Add(new TextBlock
        {
            Text = $"{volumeName} currently has {(currentLetter is { } c ? $"the letter {c}:" : "no drive letter")}. " +
                   "Choose a new letter. Files on the volume are not touched, but shortcuts or programs that use the old letter will need updating.",
            TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 12),
        });
        var free = FreeLetters();
        _letters.ItemsSource = free;
        _letters.SelectedItem = suggested is { } s && free.Contains(s) ? s : free.FirstOrDefault();
        var row = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 16) };
        row.Children.Add(new TextBlock { Text = "New letter:", VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 8, 0) });
        row.Children.Add(_letters);
        panel.Children.Add(row);

        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        var cancel = new Button { Content = "Cancel", IsCancel = true, MinWidth = 90, Padding = new Thickness(12, 5, 12, 5), Margin = new Thickness(0, 0, 8, 0) };
        var ok = new Button { Content = "Change letter", IsDefault = true, MinWidth = 120, Padding = new Thickness(12, 5, 12, 5), IsEnabled = free.Count > 0 };
        cancel.Click += (_, _) => DialogResult = false;
        ok.Click += (_, _) => DialogResult = ChosenLetter is not null;
        buttons.Children.Add(cancel);
        buttons.Children.Add(ok);
        panel.Children.Add(buttons);
        Content = panel;
    }
}
