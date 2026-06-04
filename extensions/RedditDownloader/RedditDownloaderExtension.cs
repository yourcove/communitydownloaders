using System.Text.Json;
using Cove.Core.DTOs;
using Cove.Plugins;
using Microsoft.Extensions.DependencyInjection;

namespace Cove.Extensions.CommunityDownloaders;

public sealed class RedditDownloaderExtension : IDownloaderProvider
{
    private const string ExtensionId = "cove.community.downloaders.reddit";
    private const string RedditImageDownloaderId = "cove.community.downloaders.reddit/image";
    private const string RedditVideoDownloaderId = "cove.community.downloaders.reddit/video";
    private const string DivertDownloaderId = "cove.community.downloaders.reddit/divert";
    private static readonly string[] RedditUrlPatterns = ["reddit.com/*", "*.reddit.com/*", "redd.it/*", "*.redd.it/*"];
    private static readonly string[] RedgifsUrlPatterns = ["redgifs.com/*", "*.redgifs.com/*", "redgif.com/*", "*.redgif.com/*"];
    private IServiceProvider? _services;

    private static readonly DownloaderDescriptor ImageDownloader = new(
        RedditImageDownloaderId,
        "Reddit Image",
        DownloaderEntity.Image,
        RedditUrlPatterns,
        DownloaderCapabilities.None);

    private static readonly DownloaderDescriptor VideoDownloader = new(
        RedditVideoDownloaderId,
        "Reddit Video",
        DownloaderEntity.Video,
        RedditUrlPatterns,
        DownloaderCapabilities.None);

    public string Id => ExtensionId;
    public string Name => "Reddit Downloader";
    public string Version => OfficialDownloaderUtilities.GetExtensionVersion(typeof(RedditDownloaderExtension));
    public string? Description => "Downloads Reddit-hosted post media and diverts linked URLs to other registered downloaders.";
    public string? Author => "Cove Team";
    public string? Url => OfficialDownloaderUtilities.RepoUrl;
    public string? IconUrl => null;
    public IReadOnlyList<string> Categories => [ExtensionCategories.Downloader];

    public void ConfigureServices(IServiceCollection services, ExtensionContext context)
    {
    }

    public Task InitializeAsync(IServiceProvider services, CancellationToken ct = default)
    {
        _services = services;
        return Task.CompletedTask;
    }

    public IReadOnlyList<DownloaderDescriptor> GetDownloaders() => [ImageDownloader, VideoDownloader];

    public async Task<DownloaderUrlMatch?> MatchAsync(string url, CancellationToken ct)
    {
        var matches = await MatchAllAsync(url, ct);
        return matches.FirstOrDefault();
    }

    public async Task<IReadOnlyList<DownloaderUrlMatch>> MatchAllAsync(string url, CancellationToken ct)
    {
        if (!IsRedditUrl(url))
            return [];

        var directImageUrl = TryExtractRedditImageUrl(url);
        if (!string.IsNullOrWhiteSpace(directImageUrl))
        {
            return [new DownloaderUrlMatch(
                ImageDownloader.Id,
                directImageUrl,
                null,
                OfficialDownloaderUtilities.DeriveTitleFromUrl(directImageUrl, "Reddit image"),
                string.Equals(directImageUrl, url, StringComparison.OrdinalIgnoreCase) ? null : url)];
        }

        var post = await TryGetPostInfoAsync(url, ct);
        if (post == null)
            return [];

        var matches = new List<DownloaderUrlMatch>();
        for (var index = 0; index < post.NativeMedia.Count; index++)
        {
            var media = post.NativeMedia[index];
            var label = BuildIndexedLabel(post.Title, post.NativeMedia.Count, index);
            matches.Add(new DownloaderUrlMatch(
                media.Kind == NativeMediaKind.Image ? ImageDownloader.Id : VideoDownloader.Id,
                media.Url,
                null,
                label,
                post.SourceUrl));
        }

        var links = post.LinkedUrls
            .Where(link => !IsRedditUrl(link))
            .Where(link => !post.NativeMedia.Any(media => string.Equals(media.Url, link, StringComparison.OrdinalIgnoreCase)))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        var redgifsLinks = links
            .Where(IsRedgifsUrl)
            .ToList();

        for (var index = 0; index < redgifsLinks.Count; index++)
        {
            var mediaUrl = await TryResolveRedgifsMediaUrlAsync(redgifsLinks[index], ct);
            matches.Add(new DownloaderUrlMatch(
                VideoDownloader.Id,
                string.IsNullOrWhiteSpace(mediaUrl) ? redgifsLinks[index] : mediaUrl,
                null,
                BuildIndexedLabel(post.Title, redgifsLinks.Count, index),
                post.SourceUrl));
        }

        var divertedLinks = links
            .Where(link => !IsRedgifsUrl(link))
            .ToList();

        for (var index = 0; index < divertedLinks.Count; index++)
        {
            var linkedLabel = OfficialDownloaderUtilities.DeriveTitleFromUrl(divertedLinks[index], "Linked media");
            matches.Add(new DownloaderUrlMatch(
                DivertDownloaderId,
                divertedLinks[index],
                null,
                BuildIndexedLabel(linkedLabel, divertedLinks.Count, index),
                post.SourceUrl,
                Divert: true));
        }

        return matches;
    }

