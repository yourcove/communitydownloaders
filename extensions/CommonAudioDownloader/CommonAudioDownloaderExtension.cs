using System.Net;
using System.Text.RegularExpressions;
using Cove.Core.DTOs;
using Cove.Plugins;
using Microsoft.Extensions.DependencyInjection;

namespace Cove.Extensions.CommunityDownloaders;

public sealed class CommonAudioDownloaderExtension : IDownloaderProvider
{
    private const string ExtensionId = "cove.official.downloaders.common-audio";
    public const string AudioDownloaderId = "cove.official.downloaders.common-audio/audio";
    private IServiceProvider? _services;

    private static readonly DownloaderDescriptor AudioDownloader = new(
        AudioDownloaderId,
        "Common Audio Sites",
        DownloaderEntity.Audio,
        ["soundgasm.net/*", "*.soundgasm.net/*", "whyp.it/*", "*.whyp.it/*"],
        DownloaderCapabilities.None);

    public string Id => ExtensionId;
    public string Name => "Common Audio Downloader";
    public string Version => "1.0.0";
    public string? Description => "Downloads audio from common hosted-audio pages such as Soundgasm and Whyp.";
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

    public IReadOnlyList<DownloaderDescriptor> GetDownloaders() => [AudioDownloader];

    public async Task<DownloaderUrlMatch?> MatchAsync(string url, CancellationToken ct)
    {
        if (!IsSupportedUrl(url))
            return null;

        var info = await TryGetAudioInfoAsync(url, ct);

        return new DownloaderUrlMatch(
            AudioDownloader.Id,
            url,
            null,
            info?.Title ?? OfficialDownloaderUtilities.DeriveTitleFromUrl(url, "Audio"));
    }

    public async Task<DownloaderResult?> DownloadAsync(DownloaderRequest request, IDownloaderHost host, CancellationToken ct)
    {
        if (!string.Equals(request.DownloaderId, AudioDownloader.Id, StringComparison.OrdinalIgnoreCase))
            return null;

        if (request.Entity != DownloaderEntity.Audio)
            throw new InvalidOperationException("The common audio downloader only supports audio downloads.");

        if (!IsSupportedUrl(request.Url))
            throw new InvalidOperationException("This downloader only supports Soundgasm and Whyp URLs.");

        host.ReportProgress(0.1d, "Resolving audio URL...");
        var info = await GetAudioInfoAsync(request.Url, ct);

        host.ReportProgress(0.25d, $"Downloading {info.Title}...");
        var client = GetHttpClient();
        using var response = await client.GetAsync(info.AudioUrl, HttpCompletionOption.ResponseHeadersRead, ct);
        response.EnsureSuccessStatusCode();

        var extension = OfficialDownloaderUtilities.EnsureExtensionFromUrl(info.AudioUrl, ".mp3");
        var localName = "downloaded" + extension;
        var localPath = Path.Combine(host.TempDirectory, localName);
        await using (var input = await response.Content.ReadAsStreamAsync(ct))
        await using (var output = File.Create(localPath))
        {
            await input.CopyToAsync(output, ct);
        }

        host.ReportProgress(0.95d, "Audio download completed.");
        return new DownloaderResult(localName, OfficialDownloaderUtilities.SanitizeFileName(info.Title + extension));
    }

    private static bool IsSupportedUrl(string url)
    {
        return OfficialDownloaderUtilities.IsHttpUrl(url) && OfficialDownloaderUtilities.IsDirectAudioSite(url);
    }

    private async Task<AudioPageInfo?> TryGetAudioInfoAsync(string url, CancellationToken ct)
    {
        try
        {
            return await GetAudioInfoAsync(url, ct);
        }
        catch
        {
            return null;
        }
    }

