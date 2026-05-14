using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Cove.Core.DTOs;
using Cove.Plugins;
using Microsoft.Extensions.DependencyInjection;

namespace Cove.Extensions.CommunityDownloaders;

public sealed class CommonTextDownloaderExtension : IDownloaderProvider
{
    private const string ExtensionId = "cove.official.downloaders.common-text";
    public const string TextDownloaderId = "cove.official.downloaders.common-text/literotica";
    private IServiceProvider? _services;

    private static readonly DownloaderDescriptor TextDownloader = new(
        TextDownloaderId,
        "Literotica Text",
        DownloaderEntity.Text,
        ["literotica.com/s/*", "www.literotica.com/s/*"],
        DownloaderCapabilities.None);

    public string Id => ExtensionId;
    public string Name => "Common Text Downloader";
    public string Version => "1.0.0";
    public string? Description => "Downloads stories from common text sites. Currently supports Literotica.";
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

    public IReadOnlyList<DownloaderDescriptor> GetDownloaders() => [TextDownloader];

    public async Task<DownloaderUrlMatch?> MatchAsync(string url, CancellationToken ct)
    {
        if (!IsLiteroticaUrl(url))
            return null;

        var info = await TryGetTextInfoAsync(url, ct);

        return new DownloaderUrlMatch(
            TextDownloader.Id,
            url,
            null,
            info?.Title ?? OfficialDownloaderUtilities.DeriveTitleFromUrl(url, "Literotica story"));
    }

    public async Task<DownloaderResult?> DownloadAsync(DownloaderRequest request, IDownloaderHost host, CancellationToken ct)
    {
        if (!string.Equals(request.DownloaderId, TextDownloader.Id, StringComparison.OrdinalIgnoreCase))
            return null;

        if (request.Entity != DownloaderEntity.Text)
            throw new InvalidOperationException("The common text downloader only supports text downloads.");

        if (!IsLiteroticaUrl(request.Url))
            throw new InvalidOperationException("This downloader currently supports Literotica story URLs.");

        host.ReportProgress(0.1d, "Downloading text page...");
        var info = await GetTextInfoAsync(request.Url, ct);
        var localName = "downloaded.txt";
        await File.WriteAllTextAsync(Path.Combine(host.TempDirectory, localName), BuildTextFileContent(info), Encoding.UTF8, ct);

        host.ReportProgress(0.95d, "Text download completed.");
        return new DownloaderResult(localName, OfficialDownloaderUtilities.SanitizeFileName(info.Title + ".txt"));
    }