    public async Task<DownloaderResult?> DownloadAsync(DownloaderRequest request, IDownloaderHost host, CancellationToken ct)
    {
        if (string.Equals(request.DownloaderId, ImageDownloader.Id, StringComparison.OrdinalIgnoreCase))
            return await DownloadNativeMediaAsync(request, host, DownloaderEntity.Image, ".jpg", ct);

        if (string.Equals(request.DownloaderId, VideoDownloader.Id, StringComparison.OrdinalIgnoreCase))
        {
            if (IsRedgifsUrl(request.Url) && !IsRedgifsMediaUrl(request.Url))
            {
                host.ReportProgress(0.1d, "Resolving Redgifs media...");
                var mediaUrl = await TryResolveRedgifsMediaUrlAsync(request.Url, ct);
                if (string.IsNullOrWhiteSpace(mediaUrl))
                    throw new InvalidOperationException("Could not resolve Redgifs media URL.");

                request = request with { Url = mediaUrl };
            }

            return await DownloadNativeMediaAsync(request, host, DownloaderEntity.Video, ".mp4", ct);
        }

        return null;
    }

    private async Task<DownloaderResult> DownloadNativeMediaAsync(DownloaderRequest request, IDownloaderHost host, DownloaderEntity expectedEntity, string fallbackExtension, CancellationToken ct)
    {
        if (request.Entity != expectedEntity)
            throw new InvalidOperationException($"The Reddit {expectedEntity.ToString().ToLowerInvariant()} downloader cannot download {request.Entity.ToString().ToLowerInvariant()} items.");

        if (!OfficialDownloaderUtilities.IsHttpUrl(request.Url))
            throw new InvalidOperationException("Reddit media downloads require an absolute HTTP or HTTPS URL.");

        host.ReportProgress(0.2d, "Downloading Reddit media...");
        using var httpRequest = new HttpRequestMessage(HttpMethod.Get, request.Url);
        httpRequest.Headers.UserAgent.ParseAdd("CoveRedditDownloader/1.0");
        if (IsRedgifsMediaUrl(request.Url))
            httpRequest.Headers.Referrer = new Uri("https://www.redgifs.com/");

        using var response = await GetHttpClient().SendAsync(httpRequest, HttpCompletionOption.ResponseHeadersRead, ct);
        response.EnsureSuccessStatusCode();

        var extension = OfficialDownloaderUtilities.EnsureExtensionFromUrl(request.Url, fallbackExtension);
        var localName = "downloaded" + extension;
        var localPath = Path.Combine(host.TempDirectory, localName);
        await using (var input = await response.Content.ReadAsStreamAsync(ct))
        await using (var output = File.Create(localPath))
        {
            await input.CopyToAsync(output, ct);
        }

        host.ReportProgress(0.95d, "Reddit media download completed.");
        return new DownloaderResult(localName, OfficialDownloaderUtilities.SanitizeFileName(OfficialDownloaderUtilities.DeriveTitleFromUrl(request.Url, "reddit-media") + extension));
    }