    private async Task<AudioPageInfo> GetAudioInfoAsync(string url, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.UserAgent.ParseAdd("CoveCommonAudioDownloader/1.0");
        using var response = await GetHttpClient().SendAsync(request, ct);
        response.EnsureSuccessStatusCode();

        var html = await response.Content.ReadAsStringAsync(ct);
        var fallbackTitle = OfficialDownloaderUtilities.DeriveTitleFromUrl(url, "Audio");
        var rawTitle = CleanAudioTitle(ExtractSoundgasmTitle(html) ?? OfficialDownloaderUtilities.ExtractHtmlTitle(html), url);
        var audioUrl = ExtractAudioUrl(html, url)
            ?? throw new InvalidOperationException("The audio page was downloaded, but no audio file URL could be found.");
        var creator = ExtractAudioCreator(html, url);
        var details = ExtractAudioDescription(html);
        var metadata = OfficialDownloaderUtilities.ExtractBracketedMetadata(rawTitle, details, fallbackTitle);

        return new AudioPageInfo(url, metadata.Title, audioUrl, creator, metadata.Details, metadata.TagNames);
    }

    private static string? ExtractSoundgasmTitle(string html)
    {
        var rawTitle = OfficialDownloaderUtilities.ExtractFirstRawMatch(html, @"(?is)<div\b[^>]*class\s*=\s*['""][^'"">]*\bjp-title\b[^'"">]*['""][^>]*>(.*?)</div>");
        if (string.IsNullOrWhiteSpace(rawTitle))
            return null;

        return OfficialDownloaderUtilities.CleanHtmlFragment(rawTitle);
    }

    private static string CleanAudioTitle(string? title, string url)
    {
        var fallback = OfficialDownloaderUtilities.DeriveTitleFromUrl(url, "Audio");
        if (string.IsNullOrWhiteSpace(title))
            return fallback;

        var trimmed = title.Trim();
        return Uri.TryCreate(url, UriKind.Absolute, out var uri)
            && trimmed.Equals(uri.Host, StringComparison.OrdinalIgnoreCase)
            ? fallback
            : trimmed;
    }

    private static string? ExtractAudioCreator(string html, string pageUrl)
    {
        var metaAuthor = OfficialDownloaderUtilities.ExtractMetaContent(html, "author")
            ?? OfficialDownloaderUtilities.ExtractMetaContent(html, "article:author");
        if (!string.IsNullOrWhiteSpace(metaAuthor))
            return metaAuthor.Trim();

        var audioBy = ExtractSoundgasmAudioBy(html);
        if (!string.IsNullOrWhiteSpace(audioBy))
            return audioBy;

        var profileLink = ExtractSoundgasmProfileLabel(html);
        if (!string.IsNullOrWhiteSpace(profileLink))
            return profileLink;

        if (!Uri.TryCreate(pageUrl, UriKind.Absolute, out var uri))
            return null;

        var segments = uri.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (segments.Length >= 2 && segments[0].Equals("u", StringComparison.OrdinalIgnoreCase))
            return Uri.UnescapeDataString(segments[1]).Trim();

        return null;
    }

    private static string? ExtractSoundgasmAudioBy(string html)
    {
        var rawAudioBy = OfficialDownloaderUtilities.ExtractFirstRawMatch(html, @"(?is)<b\b[^>]*>\s*Audio\s+by\s*:\s*</b>\s*(.*?)(?:<br\b|</p>|$)");
        if (string.IsNullOrWhiteSpace(rawAudioBy))
            return null;

        var creator = OfficialDownloaderUtilities.CleanHtmlFragment(rawAudioBy).Trim();
        var parenthesized = Regex.Match(creator, @"\((?<name>[^()]+)\)\s*$");
        if (parenthesized.Success && !string.IsNullOrWhiteSpace(parenthesized.Groups["name"].Value))
            return parenthesized.Groups["name"].Value.Trim();

        return string.IsNullOrWhiteSpace(creator) ? null : creator;
    }

