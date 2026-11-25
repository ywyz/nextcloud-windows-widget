using System.Text.RegularExpressions;
using System.Web;

namespace NextcloudWidget.Core.Mail;

public enum MailImageKind { Embedded, Attachment, Remote }

public sealed record MailImageSource(MailImageKind Kind, string Value);

public static class MailImages
{
    private const int MaxBytes = 8 * 1024 * 1024;
    private static readonly HttpClient Anonymous = new(new HttpClientHandler { UseCookies = false, AllowAutoRedirect = true })
        { Timeout = TimeSpan.FromSeconds(20) };

    internal static bool ValidPart(string part) => part.Length <= 80 &&
        Regex.IsMatch(part, @"\A[0-9]+(?:\.[0-9]+)*\z", RegexOptions.CultureInvariant | RegexOptions.NonBacktracking);

    internal static Uri AttachmentUrl(Uri server, string id, string part)
    {
        if (id.Length == 0 || !id.All(char.IsAsciiDigit) || !ValidPart(part))
            throw new ArgumentException("无效的邮件附件编号。");
        return new Uri(Authorization.BaseUri(server), $"index.php/apps/mail/api/messages/{id}/attachment/{part}");
    }

    public static MailImageSource? Resolve(string source, Uri server, string id)
    {
        if (source.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
        {
            var comma = source.IndexOf(',');
            if (comma <= 0 || source.Length - comma > (MaxBytes + 2L) / 3 * 4 + 1) return null;
            return source[..comma].ToLowerInvariant() is "data:image/png;base64" or "data:image/jpeg;base64"
                or "data:image/gif;base64" or "data:image/webp;base64" or "data:image/bmp;base64"
                ? new(MailImageKind.Embedded, source[(comma + 1)..]) : null;
        }
        var root = Authorization.BaseUri(server);
        if (!Uri.TryCreate(root, source, out var uri) || uri.Scheme is not ("https" or "http") || uri.UserInfo.Length != 0)
            return null;
        if (Authorization.SameOrigin(root, uri) && uri.AbsolutePath.StartsWith(root.AbsolutePath, StringComparison.Ordinal))
        {
            var path = uri.AbsolutePath[root.AbsolutePath.Length..];
            if (path.StartsWith("index.php/", StringComparison.Ordinal)) path = path["index.php/".Length..];
            var prefix = $"apps/mail/api/messages/{id}/attachment/";
            if (path.StartsWith(prefix, StringComparison.Ordinal) && ValidPart(path[prefix.Length..]))
                return new(MailImageKind.Attachment, path[prefix.Length..]);
            // Nextcloud rewrites remote image URLs through its cookie-bound web proxy.
            // A desktop reader can request the original with a separate, anonymous client.
            if (path == "apps/mail/proxy")
            {
                var target = HttpUtility.ParseQueryString(uri.Query)["src"];
                if (!Uri.TryCreate(target, UriKind.Absolute, out uri)) return null;
            }
            else if (path.StartsWith("apps/mail/api/", StringComparison.Ordinal) || path.StartsWith("ocs/", StringComparison.Ordinal))
                return null;
        }
        return uri.Scheme is "http" or "https" && uri.UserInfo.Length == 0
            ? new(MailImageKind.Remote, uri.AbsoluteUri) : null;
    }

    public static Task<byte[]> DownloadRemoteAsync(string url, CancellationToken ct) =>
        DownloadAsync(Anonymous, new Uri(url), ct);

    internal static async Task<byte[]> DownloadAsync(HttpClient client, Uri uri, CancellationToken ct, bool attachment = false)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(20));
        ct = timeout.Token;
        using var request = new HttpRequestMessage(HttpMethod.Get, uri);
        request.Headers.Accept.ParseAdd("image/*");
        if (attachment) request.Headers.Add("OCS-APIRequest", "true");
        using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        // MIME transfer encoding can add 4/3 expansion plus folded line endings.
        var wireLimit = attachment ? MaxBytes * 3 / 2 : MaxBytes;
        if (response.Content.Headers.ContentLength > wireLimit) throw new InvalidDataException("图片超过大小限制。");
        using var stream = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        using var image = new MemoryStream();
        var buffer = new byte[32768];
        int count;
        while ((count = await stream.ReadAsync(buffer, ct).ConfigureAwait(false)) > 0)
        {
            if (image.Length + count > wireLimit) throw new InvalidDataException("图片超过大小限制。");
            image.Write(buffer, 0, count);
        }
        var bytes = image.ToArray();
        // Some servers label inline photos application/octet-stream. Check the container
        // signature before caching or handing it to the native raster decoder.
        if (bytes.Length <= MaxBytes && IsRaster(bytes)) return bytes;
        // Some IMAP servers leave a related part's Content-Transfer-Encoding in
        // place even on Nextcloud's binary attachment route. Accept it only here.
        if (attachment && !IsRaster(bytes))
        {
            var text = System.Text.Encoding.ASCII.GetString(bytes);
            var decoded = new byte[Math.Min(MaxBytes, bytes.Length * 3 / 4)];
            if (Convert.TryFromBase64String(text, decoded, out int size) && IsRaster(decoded.AsSpan(0, size)))
                return decoded[..size];
        }
        throw new InvalidDataException("图片过大或格式不受支持。");
    }

    public static bool IsRaster(ReadOnlySpan<byte> bytes) =>
        bytes.StartsWith(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 }) ||
        bytes.StartsWith(new byte[] { 255, 216, 255 }) || bytes.StartsWith("GIF87a"u8) ||
        bytes.StartsWith("GIF89a"u8) || bytes.StartsWith("BM"u8) ||
        (bytes.Length >= 12 && bytes.StartsWith("RIFF"u8) && bytes[8..].StartsWith("WEBP"u8));
}