    private async Task<RedditPostInfo?> TryGetPostInfoAsync(string url, CancellationToken ct)
    {
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, BuildRedditJsonUrl(url));
            request.Headers.UserAgent.ParseAdd("CoveRedditDownloader/1.0");
            using var response = await GetHttpClient().SendAsync(request, ct);
            if (!response.IsSuccessStatusCode)
                return null;

            var json = await response.Content.ReadAsStringAsync(ct);
            using var document = JsonDocument.Parse(json);
            if (document.RootElement.ValueKind != JsonValueKind.Array || document.RootElement.GetArrayLength() == 0)
                return null;

            var listing = document.RootElement[0];
            if (!listing.TryGetProperty("data", out var data)
                || !data.TryGetProperty("children", out var children)
                || children.ValueKind != JsonValueKind.Array
                || children.GetArrayLength() == 0)
            {
                return null;
            }

            var post = children[0].GetProperty("data");
            var fallbackTitle = OfficialDownloaderUtilities.DeriveTitleFromUrl(url, "Reddit post");
            var rawTitle = GetString(post, "title") ?? fallbackTitle;
            var metadata = OfficialDownloaderUtilities.ExtractBracketedMetadata(rawTitle, ExtractPostDescription(post), fallbackTitle);
            var nativeMedia = ExtractNativeMedia(post);
            var linkedUrls = ExtractLinkedUrls(post);