    private static string? ExtractSoundgasmProfileLabel(string html)
    {
        foreach (Match match in Regex.Matches(html, @"(?is)<a\b[^>]*href\s*=\s*(['""])[^'"" >]*?/u/[^'"" >]+\1[^>]*>(?<label>.*?)</a>"))
        {
            var label = OfficialDownloaderUtilities.CleanHtmlFragment(match.Groups["label"].Value);
            if (!string.IsNullOrWhiteSpace(label))
                return label;
        }

        return null;
    }

    private static string? ExtractAudioDescription(string html)
    {
        var rawDescription = OfficialDownloaderUtilities.ExtractFirstRawMatch(html, @"(?is)<div\b[^>]*class\s*=\s*['""][^'"">]*\bjp-description\b[^'"">]*['""][^>]*>(.*?)</div>")
            ?? OfficialDownloaderUtilities.ExtractMetaContent(html, "description")
            ?? OfficialDownloaderUtilities.ExtractMetaContent(html, "og:description");

        if (string.IsNullOrWhiteSpace(rawDescription))
            return null;

        var details = OfficialDownloaderUtilities.CleanHtmlFragment(rawDescription);
        return string.IsNullOrWhiteSpace(details) ? null : details;
    }

    private static string? ExtractAudioUrl(string html, string pageUrl)
    {
        var normalizedHtml = NormalizeEmbeddedUrls(html);
        var candidates = new List<string?>
        {
            OfficialDownloaderUtilities.ExtractMetaContent(html, "og:audio"),
            OfficialDownloaderUtilities.ExtractMetaContent(html, "og:audio:url"),
            OfficialDownloaderUtilities.ExtractFirstRawMatch(html, @"(?is)<audio[^>]+src\s*=\s*['""]([^'""]+)['""]"),
            OfficialDownloaderUtilities.ExtractFirstRawMatch(html, @"(?is)<source[^>]+src\s*=\s*['""]([^'""]+)['""]"),
            OfficialDownloaderUtilities.ExtractFirstRawMatch(html, @"(?is)(https?://[^'""<>\s]+\.(?:mp3|m4a|ogg|opus|wav|flac)(?:\?[^'""<>\s]+)?)"),
        };

        if (!string.Equals(normalizedHtml, html, StringComparison.Ordinal))
        {
            candidates.Add(OfficialDownloaderUtilities.ExtractFirstRawMatch(normalizedHtml, @"(?is)<audio[^>]+src\s*=\s*['""]([^'""]+)['""]"));
            candidates.Add(OfficialDownloaderUtilities.ExtractFirstRawMatch(normalizedHtml, @"(?is)<source[^>]+src\s*=\s*['""]([^'""]+)['""]"));
            candidates.Add(OfficialDownloaderUtilities.ExtractFirstRawMatch(normalizedHtml, @"(?is)(https?://[^'""<>\s]+\.(?:mp3|m4a|ogg|opus|wav|flac)(?:\?[^'""<>\s]+)?)"));
        }

        foreach (var candidate in candidates)
        {
            if (string.IsNullOrWhiteSpace(candidate))
                continue;

            if (Uri.TryCreate(candidate, UriKind.Absolute, out var absolute))
                return absolute.ToString();

            if (Uri.TryCreate(new Uri(pageUrl), candidate, out var relative))
                return relative.ToString();
        }

        return null;
    }

    private static string NormalizeEmbeddedUrls(string html)
    {
        return WebUtility.HtmlDecode(html)
            .Replace("\\u002F", "/", StringComparison.OrdinalIgnoreCase)
            .Replace("\\/", "/", StringComparison.Ordinal)
            .Replace("\\u0026", "&", StringComparison.OrdinalIgnoreCase)
            .Replace("\\u003D", "=", StringComparison.OrdinalIgnoreCase);
    }

    private HttpClient GetHttpClient()
    {
        if (_services == null)
            throw new InvalidOperationException("The common audio downloader has not been initialized yet.");

        return _services.GetRequiredService<IHttpClientFactory>().CreateClient();
    }

    private sealed record AudioPageInfo(string PageUrl, string Title, string AudioUrl, string? Creator, string? Details, List<string> TagNames);
}
