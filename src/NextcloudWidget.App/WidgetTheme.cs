using System.Windows.Media;

namespace NextcloudWidget.App;

internal static class WidgetTheme
{
    private static readonly (string Key, string Day, string Night)[] Palette =
    [
        ("CanvasBrush", "#F5F7F9", "#151B20"),
        ("SurfaceBrush", "#FFFFFF", "#20292F"),
        ("TextBrush", "#1E3037", "#EEF4F6"),
        ("MutedBrush", "#546970", "#ADBDC4"),
        ("StrokeBrush", "#C8D5DA", "#46575F"),
        ("AccentBrush", "#036A83", "#83DAEE"),
        ("AccentTextBrush", "#FFFFFF", "#063640"),
        ("HoverBrush", "#EDF6F8", "#2D3B43"),
        ("SelectedBrush", "#D6EDF3", "#314F5A"),
        ("DangerBrush", "#AA353E", "#FF9EAA")
    ];

    internal static void Apply(bool dark)
    {
        var app = Application.Current;
        // The Windows Desktop SDK marks its programmatic Fluent switch experimental.
        // Fluent provides matching native templates for popups, date pickers and focus states.
#pragma warning disable WPF0001
        app.ThemeMode = dark ? ThemeMode.Dark : ThemeMode.Light;
#pragma warning restore WPF0001
        foreach (var (key, day, night) in Palette)
        {
            var brush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(dark ? night : day));
            brush.Freeze();
            app.Resources[key] = brush;
        }
    }
}
