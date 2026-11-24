using System.Globalization;
using System.Text.Json;

namespace NextcloudWidget.Updates;

public sealed class ReleaseInfo
{
    public Version Version { get; }
    public long Size { get; }
    public string Sha256 { get; }
    internal const string Product = "Nextcloud Windows Widget";
    internal const string Asset = "NextcloudWidget-win-x64.exe";
    internal const string Repository = "https://github.com/ywyz/nextcloud-windows-widget";
    internal string DownloadUrl => $"{Repository}/releases/download/v{Version}/{Asset}";

    private ReleaseInfo(Version version, long size, string sha256) =>
        (Version, Size, Sha256) = (version, size, sha256);

    internal static ReleaseInfo Parse(byte[] bytes)
    {
        using var json = JsonDocument.Parse(bytes);
        var root = json.RootElement;
        string version = root.GetProperty("version").GetString() ?? "";
        if (root.GetProperty("product").GetString() != Product ||
            root.GetProperty("asset").GetString() != Asset ||
            !Version.TryParse(version, out var number) || number.Build < 0 ||
            number.Revision != -1 || number.ToString() != version)
            throw new InvalidDataException("更新清单的产品或版本不正确。");
        long size = root.GetProperty("size").GetInt64();
        string hash = root.GetProperty("sha256").GetString() ?? "";
        if (size < 1 || size > 512L * 1024 * 1024 || hash.Length != 64 || !hash.All(Uri.IsHexDigit))
            throw new InvalidDataException("更新清单中的大小或校验值无效。");
        return new ReleaseInfo(number, size, hash);
    }
}
