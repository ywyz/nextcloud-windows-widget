using System.Text.Json;

namespace NextcloudWidget.App;

internal sealed class Preferences
{
    private readonly string path;
    internal string DirectoryPath => Path.GetDirectoryName(path)!;
    public sealed record Values(bool Pinned = false, double? Left = null, double? Top = null,
        double? Width = null, double? Height = null, DateTimeOffset? LastUpdateCheck = null,
        bool DarkMode = false, string? LastTaskList = null);
    public Values State { get; private set; }

    public Preferences(string directory)
    {
        path = Path.Combine(directory, "window.json");
        try { State = JsonSerializer.Deserialize<Values>(File.ReadAllText(path)) ?? new(); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException) { State = new(); }
    }

    public void Set(Values value)
    {
        State = value;
        try
        {
            File.WriteAllText(path + ".tmp", JsonSerializer.Serialize(value));
            File.Move(path + ".tmp", path, true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Preferences are cosmetic. Read-only profiles may still open cached data.
        }
    }
}
