using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace ShadowDisk.App;

/// <summary>
/// The app's look: the "Shadow" theme (default, night blue with the AIShadow electric blue and ember orange)
/// or "Light". The choice is remembered in %LOCALAPPDATA%\ShadowDisk\settings.json.
/// Motion follows Windows' own "Show animations in Windows" setting.
/// </summary>
public static class Theme
{
    public enum Kind { Shadow, Light }

    public static Kind Current { get; private set; } = Kind.Shadow;
    public static bool IsDark => Current == Kind.Shadow;

    /// <summary>Raised after the theme changed, so windows can redraw what they paint themselves.</summary>
    public static event Action? Changed;

    /// <summary>Animations are shown only when Windows' "Show animations" setting is on.</summary>
    public static bool Motion => SystemParameters.ClientAreaAnimation;

    private static string SettingsFile => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ShadowDisk", "settings.json");

    /// <summary>Loads the remembered theme (call once at start-up).</summary>
    public static void Load()
    {
        try
        {
            if (File.Exists(SettingsFile) &&
                JsonSerializer.Deserialize<Settings>(File.ReadAllText(SettingsFile)) is { Theme: "Light" })
            {
                Set(Kind.Light, save: false);
            }
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            // A missing or unreadable settings file just means the default theme.
        }
    }

    public static void Set(Kind kind, bool save = true)
    {
        var dictionaries = Application.Current.Resources.MergedDictionaries;
        var uri = new Uri($"pack://application:,,,/Themes/{kind}.xaml", UriKind.Absolute);
        if (dictionaries.Count > 0) dictionaries[0] = new ResourceDictionary { Source = uri };
        else dictionaries.Insert(0, new ResourceDictionary { Source = uri });
        Current = kind;
        if (save)
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(SettingsFile)!);
                File.WriteAllText(SettingsFile, JsonSerializer.Serialize(new Settings { Theme = kind.ToString() }));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }
        Changed?.Invoke();
    }

    public static void Toggle() => Set(IsDark ? Kind.Light : Kind.Shadow);

    /// <summary>
    /// Gives a code-built window the theme's background and text colours (updates if the theme changes),
    /// and lets its content zoom gently into place when it opens.
    /// </summary>
    public static void Apply(Window window)
    {
        window.SetResourceReference(Control.BackgroundProperty, "WindowBrush");
        window.SetResourceReference(Control.ForegroundProperty, "TextBrush");
        window.Loaded += (_, _) =>
        {
            if (window.Content is FrameworkElement content) PopIn(content);
        };
    }

    // ===== Motion helpers (all do nothing when Windows' "Show animations" is off) =====

    private static IEasingFunction EaseOut => new CubicEase { EasingMode = EasingMode.EaseOut };

    /// <summary>Fades an element in while it rises <paramref name="distance"/> pixels into place.</summary>
    public static void FadeUp(UIElement element, int delayMs = 0, double distance = 16, int durationMs = 450)
    {
        if (!Motion) return;
        var slide = element.RenderTransform as TranslateTransform;
        if (slide is null || slide.IsFrozen)
        {
            slide = new TranslateTransform();
            element.RenderTransform = slide;
        }
        var begin = TimeSpan.FromMilliseconds(delayMs);
        var duration = TimeSpan.FromMilliseconds(durationMs);
        element.Opacity = 0;
        element.BeginAnimation(UIElement.OpacityProperty,
            new DoubleAnimation(0, 1, duration) { BeginTime = begin, EasingFunction = EaseOut });
        slide.BeginAnimation(TranslateTransform.YProperty,
            new DoubleAnimation(distance, 0, duration) { BeginTime = begin, EasingFunction = EaseOut });
    }

    /// <summary>Fades an element in while it slides <paramref name="distance"/> pixels sideways into place.</summary>
    public static void SlideIn(UIElement element, double distance, int delayMs = 0, int durationMs = 550)
    {
        if (!Motion) return;
        var slide = new TranslateTransform();
        element.RenderTransform = slide;
        var begin = TimeSpan.FromMilliseconds(delayMs);
        var duration = TimeSpan.FromMilliseconds(durationMs);
        element.Opacity = 0;
        element.BeginAnimation(UIElement.OpacityProperty,
            new DoubleAnimation(0, 1, duration) { BeginTime = begin, EasingFunction = EaseOut });
        slide.BeginAnimation(TranslateTransform.XProperty,
            new DoubleAnimation(distance, 0, duration) { BeginTime = begin, EasingFunction = new BackEase { EasingMode = EasingMode.EaseOut, Amplitude = 0.3 } });
    }

    /// <summary>Zooms an element from 96% to full size while fading in (dialogs opening).</summary>
    public static void PopIn(FrameworkElement element)
    {
        if (!Motion) return;
        var scale = new ScaleTransform(0.96, 0.96);
        element.RenderTransformOrigin = new Point(0.5, 0.5);
        element.RenderTransform = scale;
        var duration = TimeSpan.FromMilliseconds(260);
        element.BeginAnimation(UIElement.OpacityProperty, new DoubleAnimation(0, 1, duration) { EasingFunction = EaseOut });
        scale.BeginAnimation(ScaleTransform.ScaleXProperty, new DoubleAnimation(0.96, 1, duration) { EasingFunction = EaseOut });
        scale.BeginAnimation(ScaleTransform.ScaleYProperty, new DoubleAnimation(0.96, 1, duration) { EasingFunction = EaseOut });
    }

    /// <summary>A quick brightening flash (e.g. a scan cell that was just read).</summary>
    public static void Flash(UIElement element)
    {
        if (!Motion) return;
        element.BeginAnimation(UIElement.OpacityProperty, new DoubleAnimation(0.25, 1, TimeSpan.FromMilliseconds(450)) { EasingFunction = EaseOut });
    }

    /// <summary>Each child of a panel fades up one after another.</summary>
    public static void Cascade(Panel panel, int startDelayMs = 0, int stepMs = 70)
    {
        if (!Motion) return;
        var delay = startDelayMs;
        foreach (UIElement child in panel.Children)
        {
            if (child.Visibility != Visibility.Visible) continue;
            FadeUp(child, delay);
            delay += stepMs;
        }
    }

    public static Brush Get(string key) => Application.Current.TryFindResource(key) as Brush ?? Brushes.Gray;

    public static Brush Text => Get("TextBrush");
    public static Brush Muted => Get("MutedText");
    public static Brush Danger => Get("DangerBrush");
    public static Brush Success => Get("SuccessBrush");
    public static Brush Warning => Get("WarningBrush");

    public static Style? StyleFor(string key) => Application.Current.TryFindResource(key) as Style;

    // Colours as text, for view models that bind a colour name (readable on both themes).
    public static string DangerHex => IsDark ? "#F87171" : "#B91C1C";
    public static string WarningHex => IsDark ? "#FBBF24" : "#B45309";
    public static string SuccessHex => IsDark ? "#34D399" : "#047857";
    public static string InfoHex => IsDark ? "#60A5FA" : "#1D4ED8";
    public static string MutedHex => IsDark ? "#94A3B8" : "#6B7280";

    private sealed class Settings
    {
        public string? Theme { get; set; }
    }
}
