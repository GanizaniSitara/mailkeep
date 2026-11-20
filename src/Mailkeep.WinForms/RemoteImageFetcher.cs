using System.Net;
using System.Net.Http;

namespace Mailkeep.WinForms;

/// <summary>Fetches a letter's remote pictures and writes them into the page, so the pane shows a letter
/// whose pictures are its own. The program does the fetching, as the client this is modelled on did: the
/// page's runtime is a separate program with its own standing at the firewall, and on a machine that lets
/// only named programs out it may be refused where this one is allowed. Only pictures from the sites the
/// reader allowed are fetched; anything else stays blocked by the page's own policy.</summary>
internal static class RemoteImageFetcher
{
    private static readonly HttpClient Client = new(new HttpClientHandler { AllowAutoRedirect = false })
    {
        Timeout = TimeSpan.FromSeconds(12)
    };
    private const long MaxBytes = 8 * 1024 * 1024;
    private sealed record CachedImage(string Data, string[] Origins);
    private static readonly Dictionary<string, CachedImage> Cache = new(StringComparer.Ordinal);

    static RemoteImageFetcher()
    {
        Client.DefaultRequestHeaders.UserAgent.ParseAdd("Mozilla/5.0 (Windows NT 10.0; Win64; x64) Mailkeep/1.0");
        Client.DefaultRequestHeaders.Accept.ParseAdd("image/*,*/*;q=0.5");
    }

    /// <summary>The HTML with every allowed remote picture inlined as data; one that cannot be fetched is
    /// left as it was, for the page's policy to deal with.</summary>
    public static async Task<string> InlineAsync(string html, IReadOnlyCollection<string> allowedOrigins, CancellationToken cancellationToken = default,
        IProgress<string>? progress = null)
    {
        var allowed = allowedOrigins.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var urls = RemoteImages.Urls(html)
            .Where(url => RemoteImages.TryOrigin(url, out var origin) && allowed.Contains(origin)).ToArray();
        if (urls.Length == 0) return html;

        var fetched = new Dictionary<string, string>(StringComparer.Ordinal);
        using var slots = new SemaphoreSlim(6);
        await Task.WhenAll(urls.Select(async url =>
        {
            await slots.WaitAsync(cancellationToken);
            try
            {
                var data = await FetchAsync(url, allowed, cancellationToken);
                if (data is not null)
                    lock (fetched)
                    {
                        fetched[url] = data;
                        progress?.Report(Render());
                    }
            }
            finally { slots.Release(); }
        }));
        if (fetched.Count == 0) return html;

        return Render();

        // Replace the attribute text as written, encoded ampersands and all, so nothing else on the page moves.
        string Render()
        {
            var result = html;
            foreach (var (url, data) in fetched.OrderByDescending(pair => pair.Key.Length))
            {
                result = result.Replace(url, data, StringComparison.Ordinal);
                var encoded = WebUtility.HtmlEncode(url);
                if (encoded != url) result = result.Replace(encoded, data, StringComparison.Ordinal);
            }
            return result;
        }
    }

    private static async Task<string?> FetchAsync(string url, IReadOnlySet<string> allowed, CancellationToken cancellationToken)
    {
        lock (Cache) if (Cache.TryGetValue(url, out var cached) && cached.Origins.All(allowed.Contains)) return cached.Data;
        try
        {
            var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var current = new Uri(url);
            for (var redirects = 0; redirects <= 5; redirects++)
            {
                if (!RemoteImages.TryOrigin(current.AbsoluteUri, out var origin) || !allowed.Contains(origin)) return null;
                visited.Add(origin);
                using var response = await Client.GetAsync(current, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
                if ((int)response.StatusCode is 301 or 302 or 303 or 307 or 308)
                {
                    if (response.Headers.Location is not { } location) return null;
                    var next = location.IsAbsoluteUri ? location : new Uri(current, location);
                    if (current.Scheme == "https" && next.Scheme == "http") return null;
                    current = next;
                    continue;
                }
                if (!response.IsSuccessStatusCode) return null;
                var type = response.Content.Headers.ContentType?.MediaType ?? "";
                if (!type.StartsWith("image/", StringComparison.OrdinalIgnoreCase)) return null;
                if (response.Content.Headers.ContentLength is > MaxBytes) return null;
                await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
                using var buffer = new MemoryStream();
                await stream.CopyToAsync(buffer, cancellationToken);
                if (buffer.Length > MaxBytes) return null;
                var data = "data:" + type + ";base64," + Convert.ToBase64String(buffer.ToArray());
                lock (Cache) Cache[url] = new(data, visited.ToArray());
                return data;
            }
            return null;
        }
        catch (Exception) { return null; }
    }

}
