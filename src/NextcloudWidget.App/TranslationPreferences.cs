using System.Text.Json;
using NextcloudWidget.Core.Translation;

namespace NextcloudWidget.App;

internal sealed class TranslationPreferences(string directory, ITranslationKeys? keyStore = null)
{
    private readonly string path = Path.Combine(directory, "translation.json");
    internal ITranslationKeys Keys { get; } = keyStore ?? new TranslationKeys(directory);
    private sealed record Stored(string Endpoint, string Model, string Language);

    internal TranslationOptions? Load()
    {
        if (!File.Exists(path)) return null;
        var stored = JsonSerializer.Deserialize<Stored>(File.ReadAllBytes(path));
        if (stored?.Endpoint == null || stored.Model == null || stored.Language == null)
            throw new InvalidDataException("翻译设置格式无效，请重新保存。");
        return new TranslationOptions(stored.Endpoint, stored.Model, stored.Language);
    }

    // An empty input leaves an existing key for this exact endpoint intact.
    internal void Save(TranslationOptions options, string? newKey, bool removeKey)
    {
        Directory.CreateDirectory(directory);
        string temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        bool changeKey = removeKey || !string.IsNullOrWhiteSpace(newKey);
        try
        {
            File.WriteAllBytes(temporary, JsonSerializer.SerializeToUtf8Bytes(
                new Stored(options.BaseUrl.AbsoluteUri, options.Model, options.TargetLanguage)));
            string? previous = null;
            if (changeKey)
            {
                previous = Keys.Read(options);
                Keys.Save(options, removeKey ? null : newKey);
            }
            try { File.Move(temporary, path, true); }
            catch
            {
                if (changeKey) Keys.Save(options, previous);
                throw;
            }
        }
        finally { File.Delete(temporary); }
    }
}
