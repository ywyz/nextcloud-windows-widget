using System.Net;
using System.Security.Cryptography;
using System.Text;

namespace NextcloudWidget.Core.Translation;

public sealed class TranslationOptions
{
    public Uri BaseUrl { get; }
    public string Model { get; }
    public string TargetLanguage { get; }
    public const string DefaultLanguage = "简体中文";

    public TranslationOptions(string endpoint, string model, string targetLanguage)
    {
        if (!Uri.TryCreate(endpoint.Trim(), UriKind.Absolute, out var uri) ||
            uri.Host.Length == 0 || uri.UserInfo.Length != 0 || uri.Query.Length != 0 || uri.Fragment.Length != 0 ||
            (uri.Scheme != Uri.UriSchemeHttps && !(uri.Scheme == Uri.UriSchemeHttp && IsLocal(uri))))
            throw new ArgumentException("请输入 HTTPS API 基址。本机服务也可以使用 HTTP localhost 或回环 IP。");
        string path = uri.GetLeftPart(UriPartial.Path).TrimEnd('/');
        if (path.EndsWith("/chat/completions", StringComparison.Ordinal))
            path = path[..^"/chat/completions".Length];
        if (uri.AbsolutePath == "/") path += "/v1";
        BaseUrl = new Uri(path + "/");
        Model = Required(model, "模型名称");
        TargetLanguage = Required(targetLanguage, "目标语言");
    }

    private static string Required(string value, string label)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Any(char.IsControl))
            throw new ArgumentException($"请填写有效的{label}。");
        return value.Trim();
    }

    internal static bool IsLocal(Uri uri) =>
        uri.IdnHost.Equals("localhost", StringComparison.OrdinalIgnoreCase) ||
        (IPAddress.TryParse(uri.DnsSafeHost, out var address) && IPAddress.IsLoopback(address));
}

public interface ITranslationKeys
{
    string? Read(TranslationOptions options);
    void Save(TranslationOptions options, string? key);
}

public sealed class TranslationKeys(string profileDirectory) : ITranslationKeys
{
    private readonly WindowsDeviceSecrets secrets = new();
    private readonly string profile = Path.TrimEndingDirectorySeparator(Path.GetFullPath(profileDirectory)).ToUpperInvariant();
    private Guid Id(TranslationOptions options)
    {
        // Bind the credential to the profile and the entire canonical API base, including the port/path.
        var input = Encoding.UTF8.GetBytes("translation-key\0" + profile + "\0" + options.BaseUrl.AbsoluteUri);
        return new Guid(SHA256.HashData(input).AsSpan(0, 16));
    }

    public string? Read(TranslationOptions options) => secrets.Read(Id(options));
    public void Save(TranslationOptions options, string? key)
    {
        if (string.IsNullOrWhiteSpace(key)) secrets.Delete(Id(options));
        else
        {
            if (key.Any(char.IsControl)) throw new ArgumentException("API Key 不能包含控制字符。");
            secrets.Write(Id(options), options.BaseUrl.GetLeftPart(UriPartial.Authority), key.Trim());
        }
    }
}
