using System.Net;

namespace Cove.Extensions.CommunityDownloaders;

/// <summary>
/// Keeps a browsing session for reddit.com. Reddit refuses its <c>.json</c> endpoints to clients that arrive
/// without one — the very same request answers 403 cold and returns JSON once the cookies a browser picks up
/// (loid, edgebucket, …) are present. Fetching Reddit's front page once yields those cookies, so the plain
/// HTTP path keeps working without an API app or a registered client. Reddit asks for a descriptive
/// User-Agent rather than a browser's, and that is what it gets: only the cookies decide the outcome.
/// </summary>
internal sealed class RedditSession : IDisposable
{
    private const string SeedUrl = "https://old.reddit.com/";
    private static readonly TimeSpan SeedLifetime = TimeSpan.FromMinutes(30);

    private readonly HttpClient _client;
    private readonly SemaphoreSlim _seedLock = new(1, 1);
    private DateTimeOffset _seededAt = DateTimeOffset.MinValue;

    public RedditSession(string userAgent)
    {
        _client = new HttpClient(new HttpClientHandler
        {
            CookieContainer = new CookieContainer(),
            UseCookies = true,
            AutomaticDecompression = DecompressionMethods.All,
        })
        {
            Timeout = TimeSpan.FromSeconds(60),
        };
        // Reddit asks for "platform:app-id:version (by /u/name)", which strict User-Agent parsing rejects, so it is
        // added verbatim.
        _client.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent", userAgent);
        // Reddit refuses the session cookie (loid) to requests that do not ask for content the way a browser
        // does; without these the seed answers 403 and every later request does too.
        _client.DefaultRequestHeaders.TryAddWithoutValidation("Accept", "text/html,application/xhtml+xml,application/xml;q=0.9,*/*;q=0.8");
        _client.DefaultRequestHeaders.TryAddWithoutValidation("Accept-Language", "en-US,en;q=0.9");
    }

    /// <summary>
    /// Sends a request with the session's cookies, re-seeding once and retrying when Reddit turns it away —
    /// which is what an expired or missing session looks like. <paramref name="createRequest"/> is a factory
    /// because a request message cannot be sent twice.
    /// </summary>
    public async Task<HttpResponseMessage> SendAsync(
        Func<HttpRequestMessage> createRequest,
        HttpCompletionOption completionOption,
        CancellationToken ct)
    {
        await EnsureSeededAsync(force: false, ct);
        var response = await _client.SendAsync(createRequest(), completionOption, ct);
        if (response.StatusCode is not (HttpStatusCode.Forbidden or HttpStatusCode.Unauthorized))
            return response;

        response.Dispose();
        await EnsureSeededAsync(force: true, ct);
        return await _client.SendAsync(createRequest(), completionOption, ct);
    }

    private async Task EnsureSeededAsync(bool force, CancellationToken ct)
    {
        if (!force && DateTimeOffset.UtcNow - _seededAt < SeedLifetime)
            return;

        await _seedLock.WaitAsync(ct);
        try
        {
            if (!force && DateTimeOffset.UtcNow - _seededAt < SeedLifetime)
                return;

            using var seed = await _client.GetAsync(SeedUrl, HttpCompletionOption.ResponseHeadersRead, ct);
            // Even a refused seed leaves the clock moving, so a site-wide block cannot turn every request into two.
            _seededAt = DateTimeOffset.UtcNow;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _seededAt = DateTimeOffset.UtcNow;
        }
        finally
        {
            _seedLock.Release();
        }
    }

    public void Dispose()
    {
        _client.Dispose();
        _seedLock.Dispose();
    }
}