    private static bool IsLiteroticaUrl(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri))
            return false;

        return OfficialDownloaderUtilities.IsHost(uri, "literotica.com")
            && uri.AbsolutePath.StartsWith("/s/", StringComparison.OrdinalIgnoreCase);
    }

    private async Task<TextPageInfo?> TryGetTextInfoAsync(string url, CancellationToken ct)
    {
        try
        {
            return await GetTextInfoAsync(url, ct);
        }
        catch
        {
            return null;
        }
    }

    private async Task<TextPageInfo> GetTextInfoAsync(string url, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.UserAgent.ParseAdd("CoveCommonTextDownloader/1.0");
        using var response = await GetHttpClient().SendAsync(request, ct);
        response.EnsureSuccessStatusCode();

        var html = await response.Content.ReadAsStringAsync(ct);
        var primaryCategory = ExtractLiteroticaPrimaryCategory(html);
        var titleMetadata = CleanLiteroticaTitle(OfficialDownloaderUtilities.ExtractHtmlTitle(html) ?? ExtractJsonLdString(html, "headline") ?? OfficialDownloaderUtilities.DeriveTitleFromUrl(url, "Literotica story"), primaryCategory);
        var body = ExtractStoryBody(html);
        if (string.IsNullOrWhiteSpace(body))
            throw new InvalidOperationException("The Literotica page was downloaded, but the story text could not be found.");

        var details = ExtractLiteroticaDescription(html);
        var author = ExtractLiteroticaAuthor(html);
        var tagNames = titleMetadata.TagNames
            .Concat(ExtractLiteroticaTags(html))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        return new TextPageInfo(titleMetadata.Title, body, details, author, tagNames);
    }

    private static string ExtractStoryBody(string html)
    {
        var bodyHtml = ExtractElementByClassFragment(html, "article__content")
            ?? ExtractElementByClassFragment(html, "aa_ht")
            ?? OfficialDownloaderUtilities.ExtractFirstRawMatch(html, @"(?is)<article[^>]*>(.*?)</article>");

        var paragraphs = ExtractParagraphs(string.IsNullOrWhiteSpace(bodyHtml) ? html : bodyHtml)
            .Where(IsStoryParagraph)
            .ToList();
        if (paragraphs.Count > 0)
            return string.Join(Environment.NewLine + Environment.NewLine, paragraphs);

        return CleanPreservingLineBreaks(bodyHtml ?? html);
    }

    private static string BuildTextFileContent(TextPageInfo info)
        => info.Body.Trim() + Environment.NewLine;

    private static TextTitleMetadata CleanLiteroticaTitle(string title, string? primaryCategory)
    {
        var trimmed = title.Trim();
        var cleaned = Regex.Replace(trimmed, @"\s*[-|]\s*(?:www\.)?literotica(?:\.com)?(?:\s*[-|].*)?\s*$", string.Empty, RegexOptions.IgnoreCase).Trim();
        if (string.IsNullOrWhiteSpace(cleaned))
            return new TextTitleMetadata(trimmed, []);

        var tagNames = new List<string>();
        var parts = Regex.Split(cleaned, @"\s+-\s+")
            .Select(part => part.Trim())
            .Where(part => !string.IsNullOrWhiteSpace(part))
            .ToList();

        if (parts.Count > 1 && IsLiteroticaCategoryName(parts[^1], primaryCategory))
        {
            tagNames.Add(parts[^1]);
            cleaned = string.Join(" - ", parts.Take(parts.Count - 1)).Trim();
        }

        return new TextTitleMetadata(string.IsNullOrWhiteSpace(cleaned) ? trimmed : cleaned, tagNames);
    }

    private static string? ExtractLiteroticaDescription(string html)
    {
        var description = OfficialDownloaderUtilities.ExtractMetaContent(html, "description")
            ?? OfficialDownloaderUtilities.ExtractMetaContent(html, "og:description")
            ?? OfficialDownloaderUtilities.ExtractMetaContent(html, "twitter:description")
            ?? ExtractJsonLdString(html, "description");

        return string.IsNullOrWhiteSpace(description)
            ? null
            : Regex.Replace(description.Trim(), @"\s+", " ");
    }

    private static string? ExtractLiteroticaAuthor(string html)
    {
        var author = OfficialDownloaderUtilities.ExtractMetaContent(html, "author")
            ?? OfficialDownloaderUtilities.ExtractMetaContent(html, "article:author");
        if (!string.IsNullOrWhiteSpace(author))
            return CleanAuthorName(author);

        author = ExtractJsonLdAuthor(html);
        if (!string.IsNullOrWhiteSpace(author))
            return CleanAuthorName(author);

        foreach (Match match in Regex.Matches(html, @"(?is)<a\b[^>]*href\s*=\s*(['""])[^'"" >]*?/authors/[^'"" >]+\1[^>]*>(?<label>.*?)</a>"))
        {
            var label = OfficialDownloaderUtilities.CleanHtmlFragment(match.Groups["label"].Value);
            if (!string.IsNullOrWhiteSpace(label) && !label.Contains("literotica", StringComparison.OrdinalIgnoreCase))
                return CleanAuthorName(label);
        }

        return null;
    }

    private static IReadOnlyList<string> ExtractLiteroticaTags(string html)
    {
        var tags = new List<string>();
        foreach (var name in new[] { "article:section", "section" })
        {
            var category = OfficialDownloaderUtilities.ExtractMetaContent(html, name);
            if (!string.IsNullOrWhiteSpace(category))
                tags.Add(category.Trim());
        }

        var jsonCategory = ExtractLiteroticaPrimaryCategory(html);
        if (!string.IsNullOrWhiteSpace(jsonCategory))
            tags.Add(jsonCategory.Trim());

        var keywords = OfficialDownloaderUtilities.ExtractMetaContent(html, "keywords");
        if (!string.IsNullOrWhiteSpace(keywords))
        {
            tags.AddRange(keywords
                .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Where(tag => !string.IsNullOrWhiteSpace(tag)));
        }

        tags.AddRange(ExtractJsonLdStringList(html, "keywords"));

        foreach (Match match in Regex.Matches(html, @"(?is)<a\b[^>]*href\s*=\s*(['""])[^'"">]*/c/[^'"">]+\1[^>]*>(?<label>.*?)</a>"))
        {
            var label = OfficialDownloaderUtilities.CleanHtmlFragment(match.Groups["label"].Value);
            if (!string.IsNullOrWhiteSpace(label) && LiteroticaCategories.Contains(label.Trim()))
                tags.Add(label.Trim());
        }

        return tags.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
    }

    private static string? ExtractJsonLdAuthor(string html)
    {
        foreach (var root in EnumerateJsonLdObjects(html))
        {
            if (!TryGetJsonProperty(root, "author", out var author))
                continue;

            var authorName = ExtractJsonString(author, "name") ?? ExtractJsonString(author);
            if (!string.IsNullOrWhiteSpace(authorName))
                return authorName;

            if (author.ValueKind != JsonValueKind.Array)
                continue;

            foreach (var item in author.EnumerateArray())
            {
                authorName = ExtractJsonString(item, "name") ?? ExtractJsonString(item);
                if (!string.IsNullOrWhiteSpace(authorName))
                    return authorName;
            }
        }

        return null;
    }

    private static string? ExtractJsonLdString(string html, string propertyName)
    {
        foreach (var root in EnumerateJsonLdObjects(html))
        {
            if (!TryGetJsonProperty(root, propertyName, out var value))
                continue;

            var text = ExtractJsonString(value);
            if (!string.IsNullOrWhiteSpace(text))
                return text;
        }

        return null;
    }

    private static IReadOnlyList<string> ExtractJsonLdStringList(string html, string propertyName)
    {
        var values = new List<string>();
        foreach (var root in EnumerateJsonLdObjects(html))
        {
            if (!TryGetJsonProperty(root, propertyName, out var value))
                continue;

            if (value.ValueKind == JsonValueKind.Array)
            {
                values.AddRange(value.EnumerateArray()
                    .Select(item => ExtractJsonString(item))
                    .Where(text => !string.IsNullOrWhiteSpace(text))
                    .Select(text => text!));
                continue;
            }

            var text = ExtractJsonString(value);
            if (!string.IsNullOrWhiteSpace(text))
                values.AddRange(text.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
        }

        return values.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
    }

    private static IEnumerable<JsonElement> EnumerateJsonLdObjects(string html)
    {
        foreach (Match match in Regex.Matches(html, @"(?is)<script\b[^>]*type\s*=\s*(['""])[^'""]*ld\+json[^'""]*\1[^>]*>(?<json>.*?)</script>"))
        {
            var json = System.Net.WebUtility.HtmlDecode(match.Groups["json"].Value).Trim();
            if (string.IsNullOrWhiteSpace(json))
                continue;

            JsonDocument document;
            try
            {
                document = JsonDocument.Parse(json);
            }
            catch (JsonException)
            {
                continue;
            }

            using (document)
            {
            foreach (var element in EnumerateJsonObjects(document.RootElement))
                yield return element.Clone();
            }
        }
    }

    private static IEnumerable<JsonElement> EnumerateJsonObjects(JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            yield return element;
            if (TryGetJsonProperty(element, "@graph", out var graph))
            {
                foreach (var graphElement in EnumerateJsonObjects(graph))
                    yield return graphElement;
            }
        }
        else if (element.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in element.EnumerateArray())
            {
                foreach (var nested in EnumerateJsonObjects(item))
                    yield return nested;
            }
        }
    }

    private static bool TryGetJsonProperty(JsonElement element, string propertyName, out JsonElement value)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            foreach (var property in element.EnumerateObject())
            {
                if (string.Equals(property.Name, propertyName, StringComparison.OrdinalIgnoreCase))
                {
                    value = property.Value;
                    return true;
                }
            }
        }

        value = default;
        return false;
    }

    private static string? ExtractJsonString(JsonElement element, string? propertyName = null)
    {
        if (!string.IsNullOrWhiteSpace(propertyName))
        {
            return TryGetJsonProperty(element, propertyName, out var property)
                ? ExtractJsonString(property)
                : null;
        }

        return element.ValueKind == JsonValueKind.String
            ? element.GetString()?.Trim()
            : null;
    }

    private static string CleanAuthorName(string value)
    {
        var cleaned = OfficialDownloaderUtilities.CleanHtmlFragment(value).Trim();
        cleaned = Regex.Replace(cleaned, @"^by\s+", string.Empty, RegexOptions.IgnoreCase).Trim();
        return cleaned;
    }

    private static string? ExtractLiteroticaPrimaryCategory(string html)
        => OfficialDownloaderUtilities.ExtractMetaContent(html, "article:section")
            ?? OfficialDownloaderUtilities.ExtractMetaContent(html, "section")
            ?? ExtractJsonLdString(html, "articleSection");

    private static bool IsLiteroticaCategoryName(string value, string? primaryCategory)
    {
        var trimmed = value.Trim();
        return LiteroticaCategories.Contains(trimmed)
            || (!string.IsNullOrWhiteSpace(primaryCategory) && trimmed.Equals(primaryCategory.Trim(), StringComparison.OrdinalIgnoreCase));
    }

    private static string? ExtractElementByClassFragment(string html, string classFragment)
    {
        var pattern = $@"(?is)<(?<tag>div|section|article)\b[^>]*class\s*=\s*(['""])[^'""]*{Regex.Escape(classFragment)}[^'""]*\2[^>]*>(?<content>.*?)</\k<tag>>";
        var match = Regex.Match(html, pattern);
        return match.Success ? match.Groups["content"].Value : null;
    }

    private static IEnumerable<string> ExtractParagraphs(string html)
    {
        foreach (Match match in Regex.Matches(html, @"(?is)<p\b[^>]*>(.*?)</p>"))
        {
            var paragraph = OfficialDownloaderUtilities.CleanHtmlFragment(match.Groups[1].Value);
            if (!string.IsNullOrWhiteSpace(paragraph))
                yield return paragraph;
        }
    }

    private static bool IsStoryParagraph(string paragraph)
    {
        return !paragraph.Equals("Log in to change and save custom font settings", StringComparison.OrdinalIgnoreCase)
            && !paragraph.Equals("Switch back to Classic.", StringComparison.OrdinalIgnoreCase)
            && !paragraph.StartsWith("November 2025 update is live", StringComparison.OrdinalIgnoreCase)
            && !paragraph.StartsWith("Help us make Literotica better", StringComparison.OrdinalIgnoreCase);
    }

    private static string CleanPreservingLineBreaks(string html)
    {
        var text = Regex.Replace(html, @"(?i)<\s*br\s*/?\s*>", "\n");
        text = Regex.Replace(text, @"(?i)</\s*(p|div|section|article)\s*>", "\n\n");
        text = Regex.Replace(text, @"<[^>]+>", string.Empty);
        text = System.Net.WebUtility.HtmlDecode(text);
        text = Regex.Replace(text, @"[ \t\r\f\v]+", " ");
        text = Regex.Replace(text, @"\n{3,}", "\n\n");
        return text.Trim();
    }

    private HttpClient GetHttpClient()
    {
        if (_services == null)
            throw new InvalidOperationException("The common text downloader has not been initialized yet.");

        return _services.GetRequiredService<IHttpClientFactory>().CreateClient();
    }

    private static readonly HashSet<string> LiteroticaCategories = new(StringComparer.OrdinalIgnoreCase)
    {
        "Anal",
        "BDSM",
        "Celebrities & Fan Fiction",
        "Chain Stories",
        "Erotic Couplings",
        "Erotic Horror",
        "Erotic Humor & Satire",
        "Erotic Novels",
        "Erotic Poetry",
        "Erotic Sci-Fi & Fantasy",
        "Exhibitionist & Voyeur",
        "Fetish",
        "First Time",
        "Gay Male",
        "Group Sex",
        "How To",
        "Illustrated",
        "Incest/Taboo",
        "Interracial Love",
        "Lesbian Sex",
        "Loving Wives",
        "Mature",
        "Mind Control",
        "Non-Erotic",
        "NonConsent/Reluctance",
        "Novels and Novellas",
        "Romance",
        "Toys & Masturbation",
        "Transgender & Crossdressers",
    };

    private sealed record TextTitleMetadata(string Title, List<string> TagNames);
    private sealed record TextPageInfo(string Title, string Body, string? Details, string? Author, List<string> TagNames);
}
