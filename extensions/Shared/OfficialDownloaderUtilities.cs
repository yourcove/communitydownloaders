using System.Net;
using System.Text.RegularExpressions;

namespace Cove.Extensions.CommunityDownloaders;

internal static class OfficialDownloaderUtilities
{
    public const string RepoUrl = "https://github.com/yourcove/communitydownloaders";
    private static readonly Regex HtmlTagRegex = new("<[^>]+>", RegexOptions.Compiled);
    private static readonly Regex BracketedTagRegex = new(@"\[(?<tag>[^\[\]]+)\]", RegexOptions.Compiled);
    private static readonly Regex WhitespaceRegex = new(@"[ \t\r\n]+", RegexOptions.Compiled);
    private static readonly Regex UrlRegex = new(@"https?://[^\s<>""'\)\]\}]+", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    public sealed record BracketedMetadata(string Title, string? Details, List<string> TagNames);

    public static bool IsHttpUrl(string url)
    {
        return Uri.TryCreate(url, UriKind.Absolute, out var uri)
            && (uri.Scheme.Equals(Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase)
                || uri.Scheme.Equals(Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase));
    }

    public static bool IsHost(Uri uri, string host)
    {
        return uri.Host.Equals(host, StringComparison.OrdinalIgnoreCase)
            || uri.Host.EndsWith("." + host, StringComparison.OrdinalIgnoreCase);
    }

    public static bool IsHost(string url, string host)
    {
        return Uri.TryCreate(url, UriKind.Absolute, out var uri) && IsHost(uri, host);
    }

    public static bool IsDirectAudioSite(string url)
    {
        return IsHost(url, "soundgasm.net") || IsHost(url, "whyp.it");
    }

    public static bool IsCommonTextSite(string url)
    {
        return IsHost(url, "literotica.com");
    }

    public static string DeriveTitleFromUrl(string url, string fallback)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri))
            return fallback;

        var lastSegment = uri.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries).LastOrDefault();
        if (string.IsNullOrWhiteSpace(lastSegment))
            return fallback;

        return Uri.UnescapeDataString(lastSegment.Replace('-', ' ').Replace('_', ' ')).Trim();
    }

    public static string SanitizeFileName(string name)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var sanitized = new string(name.Select(ch => invalid.Contains(ch) ? '_' : ch).ToArray()).Trim();
        return string.IsNullOrWhiteSpace(sanitized) ? "downloaded" : sanitized;
    }

    public static string? ExtractHtmlTitle(string html)
    {
        var ogTitle = ExtractMetaContent(html, "og:title");
        if (!string.IsNullOrWhiteSpace(ogTitle))
            return ogTitle;

        var h1 = ExtractFirstMatch(html, @"(?is)<h1[^>]*>(.*?)</h1>");
        if (!string.IsNullOrWhiteSpace(h1))
            return h1;

        return ExtractFirstMatch(html, @"(?is)<title[^>]*>(.*?)</title>");
    }

    public static string? ExtractMetaContent(string html, string propertyName)
    {
        foreach (Match metaTag in Regex.Matches(html, @"(?is)<meta\b[^>]*>"))
        {
            var attributes = ParseAttributes(metaTag.Value);
            var property = attributes.GetValueOrDefault("property") ?? attributes.GetValueOrDefault("name");
            if (propertyName.Equals(property, StringComparison.OrdinalIgnoreCase)
                && attributes.TryGetValue("content", out var content)
                && !string.IsNullOrWhiteSpace(content))
            {
                return WebUtility.HtmlDecode(content).Trim();
            }
        }

        return null;
    }

    private static Dictionary<string, string> ParseAttributes(string tag)
    {
        var attributes = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (Match attribute in Regex.Matches(tag, @"(?is)([a-zA-Z_:][-a-zA-Z0-9_:.]*)\s*=\s*(?:(['""])(.*?)\2|([^\s>]+))"))
        {
            attributes[attribute.Groups[1].Value] = attribute.Groups[3].Success
                ? attribute.Groups[3].Value
                : attribute.Groups[4].Value;
        }

        return attributes;
    }

    public static IReadOnlyList<string> ExtractUrls(string text)
    {
        var urls = new List<string>();
        var decoded = WebUtility.HtmlDecode(text);
        foreach (Match match in UrlRegex.Matches(decoded))
        {
            var url = match.Value.Trim().TrimEnd('.', ',', ';', ':');
            if (Uri.TryCreate(url, UriKind.Absolute, out _) && !urls.Contains(url, StringComparer.OrdinalIgnoreCase))
                urls.Add(url);
        }

        return urls;
    }

    public static BracketedMetadata ExtractBracketedMetadata(string? title, string? details, string fallbackTitle)
    {
        var titleValue = string.IsNullOrWhiteSpace(title) ? fallbackTitle : title.Trim();
        var fallbackValue = string.IsNullOrWhiteSpace(fallbackTitle) ? "Untitled" : fallbackTitle.Trim();
        var tagNames = new List<string>();

        AddBracketedTags(titleValue, tagNames);
        if (!string.IsNullOrWhiteSpace(details))
            AddBracketedTags(details, tagNames);

        var cleanedTitle = StripBracketedText(titleValue);
        if (string.IsNullOrWhiteSpace(cleanedTitle))
            cleanedTitle = fallbackValue;

        var cleanedDetails = string.IsNullOrWhiteSpace(details) ? null : StripBracketedText(details);

        return new BracketedMetadata(
            cleanedTitle,
            string.IsNullOrWhiteSpace(cleanedDetails) ? null : cleanedDetails,
            tagNames);
    }

    private static void AddBracketedTags(string value, List<string> tagNames)
    {
        foreach (Match match in BracketedTagRegex.Matches(value))
        {
            var tagName = NormalizeBracketedTagName(match.Groups["tag"].Value);
            if (!string.IsNullOrWhiteSpace(tagName) && !tagNames.Contains(tagName, StringComparer.OrdinalIgnoreCase))
                tagNames.Add(tagName);
        }
    }

    private static string NormalizeBracketedTagName(string value)
    {
        return value.Trim().TrimEnd('\\').Trim();
    }

    private static string StripBracketedText(string value)
    {
        var withoutBracketedTags = BracketedTagRegex.Replace(value, " ");
        return WhitespaceRegex.Replace(withoutBracketedTags, " ").Trim();
    }

    public static string? ExtractFirstMatch(string html, string pattern)
    {
        var value = ExtractFirstRawMatch(html, pattern);
        return string.IsNullOrWhiteSpace(value) ? null : CleanHtmlFragment(value);
    }

    public static string? ExtractFirstRawMatch(string html, string pattern)
    {
        var match = Regex.Match(html, pattern, RegexOptions.IgnoreCase | RegexOptions.Singleline);
        return match.Success ? match.Groups[1].Value : null;
    }

    public static string CleanHtmlFragment(string value)
    {
        var decoded = WebUtility.HtmlDecode(value);
        var withoutTags = HtmlTagRegex.Replace(decoded, string.Empty);
        return WhitespaceRegex.Replace(withoutTags, " ").Trim();
    }

    public static string EnsureExtensionFromUrl(string url, string fallbackExtension)
    {
        if (Uri.TryCreate(url, UriKind.Absolute, out var uri))
        {
            var extension = Path.GetExtension(uri.AbsolutePath);
            if (!string.IsNullOrWhiteSpace(extension))
                return extension;
        }

        return fallbackExtension;
    }
}