            return new RedditPostInfo(url, metadata.Title, metadata.Details, metadata.TagNames, nativeMedia, linkedUrls);
        }
        catch
        {
            return null;
        }
    }

    private static List<NativeMedia> ExtractNativeMedia(JsonElement post)
    {
        var media = new List<NativeMedia>();
        AddNativeMediaFromPost(post, media);

        if (post.TryGetProperty("crosspost_parent_list", out var crossposts) && crossposts.ValueKind == JsonValueKind.Array)
        {
            foreach (var crosspost in crossposts.EnumerateArray())
                AddNativeMediaFromPost(crosspost, media);
        }

        return media
            .GroupBy(item => item.Url, StringComparer.OrdinalIgnoreCase)
            .Select(group => group.First())
            .ToList();
    }

    private static void AddNativeMediaFromPost(JsonElement post, List<NativeMedia> media)
    {
        var url = GetString(post, "url_overridden_by_dest", "url");
        AddImageMediaIfPresent(url, media);

        if (TryGetPropertyPath(post, out var fallbackUrl, "secure_media", "reddit_video", "fallback_url")
            || TryGetPropertyPath(post, out fallbackUrl, "media", "reddit_video", "fallback_url"))
        {
            if (!string.IsNullOrWhiteSpace(fallbackUrl))
                media.Add(new NativeMedia(NativeMediaKind.Video, fallbackUrl));
        }

        if (post.TryGetProperty("gallery_data", out var galleryData)
            && post.TryGetProperty("media_metadata", out var metadata)
            && galleryData.TryGetProperty("items", out var items)
            && items.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in items.EnumerateArray())
            {
                var mediaId = GetString(item, "media_id");
                if (string.IsNullOrWhiteSpace(mediaId) || !metadata.TryGetProperty(mediaId, out var mediaEntry))
                    continue;

                var galleryUrl = TryGetPropertyPath(mediaEntry, out var sourceUrl, "s", "u") ? sourceUrl : null;
                AddImageMediaIfPresent(galleryUrl, media);
            }
        }

        if (IsImagePost(post)
            && post.TryGetProperty("preview", out var preview)
            && preview.TryGetProperty("images", out var images)
            && images.ValueKind == JsonValueKind.Array)
        {
            foreach (var previewImage in images.EnumerateArray())
            {
                if (TryGetPropertyPath(previewImage, out var sourceUrl, "source", "url"))
                    AddImageMediaIfPresent(sourceUrl, media);
            }
        }
    }

    private static bool IsImagePost(JsonElement post)
    {
        var postHint = GetString(post, "post_hint");
        if (string.Equals(postHint, "image", StringComparison.OrdinalIgnoreCase))
            return true;

        if (post.TryGetProperty("is_gallery", out var isGallery)
            && isGallery.ValueKind == JsonValueKind.True)
        {
            return true;
        }

        var url = GetString(post, "url_overridden_by_dest", "url");
        return !string.IsNullOrWhiteSpace(url) && IsRedditImageUrl(url);
    }

    private static void AddImageMediaIfPresent(string? url, List<NativeMedia> media)
    {
        var imageUrl = NormalizeRedditMediaUrl(url);
        if (!string.IsNullOrWhiteSpace(imageUrl) && IsRedditImageUrl(imageUrl))
            media.Add(new NativeMedia(NativeMediaKind.Image, imageUrl));
    }

    private static bool TryGetPropertyPath(JsonElement root, out string? value, params string[] path)
    {
        value = null;
        var current = root;
        foreach (var segment in path)
        {
            if (current.ValueKind != JsonValueKind.Object || !current.TryGetProperty(segment, out current))
                return false;
        }

        if (current.ValueKind != JsonValueKind.String)
            return false;

        value = current.GetString();
        return !string.IsNullOrWhiteSpace(value);
    }

    private static List<string> ExtractLinkedUrls(JsonElement post)
    {
        var urls = new List<string>();
        AddPostLinkedUrls(post, urls);

        if (post.TryGetProperty("crosspost_parent_list", out var crossposts) && crossposts.ValueKind == JsonValueKind.Array)
        {
            foreach (var crosspost in crossposts.EnumerateArray())
                AddPostLinkedUrls(crosspost, urls);
        }

        return urls.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
    }

    private static string? ExtractPostDescription(JsonElement post)
    {
        var selftext = GetString(post, "selftext");
        if (!string.IsNullOrWhiteSpace(selftext))
            return selftext;

        var selftextHtml = GetString(post, "selftext_html");
        if (!string.IsNullOrWhiteSpace(selftextHtml))
        {
            var cleaned = OfficialDownloaderUtilities.CleanHtmlFragment(selftextHtml);
            if (!string.IsNullOrWhiteSpace(cleaned))
                return cleaned;
        }

        return GetString(post, "body");
    }

    private static void AddPostLinkedUrls(JsonElement post, List<string> urls)
    {
        AddLinkedUrlCandidate(GetString(post, "url_overridden_by_dest", "url"), urls);

        foreach (var bodyUrl in OfficialDownloaderUtilities.ExtractUrls(GetString(post, "selftext", "selftext_html", "body") ?? string.Empty))
            AddLinkedUrlCandidate(bodyUrl, urls);
    }

    private static void AddLinkedUrlCandidate(string? candidate, List<string> urls)
    {
        if (string.IsNullOrWhiteSpace(candidate))
            return;

        var normalized = NormalizeRedditMediaUrl(candidate);
        if (string.IsNullOrWhiteSpace(normalized)
            || !OfficialDownloaderUtilities.IsHttpUrl(normalized)
            || IsRedditUrl(normalized))
        {
            return;
        }

        if (!urls.Contains(normalized, StringComparer.OrdinalIgnoreCase))
            urls.Add(normalized);
    }

    private static bool IsRedditUrl(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri))
            return false;

        return OfficialDownloaderUtilities.IsHost(uri, "reddit.com") || OfficialDownloaderUtilities.IsHost(uri, "redd.it");
    }

    private static bool IsRedditImageUrl(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri))
            return false;

        var extension = Path.GetExtension(uri.AbsolutePath);
        return (OfficialDownloaderUtilities.IsHost(uri, "i.redd.it") || OfficialDownloaderUtilities.IsHost(uri, "preview.redd.it"))
            && (extension.Equals(".jpg", StringComparison.OrdinalIgnoreCase)
                || extension.Equals(".jpeg", StringComparison.OrdinalIgnoreCase)
                || extension.Equals(".png", StringComparison.OrdinalIgnoreCase)
                || extension.Equals(".webp", StringComparison.OrdinalIgnoreCase));
    }

    private static string? TryExtractRedditImageUrl(string url)
    {
        if (IsRedditImageUrl(url))
            return NormalizeRedditMediaUrl(url);

        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri)
            || !OfficialDownloaderUtilities.IsHost(uri, "reddit.com")
            || !uri.AbsolutePath.Equals("/media", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        foreach (var pair in uri.Query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var parts = pair.Split('=', 2);
            if (parts.Length != 2)
                continue;

            var key = Uri.UnescapeDataString(parts[0].Replace('+', ' '));
            if (!key.Equals("url", StringComparison.OrdinalIgnoreCase))
                continue;

            var candidate = Uri.UnescapeDataString(parts[1].Replace('+', ' '));
            if (IsRedditImageUrl(candidate))
                return NormalizeRedditMediaUrl(candidate);
        }

        return null;
    }

    private static string? NormalizeRedditMediaUrl(string? url)
    {
        return string.IsNullOrWhiteSpace(url)
            ? null
            : url.Trim().Replace("&amp;", "&", StringComparison.OrdinalIgnoreCase);
    }

    private async Task<string?> TryResolveRedgifsMediaUrlAsync(string url, CancellationToken ct)
    {
        var redgifsId = ExtractRedgifsId(url);
        if (string.IsNullOrWhiteSpace(redgifsId))
            return await TryResolveRedgifsMediaUrlFromHtmlAsync(url, ct);

        try
        {
            var token = await GetRedgifsTemporaryTokenAsync(ct);
            using var request = new HttpRequestMessage(HttpMethod.Get, $"https://api.redgifs.com/v2/gifs/{Uri.EscapeDataString(redgifsId)}");
            request.Headers.UserAgent.ParseAdd("CoveRedditDownloader/1.0");
            if (!string.IsNullOrWhiteSpace(token))
                request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);

            using var response = await GetHttpClient().SendAsync(request, ct);
            if (!response.IsSuccessStatusCode)
                return await TryResolveRedgifsMediaUrlFromHtmlAsync(url, ct);

            var json = await response.Content.ReadAsStringAsync(ct);
            using var document = JsonDocument.Parse(json);
            if (!document.RootElement.TryGetProperty("gif", out var gif)
                || !gif.TryGetProperty("urls", out var urls)
                || urls.ValueKind != JsonValueKind.Object)
            {
                return await TryResolveRedgifsMediaUrlFromHtmlAsync(url, ct);
            }

            return GetString(urls, "hd", "sd", "mobile", "thumbnail");
        }
        catch
        {
            return await TryResolveRedgifsMediaUrlFromHtmlAsync(url, ct);
        }
    }

    private async Task<string?> GetRedgifsTemporaryTokenAsync(CancellationToken ct)
    {
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, "https://api.redgifs.com/v2/auth/temporary");
            request.Headers.UserAgent.ParseAdd("CoveRedditDownloader/1.0");
            using var response = await GetHttpClient().SendAsync(request, ct);
            if (!response.IsSuccessStatusCode)
                return null;

            var json = await response.Content.ReadAsStringAsync(ct);
            using var document = JsonDocument.Parse(json);
            return GetString(document.RootElement, "token");
        }
        catch
        {
            return null;
        }
    }

    private async Task<string?> TryResolveRedgifsMediaUrlFromHtmlAsync(string url, CancellationToken ct)
    {
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            request.Headers.UserAgent.ParseAdd("CoveRedditDownloader/1.0");
            using var response = await GetHttpClient().SendAsync(request, ct);
            if (!response.IsSuccessStatusCode)
                return null;

            var html = await response.Content.ReadAsStringAsync(ct);
            var ogVideo = OfficialDownloaderUtilities.ExtractMetaContent(html, "og:video")
                ?? OfficialDownloaderUtilities.ExtractMetaContent(html, "og:video:url")
                ?? OfficialDownloaderUtilities.ExtractMetaContent(html, "twitter:player:stream");
            if (!string.IsNullOrWhiteSpace(ogVideo))
                return ogVideo;

            return OfficialDownloaderUtilities.ExtractUrls(html).FirstOrDefault(IsRedgifsMediaUrl);
        }
        catch
        {
            return null;
        }
    }

    private async Task<RedgifsInfo?> TryGetRedgifsInfoAsync(string url, CancellationToken ct)
    {
        if (!IsRedgifsUrl(url))
            return null;

        var mediaUrl = IsRedgifsMediaUrl(url) ? url : await TryResolveRedgifsMediaUrlAsync(url, ct);
        var title = OfficialDownloaderUtilities.DeriveTitleFromUrl(url, "Redgifs video");

        if (!IsRedgifsMediaUrl(url))
        {
            try
            {
                using var request = new HttpRequestMessage(HttpMethod.Get, url);
                request.Headers.UserAgent.ParseAdd("CoveRedditDownloader/1.0");
                using var response = await GetHttpClient().SendAsync(request, ct);
                if (response.IsSuccessStatusCode)
                {
                    var html = await response.Content.ReadAsStringAsync(ct);
                    title = OfficialDownloaderUtilities.ExtractHtmlTitle(html) ?? title;
                }
            }
            catch
            {
            }
        }

        return new RedgifsInfo(title, BuildDistinctUrls([url], string.IsNullOrWhiteSpace(mediaUrl) ? [] : [mediaUrl]));
    }

    private static bool IsRedgifsUrl(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri))
            return false;

        return OfficialDownloaderUtilities.IsHost(uri, "redgifs.com")
            || OfficialDownloaderUtilities.IsHost(uri, "redgif.com");
    }

    private static bool IsRedgifsMediaUrl(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri))
            return false;

        return (OfficialDownloaderUtilities.IsHost(uri, "redgifs.com") || OfficialDownloaderUtilities.IsHost(uri, "redgif.com"))
            && Path.GetExtension(uri.AbsolutePath).Equals(".mp4", StringComparison.OrdinalIgnoreCase);
    }

    private static string? ExtractRedgifsId(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri))
            return null;

        if (!IsRedgifsUrl(url))
            return null;

        if (Path.GetExtension(uri.AbsolutePath).Equals(".mp4", StringComparison.OrdinalIgnoreCase))
            return null;

        var segments = uri.AbsolutePath
            .Split('/', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (segments.Length == 0)
            return null;

        var knownRoute = segments[0].Equals("watch", StringComparison.OrdinalIgnoreCase)
            || segments[0].Equals("ifr", StringComparison.OrdinalIgnoreCase)
            || segments[0].Equals("gifs", StringComparison.OrdinalIgnoreCase);
        return knownRoute && segments.Length > 1 ? segments[1] : segments[^1];
    }

    private static List<string> BuildDistinctUrls(params IEnumerable<string?>[] sources)
    {
        return sources
            .SelectMany(source => source)
            .Where(url => !string.IsNullOrWhiteSpace(url))
            .Select(url => url!.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static string BuildIndexedLabel(string title, int count, int index)
    {
        return count <= 1 ? title : $"{title} ({index + 1})";
    }

    private static string BuildRedditJsonUrl(string url)
    {
        var uri = new Uri(url, UriKind.Absolute);
        var segments = uri.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var path = uri.AbsolutePath.EndsWith(".json", StringComparison.OrdinalIgnoreCase)
            ? uri.AbsolutePath
            : segments.Length >= 1 && OfficialDownloaderUtilities.IsHost(uri, "redd.it")
                ? $"/comments/{segments[0]}.json"
                : segments.Length >= 2 && segments[0].Equals("gallery", StringComparison.OrdinalIgnoreCase)
                    ? $"/comments/{segments[1]}.json"
                    : uri.AbsolutePath.TrimEnd('/') + ".json";
        var builder = new UriBuilder(uri)
        {
            Scheme = Uri.UriSchemeHttps,
            Host = "www.reddit.com",
            Path = path,
            Query = "raw_json=1",
            Fragment = string.Empty,
        };

        return builder.Uri.ToString();
    }

    private static string? GetString(JsonElement root, params string[] names)
    {
        foreach (var name in names)
        {
            if (root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String)
            {
                var text = value.GetString()?.Trim();
                if (!string.IsNullOrWhiteSpace(text))
                    return text;
            }
        }

        return null;
    }

    private HttpClient GetHttpClient()
    {
        if (_services == null)
            throw new InvalidOperationException("The Reddit downloader has not been initialized yet.");

        return _services.GetRequiredService<IHttpClientFactory>().CreateClient();
    }

    private enum NativeMediaKind
    {
        Image,
        Video,
    }

    private sealed record NativeMedia(NativeMediaKind Kind, string Url);
    private sealed record RedditPostInfo(string SourceUrl, string Title, string? Details, List<string> TagNames, IReadOnlyList<NativeMedia> NativeMedia, IReadOnlyList<string> LinkedUrls);
    private sealed record RedgifsInfo(string Title, List<string> Urls);
}
