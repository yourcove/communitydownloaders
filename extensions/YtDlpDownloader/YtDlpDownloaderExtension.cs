using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text.Json;
using Cove.Core.DTOs;
using Cove.Core.Interfaces;
using Cove.Plugins;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Cove.Extensions.CommunityDownloaders;

public sealed class YtDlpDownloaderExtension : IDownloaderProvider
{
    private const string ExtensionId = "cove.community.downloaders.ytdlp";
    private const string VideoDownloaderId = "cove.community.downloaders.ytdlp/video";
    private const string AudioDownloaderId = "cove.community.downloaders.ytdlp/audio";

    private static readonly DownloaderDescriptor VideoDownloader = new(
        VideoDownloaderId,
        "yt-dlp Video",
        DownloaderEntity.Video,
        ["https://*/*", "http://*/*"],
        DownloaderCapabilities.MultiQuality | DownloaderCapabilities.ResumeSupported | DownloaderCapabilities.InlineMetadata);

    private static readonly DownloaderDescriptor AudioDownloader = new(
        AudioDownloaderId,
        "yt-dlp Audio",
        DownloaderEntity.Audio,
        ["https://*/*", "http://*/*"],
        DownloaderCapabilities.ResumeSupported);

    private static readonly SiteSessionCoordinator SiteSessions = new();

    private IYtDlpCommandRunner? _runner;
    private IServiceProvider? _services;
    private IConfiguration? _configuration;
    private string? _extensionRoot;

    public YtDlpDownloaderExtension()
    {
    }

    public YtDlpDownloaderExtension(IYtDlpCommandRunner runner)
    {
        _runner = runner;
    }

    public string Id => ExtensionId;
    public string Name => "yt-dlp Downloader";
    public string Version => OfficialDownloaderUtilities.GetExtensionVersion(typeof(YtDlpDownloaderExtension));
    public string? Description => "Generic yt-dlp-powered video and audio downloads.";
    public string? Author => "Cove Team";
    public string? Url => OfficialDownloaderUtilities.RepoUrl;
    public string? IconUrl => null;
    public IReadOnlyList<string> Categories => [ExtensionCategories.Downloader, ExtensionCategories.Metadata];

    public void ConfigureServices(IServiceCollection services, ExtensionContext context)
    {
        _configuration = context.Configuration;
        _extensionRoot = Path.Combine(context.DataDirectory, Id);
    }

    public Task InitializeAsync(IServiceProvider services, CancellationToken ct = default)
    {
        _services = services;
        _configuration ??= services.GetService<IConfiguration>();
        _extensionRoot ??= ResolveExtensionRoot(services);
        Directory.CreateDirectory(GetExtensionRoot());
        _runner ??= CreateRunner();
        return Task.CompletedTask;
    }

    public IReadOnlyList<DownloaderDescriptor> GetDownloaders() => [VideoDownloader, AudioDownloader];

    public async Task<DownloaderUrlMatch?> MatchAsync(string url, CancellationToken ct)
    {
        var matches = await MatchAllAsync(url, ct);
        return matches.FirstOrDefault();
    }

    public async Task<IReadOnlyList<DownloaderUrlMatch>> MatchAllAsync(string url, CancellationToken ct)
    {
        if (!OfficialDownloaderUtilities.IsHttpUrl(url)
            || OfficialDownloaderUtilities.IsDirectAudioSite(url)
            || OfficialDownloaderUtilities.IsCommonTextSite(url)
            || OfficialDownloaderUtilities.IsHost(url, "reddit.com")
            || OfficialDownloaderUtilities.IsHost(url, "redd.it"))
        {
            return [];
        }

        var info = await TryGetMediaInfoAsync(url, ct);
        if (info == null)
            return [];

        var matches = new List<DownloaderUrlMatch>();
        if (info.HasVideo)
        {
            matches.Add(new DownloaderUrlMatch(
                VideoDownloader.Id,
                info.NormalizedUrl,
                BuildQualityOptions(info.AvailableResolutions),
                info.Title));
        }

        if (info.HasAudio && !info.HasVideo)
            matches.Add(new DownloaderUrlMatch(AudioDownloader.Id, info.NormalizedUrl, null, info.Title));

        return matches;
    }

    public async Task<DownloaderResult?> DownloadAsync(DownloaderRequest request, IDownloaderHost host, CancellationToken ct)
    {
        if (string.Equals(request.DownloaderId, VideoDownloader.Id, StringComparison.OrdinalIgnoreCase))
            return await DownloadMediaAsync(request, host, DownloaderEntity.Video, ct);

        if (string.Equals(request.DownloaderId, AudioDownloader.Id, StringComparison.OrdinalIgnoreCase))
            return await DownloadMediaAsync(request, host, DownloaderEntity.Audio, ct);

        return null;
    }

    public interface IYtDlpCommandRunner
    {
        Task<YtDlpCommandResult> RunAsync(IEnumerable<string> arguments, CancellationToken ct);
    }

    public sealed record YtDlpCommandResult(int ExitCode, string StandardOutput, string StandardError);

    private sealed record YtDlpSettings(
        string? Impersonate,
        string? CookiesPath,
        string? CookiesFromBrowser,
        string? Proxy,
        string? Username,
        string? Password,
        bool UseNetrc)
    {
        /// <summary>The saved-login site these arguments sign in to, or null when no login is involved.</summary>
        public string? ThrottleSite { get; init; }

        /// <summary>
        /// The cookie jar that keeps the saved-login site's session between runs, or null when the user
        /// supplied their own cookies. Runs never hand this file to yt-dlp directly; see <see cref="SiteSessionCoordinator"/>.
        /// </summary>
        public string? SiteCookieJar { get; init; }

        /// <summary>When the saved login was last saved in Cove's settings; a newer save lifts a sign-in pause.</summary>
        public DateTime? LoginSavedAt { get; init; }

        public IReadOnlyList<string> BuildArguments()
        {
            var args = new List<string>();
            AddOption(args, "--impersonate", Impersonate);
            AddOption(args, "--cookies", CookiesPath);
            AddOption(args, "--cookies-from-browser", CookiesFromBrowser);
            AddOption(args, "--proxy", Proxy);
            AddOption(args, "--username", Username);
            AddOption(args, "--password", Password);
            if (UseNetrc)
                args.Add("--netrc");

            return args;
        }

        private static void AddOption(List<string> args, string option, string? value)
        {
            if (string.IsNullOrWhiteSpace(value))
                return;

            args.Add(option);
            args.Add(value.Trim());
        }
    }

    private async Task<DownloaderResult> DownloadMediaAsync(DownloaderRequest request, IDownloaderHost host, DownloaderEntity expectedEntity, CancellationToken ct)
    {
        if (request.Entity != expectedEntity)
            throw new InvalidOperationException($"The yt-dlp {expectedEntity.ToString().ToLowerInvariant()} downloader cannot download {request.Entity.ToString().ToLowerInvariant()} items.");

        var logger = host.CreateLogger(typeof(YtDlpDownloaderExtension).FullName ?? nameof(YtDlpDownloaderExtension));
        logger.LogInformation(
            "yt-dlp {Entity} download starting for {Url}. DownloaderId: {DownloaderId}. Quality: {QualityId}",
            expectedEntity,
            request.Url,
            request.DownloaderId,
            request.QualityId);

        host.ReportProgress(0.05d, "Resolving yt-dlp metadata...");
        var info = await GetMediaInfoAsync(request.Url, ct);
        logger.LogDebug(
            "yt-dlp metadata resolved for {Url}. Title: {Title}. HasVideo: {HasVideo}. HasAudio: {HasAudio}.",
            request.Url,
            info.Title,
            info.HasVideo,
            info.HasAudio);

        if (expectedEntity == DownloaderEntity.Video && !info.HasVideo)
            throw new InvalidOperationException("yt-dlp did not report a downloadable video stream for this URL.");

        if (expectedEntity == DownloaderEntity.Audio && !info.HasAudio)
            throw new InvalidOperationException("yt-dlp did not report a downloadable audio stream for this URL.");

        host.ReportProgress(0.15d, $"Downloading {info.Title}...");
        var outputTemplate = Path.Combine(host.TempDirectory, "downloaded.%(ext)s");
        var command = await RunForUrlAsync(
            info.NormalizedUrl,
            [
                "--no-playlist",
                "--no-warnings",
                "--newline",
                "--no-part",
                "--output",
                outputTemplate,
                "--format",
                expectedEntity == DownloaderEntity.Video ? BuildVideoFormatSelector(request.QualityId) : "bestaudio/best",
                info.NormalizedUrl,
            ],
            ct);

        LogCommandResult(logger, request.Url, "download", command, includeStandardOutput: false);

        EnsureSuccess(command, "yt-dlp failed to download the media");

        var downloadedFile = FindDownloadedFile(host.TempDirectory)
            ?? throw new InvalidOperationException("yt-dlp completed successfully but did not leave a downloaded media file in the temp directory.");

        host.ReportProgress(0.95d, "Download completed.");
        var originalFilename = BuildOriginalFileName(info.Title, info.MediaId, Path.GetExtension(downloadedFile), expectedEntity == DownloaderEntity.Audio ? ".m4a" : ".mp4");
        logger.LogDebug(
            "yt-dlp {Entity} download succeeded for {Url}. OriginalFilename: {OriginalFilename}",
            expectedEntity,
            request.Url,
            originalFilename);
        return new DownloaderResult(
            Path.GetFileName(downloadedFile),
            originalFilename,
            InlineVideoMetadata: expectedEntity == DownloaderEntity.Video ? info.VideoMetadata : null);
    }

    private async Task<YtDlpMediaInfo?> TryGetMediaInfoAsync(string url, CancellationToken ct)
    {
        try
        {
            return await GetMediaInfoAsync(url, ct);
        }
        catch (InvalidOperationException ex) when (IsNoMatchError(ex.Message))
        {
            return null;
        }
        catch
        {
            throw;
        }
    }

    private static bool IsNoMatchError(string message)
    {
        if (string.IsNullOrWhiteSpace(message))
            return false;

        return message.Contains("Unsupported URL", StringComparison.OrdinalIgnoreCase)
            || message.Contains("No suitable extractor", StringComparison.OrdinalIgnoreCase);
    }

    private async Task<YtDlpMediaInfo> GetMediaInfoAsync(string url, CancellationToken ct)
    {
        var command = await RunForUrlAsync(
            url,
            ["--skip-download", "--dump-single-json", "--no-playlist", "--no-warnings", url],
            ct);

        EnsureSuccess(command, "yt-dlp failed to extract metadata");

        try
        {
            using var document = JsonDocument.Parse(command.StandardOutput);
            var root = document.RootElement;
            var normalizedUrl = GetString(root, "webpage_url", "original_url") ?? url;
            var title = GetString(root, "title", "fulltitle") ?? OfficialDownloaderUtilities.DeriveTitleFromUrl(normalizedUrl, "Downloaded media");
            var mediaId = GetString(root, "id", "display_id");
            var (hasVideo, hasAudio) = DetectMediaCapabilities(root);

            return new YtDlpMediaInfo(
                normalizedUrl.Trim(),
                title.Trim(),
                string.IsNullOrWhiteSpace(mediaId) ? null : mediaId.Trim(),
                hasVideo,
                hasAudio,
                ExtractAvailableResolutions(root),
                BuildVideoMetadata(root, normalizedUrl.Trim(), title.Trim(), string.IsNullOrWhiteSpace(mediaId) ? null : mediaId.Trim()));
        }
        catch (JsonException ex)
        {
            throw new InvalidOperationException("yt-dlp returned invalid JSON for the URL.", ex);
        }
    }

    private IYtDlpCommandRunner GetRunner() => _runner ??= CreateRunner();

    private YtDlpSettings ReadSettings(string url)
    {
        // A login saved for this URL's site in Cove's downloader settings wins over the extension-wide
        // username/password, which apply to every site.
        var siteLogin = _services?.GetService<IDownloaderSiteLoginProvider>()?.FindForUrl(url);
        var cookiesPath = GetSetting("CookiesPath", "COVE_YTDLP_COOKIES", "YT_DLP_COOKIES");
        var cookiesFromBrowser = GetSetting("CookiesFromBrowser", "COVE_YTDLP_COOKIES_FROM_BROWSER", "YT_DLP_COOKIES_FROM_BROWSER");

        // Every yt-dlp run is a fresh process that signs in again, and sites block an account that signs
        // in hundreds of times in a row. Keeping the site's session in a cookie jar lets yt-dlp find the
        // session it already established and skip the sign-in: its login step fetches the login page first
        // and stops there when that page shows it is already signed in.
        var siteCookieJar = siteLogin != null && string.IsNullOrWhiteSpace(cookiesPath) && string.IsNullOrWhiteSpace(cookiesFromBrowser)
            ? GetSiteCookieJarPath(siteLogin.Site)
            : null;

        return new YtDlpSettings(
            GetSetting("Impersonate", "COVE_YTDLP_IMPERSONATE", "YT_DLP_IMPERSONATE"),
            cookiesPath,
            cookiesFromBrowser,
            GetSetting("Proxy", "COVE_YTDLP_PROXY", "YT_DLP_PROXY"),
            siteLogin != null ? siteLogin.Username : GetSetting("Username", "COVE_YTDLP_USERNAME", "YT_DLP_USERNAME"),
            siteLogin != null ? siteLogin.Password : GetSetting("Password", "COVE_YTDLP_PASSWORD", "YT_DLP_PASSWORD"),
            GetBooleanSetting("UseNetrc", "COVE_YTDLP_NETRC", "YT_DLP_NETRC"))
        {
            ThrottleSite = siteLogin?.Site,
            SiteCookieJar = siteCookieJar,
            LoginSavedAt = siteLogin?.SavedAt,
        };
    }

    /// <summary>
    /// The cookie jar yt-dlp reads and rewrites for one saved-login site. It holds a live session, so it
    /// lives beside the extension's own data and is readable only by the account Cove runs as.
    /// </summary>
    private string? GetSiteCookieJarPath(string site)
    {
        if (string.IsNullOrWhiteSpace(site))
            return null;

        var directory = Path.Combine(GetExtensionRoot(), "cookies");
        Directory.CreateDirectory(directory);
        if (!OperatingSystem.IsWindows())
            File.SetUnixFileMode(directory, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);

        var name = string.Join("_", site.Trim().ToLowerInvariant().Split(Path.GetInvalidFileNameChars()));
        return Path.Combine(directory, name + ".txt");
    }

    /// <summary>
    /// How long to leave between the starts of runs against one saved-login site. Reusing the session
    /// removed the need to pace sign-ins, so this is only a small gap; raise it if a site ever starts
    /// answering a batch with rate limiting.
    /// </summary>
    private TimeSpan GetSiteRequestInterval()
    {
        var configured = GetSetting("SiteRequestIntervalSeconds", "COVE_YTDLP_SITE_REQUEST_INTERVAL_SECONDS");
        return double.TryParse(configured, NumberStyles.Float, CultureInfo.InvariantCulture, out var seconds) && seconds >= 0
            ? TimeSpan.FromSeconds(seconds)
            : TimeSpan.FromMilliseconds(150);
    }

    /// <summary>
    /// Runs yt-dlp for a URL. Runs against a saved-login site go through <see cref="SiteSessionCoordinator"/>,
    /// which lets them overlap once a session is on file and stops them outright after a failed sign-in.
    /// URLs with no saved login run exactly as before.
    /// </summary>
    private async Task<YtDlpCommandResult> RunForUrlAsync(string url, IReadOnlyList<string> commandArguments, CancellationToken ct)
    {
        var settings = ReadSettings(url);
        if (string.IsNullOrWhiteSpace(settings.ThrottleSite) || string.IsNullOrWhiteSpace(settings.SiteCookieJar))
            return await GetRunner().RunAsync([.. settings.BuildArguments(), .. commandArguments], ct);

        using var run = await SiteSessions.BeginRunAsync(
            settings.ThrottleSite,
            settings.SiteCookieJar,
            SiteSessionCoordinator.FingerprintLogin(settings.Username, settings.Password, settings.LoginSavedAt),
            GetSiteRequestInterval(),
            ct);

        var command = await GetRunner().RunAsync(
            [.. (settings with { CookiesPath = run.CookieJar }).BuildArguments(), .. commandArguments],
            ct);

        if (command.ExitCode == 0)
            await run.KeepSessionAsync(ct);
        else if (SiteSessionCoordinator.SignInFailureReason(command) is { } reason)
            await run.PauseAfterFailedSignInAsync(reason, ct);

        return command;
    }

    private string? GetSetting(string key, params string[] environmentVariables)
    {
        return GetConfiguredSetting(
            _configuration,
            _services?.GetService<CoveConfiguration>(),
            Id,
            key,
            environmentVariables);
    }

    private bool GetBooleanSetting(string key, params string[] environmentVariables)
    {
        var value = GetSetting(key, environmentVariables);
        return bool.TryParse(value, out var parsed) && parsed;
    }

    private IYtDlpCommandRunner CreateRunner()
    {
        if (_services == null)
            throw new InvalidOperationException("The yt-dlp extension has not been initialized yet.");

        var loggerFactory = _services.GetService<ILoggerFactory>();
        var logger = loggerFactory?.CreateLogger<YtDlpDownloaderExtension>() ?? NullLogger<YtDlpDownloaderExtension>.Instance;
        var resolver = new YtDlpExecutableResolver(
            Id,
            GetExtensionRoot(),
            _configuration,
            _services.GetService<CoveConfiguration>(),
            _services.GetRequiredService<IHttpClientFactory>(),
            logger);

        return new ProcessYtDlpCommandRunner(resolver, logger);
    }

    private string GetExtensionRoot()
    {
        if (string.IsNullOrWhiteSpace(_extensionRoot) && _services != null)
            _extensionRoot = ResolveExtensionRoot(_services);

        if (string.IsNullOrWhiteSpace(_extensionRoot))
            throw new InvalidOperationException("The yt-dlp extension root directory could not be resolved.");

        return _extensionRoot;
    }

    private string ResolveExtensionRoot(IServiceProvider services)
    {
        var extensionsDataDirectory = services.GetService<ExtensionManager>()?.Context.DataDirectory;
        if (!string.IsNullOrWhiteSpace(extensionsDataDirectory))
            return Path.Combine(extensionsDataDirectory, Id);

        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        return Path.Combine(localAppData, "cove", "extensions", Id);
    }

    private static IReadOnlyList<DownloaderQualityOption> BuildQualityOptions(IReadOnlyList<VideoResolution> resolutions)
    {
        var best = resolutions.FirstOrDefault();
        var options = new List<DownloaderQualityOption>
        {
            new("best", "Best available", "Highest quality stream that yt-dlp can download")
            {
                Width = best?.Width,
                Height = best?.Height,
            }
        };

        foreach (var resolution in resolutions)
        {
            options.Add(new DownloaderQualityOption(
                $"max-height-{resolution.Height}",
                $"{resolution.Height}p",
                $"Best downloadable stream at or below {resolution.Height}p")
            {
                Width = resolution.Width,
                Height = resolution.Height,
            });
        }

        return options;
    }

    // Highest first. Downloads use single-file format selectors ("best"), so video-only streams are skipped:
    // listing them would advertise a resolution the download cannot deliver.
    private static IReadOnlyList<VideoResolution> ExtractAvailableResolutions(JsonElement root)
    {
        var widthsByHeight = new SortedDictionary<int, int?>(Comparer<int>.Create((left, right) => right.CompareTo(left)));
        if (root.TryGetProperty("formats", out var formatsElement) && formatsElement.ValueKind == JsonValueKind.Array)
        {
            foreach (var format in formatsElement.EnumerateArray())
            {
                if (!format.TryGetProperty("height", out var heightElement) || !heightElement.TryGetInt32(out var height) || height <= 0)
                    continue;

                if (IsNoneCodec(format, "vcodec") || IsNoneCodec(format, "acodec"))
                    continue;

                // Sites can list the same height twice (e.g. progressive mp4 without a width plus HLS with one).
                if (!widthsByHeight.TryGetValue(height, out var knownWidth) || knownWidth == null)
                    widthsByHeight[height] = GetPositiveInt(format, "width");
            }
        }

        if (widthsByHeight.Count == 0 && GetPositiveInt(root, "height") is { } fallbackHeight)
            widthsByHeight[fallbackHeight] = GetPositiveInt(root, "width");

        return widthsByHeight.Select(entry => new VideoResolution(entry.Key, entry.Value)).ToList();
    }

    private static bool IsNoneCodec(JsonElement format, string propertyName)
    {
        return format.TryGetProperty(propertyName, out var codecElement)
            && codecElement.ValueKind == JsonValueKind.String
            && string.Equals(codecElement.GetString(), "none", StringComparison.OrdinalIgnoreCase);
    }

    private static int? GetPositiveInt(JsonElement element, string propertyName)
    {
        return element.TryGetProperty(propertyName, out var valueElement) && valueElement.TryGetInt32(out var value) && value > 0
            ? value
            : null;
    }

    private static (bool HasVideo, bool HasAudio) DetectMediaCapabilities(JsonElement root)
    {
        var hasVideo = false;
        var hasAudio = false;

        if (root.TryGetProperty("formats", out var formatsElement) && formatsElement.ValueKind == JsonValueKind.Array)
        {
            foreach (var format in formatsElement.EnumerateArray())
            {
                var acodec = GetString(format, "acodec");
                if (CarriesVideo(format))
                    hasVideo = true;
                if (!string.IsNullOrWhiteSpace(acodec) && !string.Equals(acodec, "none", StringComparison.OrdinalIgnoreCase))
                    hasAudio = true;
            }
        }

        var rootAcodec = GetString(root, "acodec");
        hasVideo = hasVideo || CarriesVideo(root);
        hasAudio = hasAudio || (!string.IsNullOrWhiteSpace(rootAcodec) && !string.Equals(rootAcodec, "none", StringComparison.OrdinalIgnoreCase));

        return (hasVideo, hasAudio);
    }

    // yt-dlp marks a missing stream as "none" and omits the codec when it is unknown. Some sites (e.g. Pornhub's
    // progressive MP4s) never report codecs, so an unknown codec with a frame height still counts as video.
    private static bool CarriesVideo(JsonElement format)
    {
        var vcodec = GetString(format, "vcodec");
        if (vcodec != null)
            return !string.Equals(vcodec, "none", StringComparison.OrdinalIgnoreCase);

        return GetPositiveInt(format, "height") is not null;
    }

    private static string BuildVideoFormatSelector(string? qualityId)
    {
        if (string.IsNullOrWhiteSpace(qualityId) || string.Equals(qualityId, "best", StringComparison.OrdinalIgnoreCase))
            return "best";

        const string prefix = "max-height-";
        if (qualityId.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)
            && int.TryParse(qualityId[prefix.Length..], out var height)
            && height > 0)
        {
            return $"best[height<={height}]/best";
        }

        return "best";
    }

    private static ScrapedVideoDto BuildVideoMetadata(JsonElement root, string normalizedUrl, string title, string? mediaId)
    {
        var performerNames = ExtractStringArray(root, "cast");
        AddIfPresent(performerNames, GetString(root, "uploader", "creator"));

        var tagNames = ExtractStringArray(root, "tags");
        if (tagNames.Count == 0)
            tagNames = ExtractStringArray(root, "categories");

        return new ScrapedVideoDto
        {
            Title = title,
            Code = mediaId,
            Details = GetString(root, "description"),
            Date = FormatUploadDate(GetString(root, "upload_date"), root),
            ImageUrl = GetString(root, "thumbnail") ?? ExtractThumbnail(root),
            Urls = [normalizedUrl],
            StudioName = GetString(root, "channel", "channel_name"),
            PerformerNames = performerNames,
            TagNames = tagNames,
        };
    }

    private static string? ResolveVideoUrl(VideoScrapeInput input)
    {
        if (!string.IsNullOrWhiteSpace(input.Url) && OfficialDownloaderUtilities.IsHttpUrl(input.Url))
            return input.Url.Trim();

        return input.Urls.FirstOrDefault(OfficialDownloaderUtilities.IsHttpUrl)?.Trim();
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

    private static List<string> ExtractStringArray(JsonElement root, string propertyName)
    {
        if (!root.TryGetProperty(propertyName, out var value) || value.ValueKind != JsonValueKind.Array)
            return [];

        var values = new List<string>();
        foreach (var item in value.EnumerateArray())
        {
            if (item.ValueKind == JsonValueKind.String)
                AddIfPresent(values, item.GetString());
        }

        return values;
    }

    private static string? ExtractThumbnail(JsonElement root)
    {
        if (!root.TryGetProperty("thumbnails", out var thumbnails) || thumbnails.ValueKind != JsonValueKind.Array)
            return null;

        foreach (var thumbnail in thumbnails.EnumerateArray())
        {
            var url = GetString(thumbnail, "url");
            if (!string.IsNullOrWhiteSpace(url))
                return url;
        }

        return null;
    }

    private static string? FormatUploadDate(string? uploadDate, JsonElement root)
    {
        if (!string.IsNullOrWhiteSpace(uploadDate)
            && uploadDate.Length == 8
            && DateOnly.TryParseExact(uploadDate, "yyyyMMdd", null, System.Globalization.DateTimeStyles.None, out var parsed))
        {
            return parsed.ToString("yyyy-MM-dd");
        }

        if (root.TryGetProperty("timestamp", out var timestampElement) && timestampElement.TryGetInt64(out var timestamp))
            return DateTimeOffset.FromUnixTimeSeconds(timestamp).UtcDateTime.ToString("yyyy-MM-dd");

        return null;
    }

    private static void AddIfPresent(List<string> values, string? candidate)
    {
        if (string.IsNullOrWhiteSpace(candidate))
            return;

        var trimmed = candidate.Trim();
        if (!values.Contains(trimmed, StringComparer.OrdinalIgnoreCase))
            values.Add(trimmed);
    }

    private static string? FindDownloadedFile(string directory)
    {
        return Directory.EnumerateFiles(directory, "downloaded.*", SearchOption.TopDirectoryOnly)
            .Where(path => !path.EndsWith(".part", StringComparison.OrdinalIgnoreCase))
            .Where(path => !path.EndsWith(".ytdl", StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(path => File.GetLastWriteTimeUtc(path))
            .FirstOrDefault();
    }

    private static string BuildOriginalFileName(string title, string? mediaId, string extension, string fallbackExtension)
    {
        var safeExtension = string.IsNullOrWhiteSpace(extension) ? fallbackExtension : extension;
        var suffix = string.IsNullOrWhiteSpace(mediaId) ? string.Empty : $" [{mediaId}]";
        return OfficialDownloaderUtilities.SanitizeFileName($"{title}{suffix}{safeExtension}");
    }

    private static string FormatAvailableHeights(IReadOnlyList<int> heights)
    {
        return heights.Count == 0 ? "none" : string.Join(", ", heights);
    }

    private static void LogCommandResult(ILogger logger, string url, string operation, YtDlpCommandResult command, bool includeStandardOutput)
    {
        if (command.ExitCode == 0)
        {
            logger.LogDebug(
                "yt-dlp {Operation} command completed for {Url}. ExitCode: {ExitCode}.",
                operation,
                url,
                command.ExitCode);
            return;
        }

        logger.LogWarning(
            "yt-dlp {Operation} command failed for {Url}. ExitCode: {ExitCode}. stderr: {StandardError}. stdout: {StandardOutput}",
            operation,
            url,
            command.ExitCode,
            SummarizeProcessOutput(command.StandardError),
            includeStandardOutput ? SummarizeProcessOutput(command.StandardOutput) : string.Empty);
    }

    private static string SummarizeProcessOutput(string? output)
    {
        if (string.IsNullOrWhiteSpace(output))
            return string.Empty;

        var lines = output
            .Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(line => !string.IsNullOrWhiteSpace(line))
            .ToArray();
        if (lines.Length == 0)
            return string.Empty;

        var tail = lines.Length <= 8 ? lines : lines[^8..];
        var summary = string.Join(" | ", tail);
        return summary.Length <= 1500 ? summary : summary[..1500];
    }

    private static string FormatYtDlpArguments(IReadOnlyList<string> arguments)
    {
        var formatted = new List<string>();
        var redactNext = false;

        foreach (var argument in arguments)
        {
            if (redactNext)
            {
                formatted.Add("<redacted>");
                redactNext = false;
                continue;
            }

            var optionName = argument.Split('=', 2)[0];
            if (IsSensitiveYtDlpOption(optionName))
            {
                if (argument.Contains('=', StringComparison.Ordinal))
                {
                    formatted.Add($"{optionName}=<redacted>");
                }
                else
                {
                    formatted.Add(argument);
                    redactNext = true;
                }

                continue;
            }

            formatted.Add(argument);
        }

        return string.Join(' ', formatted.Select(QuoteArgumentForLog));
    }

    private static bool IsSensitiveYtDlpOption(string optionName)
    {
        return optionName.Equals("--password", StringComparison.OrdinalIgnoreCase)
            || optionName.Equals("--username", StringComparison.OrdinalIgnoreCase)
            || optionName.Equals("--cookies", StringComparison.OrdinalIgnoreCase)
            || optionName.Equals("--cookies-from-browser", StringComparison.OrdinalIgnoreCase)
            || optionName.Equals("--proxy", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Coordinates yt-dlp runs that sign in to the same saved-login site, with one goal: the site should
    /// never see what looks like repeated sign-ins.
    /// <list type="bullet">
    /// <item>Each run works on a private copy of the site's cookie jar, because yt-dlp rewrites its cookie
    /// file in place on exit and two processes on one file can leave a line the next run rejects. A run
    /// that succeeds publishes its copy back with an atomic replace, so runs can overlap safely.</item>
    /// <item>While no session is on file, runs take turns, so a cold batch signs in once rather than once
    /// per run.</item>
    /// <item>After a failed sign-in the site's session is discarded and every run with that login fails
    /// immediately, without touching the network, for <see cref="SignInFailurePause"/>. Without this a
    /// batch retries the sign-in on every video, which is exactly the pattern sites answer by locking the
    /// account. Saving a different username or password for the site lifts the pause.</item>
    /// </list>
    /// </summary>
    private sealed class SiteSessionCoordinator
    {
        /// <summary>Pornhub locks an account for an hour after too many sign-ins, so wait at least that long.</summary>
        public static readonly TimeSpan SignInFailurePause = TimeSpan.FromHours(1);

        private readonly Dictionary<string, SiteState> _sites = new(StringComparer.OrdinalIgnoreCase);

        /// <summary>
        /// Identifies one saved login. It includes when the login was last saved, so saving it again in Cove's
        /// settings, changed or not, counts as a new login and lifts a pause: that is how a person says "I fixed
        /// whatever made the sign-in fail, try again".
        /// </summary>
        public static string FingerprintLogin(string? username, string? password, DateTime? savedAt = null)
            => Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(
                System.Text.Encoding.UTF8.GetBytes($"{username}\n{password}\n{savedAt?.ToUniversalTime().Ticks}")));

        private static readonly string[] SignInFailureMarkers =
        [
            "Unable to login",
            "Unable to log in",
            "Login failed",
            "has been blocked",
            "Invalid username or password",
            "incorrect password",
        ];

        /// <summary>
        /// The site's own words when a run failed to sign in (yt-dlp's error line without its "ERROR: [Site] id:"
        /// prefix), or null when the failure was something else.
        /// </summary>
        public static string? SignInFailureReason(YtDlpCommandResult command)
        {
            var lines = string.Concat(command.StandardError, "\n", command.StandardOutput)
                .Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            var line = lines.LastOrDefault(candidate => SignInFailureMarkers.Any(marker => candidate.Contains(marker, StringComparison.OrdinalIgnoreCase)));
            if (line is null)
                return null;

            var reason = System.Text.RegularExpressions.Regex.Replace(line, @"^ERROR:\s*(\[[^\]]+\]\s*[^:]*:\s*)?", string.Empty).Trim();
            return reason.Length <= 300 ? reason : reason[..300] + "…";
        }

        public async Task<SiteRun> BeginRunAsync(string site, string sharedJar, string loginFingerprint, TimeSpan startInterval, CancellationToken ct)
        {
            SiteState state;
            lock (_sites)
            {
                if (!_sites.TryGetValue(site, out var existing))
                    _sites[site] = existing = new SiteState();

                state = existing;
            }

            ThrowIfPaused(state, site, loginFingerprint);

            // A run that may have to sign in waits for its turn. Once it has one, a session may have been
            // published (or a sign-in may have failed) while it waited, so look again.
            var holdsSignIn = false;
            if (!HasSession(sharedJar))
            {
                await state.SignIn.WaitAsync(ct);
                holdsSignIn = true;
                try
                {
                    ThrowIfPaused(state, site, loginFingerprint);
                }
                catch
                {
                    state.SignIn.Release();
                    throw;
                }

                if (HasSession(sharedJar))
                {
                    state.SignIn.Release();
                    holdsSignIn = false;
                }
            }

            try
            {
                await state.Jar.WaitAsync(ct);
                try
                {
                    var remaining = state.LastStarted + startInterval - DateTimeOffset.UtcNow;
                    if (remaining > TimeSpan.Zero)
                        await Task.Delay(remaining, ct);

                    state.LastStarted = DateTimeOffset.UtcNow;
                    var runJar = Path.Combine(
                        Path.GetDirectoryName(sharedJar)!,
                        $"{Path.GetFileNameWithoutExtension(sharedJar)}.run-{Guid.NewGuid():n}.txt");
                    if (File.Exists(sharedJar))
                        File.Copy(sharedJar, runJar);

                    return new SiteRun(state, sharedJar, runJar, loginFingerprint, holdsSignIn);
                }
                finally
                {
                    state.Jar.Release();
                }
            }
            catch
            {
                if (holdsSignIn)
                    state.SignIn.Release();

                throw;
            }
        }

        private static void ThrowIfPaused(SiteState state, string site, string loginFingerprint)
        {
            var failure = state.FailedSignIn;
            if (failure == null
                || failure.At + SignInFailurePause <= DateTimeOffset.UtcNow
                || !string.Equals(failure.LoginFingerprint, loginFingerprint, StringComparison.Ordinal))
            {
                return;
            }

            throw new InvalidOperationException(
                $"Signing in to {site} failed at {failure.At.ToLocalTime():t} (the site said: \"{failure.Reason}\"), so runs that use this login are paused until "
                + $"{(failure.At + SignInFailurePause).ToLocalTime():t} to avoid repeated sign-ins locking the account. "
                + "Once the cause is fixed, save the site login in Cove's downloader settings (even unchanged) to try again right away.");
        }

        private static bool HasSession(string jar) => File.Exists(jar) && new FileInfo(jar).Length > 0;

        public sealed class SiteState
        {
            public SemaphoreSlim SignIn { get; } = new(1, 1);

            public SemaphoreSlim Jar { get; } = new(1, 1);

            public DateTimeOffset LastStarted { get; set; } = DateTimeOffset.MinValue;

            public SignInFailure? FailedSignIn { get; set; }
        }

        public sealed record SignInFailure(DateTimeOffset At, string LoginFingerprint, string Reason);

        public sealed class SiteRun(SiteState state, string sharedJar, string runJar, string loginFingerprint, bool holdsSignIn) : IDisposable
        {
            private bool _disposed;

            /// <summary>The private cookie jar this run hands to yt-dlp.</summary>
            public string CookieJar => runJar;

            /// <summary>Publishes this run's session so later runs reuse it instead of signing in.</summary>
            public async Task KeepSessionAsync(CancellationToken ct)
            {
                if (!File.Exists(runJar))
                    return;

                await state.Jar.WaitAsync(ct);
                try
                {
                    var staged = sharedJar + ".publish";
                    File.Copy(runJar, staged, overwrite: true);
                    File.Move(staged, sharedJar, overwrite: true);
                    state.FailedSignIn = null;
                }
                finally
                {
                    state.Jar.Release();
                }
            }

            /// <summary>Discards the site's session and pauses every run that uses this login.</summary>
            public async Task PauseAfterFailedSignInAsync(string reason, CancellationToken ct)
            {
                await state.Jar.WaitAsync(ct);
                try
                {
                    state.FailedSignIn = new SignInFailure(DateTimeOffset.UtcNow, loginFingerprint, reason);
                    if (File.Exists(sharedJar))
                        File.Delete(sharedJar);
                }
                finally
                {
                    state.Jar.Release();
                }
            }

            public void Dispose()
            {
                if (_disposed)
                    return;

                _disposed = true;
                try
                {
                    if (File.Exists(runJar))
                        File.Delete(runJar);
                }
                finally
                {
                    if (holdsSignIn)
                        state.SignIn.Release();
                }
            }
        }
    }

    private static string QuoteArgumentForLog(string argument)
    {
        if (argument.Length == 0)
            return "\"\"";

        return argument.Any(char.IsWhiteSpace)
            ? $"\"{argument.Replace("\"", "\\\"")}" + "\""
            : argument;
    }

    private static void EnsureSuccess(YtDlpCommandResult command, string message)
    {
        if (command.ExitCode == 0)
            return;

        var fullDetail = string.IsNullOrWhiteSpace(command.StandardError) ? command.StandardOutput : command.StandardError;
        var detail = fullDetail.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).LastOrDefault() ?? string.Empty;
        detail = AddTroubleshootingGuidance(detail, fullDetail);
        throw new InvalidOperationException(string.IsNullOrWhiteSpace(detail) ? message : $"{message}: {detail}");
    }

    private static string AddTroubleshootingGuidance(string detail, string fullDetail)
    {
        if (fullDetail.Contains("impersonat", StringComparison.OrdinalIgnoreCase)
            || fullDetail.Contains("curl_cffi", StringComparison.OrdinalIgnoreCase))
        {
            return string.Concat(
                detail,
                " Configure Impersonate in the extension settings or COVE_YTDLP_IMPERSONATE, and use a yt-dlp build with impersonation support such as the official standalone binary or a Python install with curl_cffi support.");
        }

        if (fullDetail.Contains("PhantomJS not found", StringComparison.OrdinalIgnoreCase))
        {
            // Some sites (Pornhub among them) answer bursts of requests with a JavaScript challenge that yt-dlp
            // can only solve by running it in PhantomJS.
            return string.Concat(
                detail,
                " The site answered with a JavaScript challenge, which yt-dlp solves with PhantomJS 2.x. Install PhantomJS (phantomjs.org) somewhere on the PATH that Cove runs yt-dlp with, then retry.");
        }

        if (fullDetail.Contains("Unable to login", StringComparison.OrdinalIgnoreCase)
            || fullDetail.Contains("has been blocked", StringComparison.OrdinalIgnoreCase))
        {
            return string.Concat(
                detail,
                " The site refused the sign-in. Runs that use this login are paused for an hour so they do not keep retrying; once the cause is fixed, save the site login in Cove's downloader settings (even unchanged) to try again right away.");
        }

        if (fullDetail.Contains("HTTP Error 403", StringComparison.OrdinalIgnoreCase)
            || fullDetail.Contains("Forbidden", StringComparison.OrdinalIgnoreCase))
        {
            return string.Concat(
                detail,
                " If this site requires a logged-in or browser-like request, configure cookies, browser cookies, or impersonation in the extension settings or via the COVE_YTDLP_* environment variables.");
        }

        if (fullDetail.Contains("HTTP Error 410", StringComparison.OrdinalIgnoreCase)
            || fullDetail.Contains("410: Gone", StringComparison.OrdinalIgnoreCase)
            || fullDetail.Contains("410 Gone", StringComparison.OrdinalIgnoreCase))
        {
            return string.Concat(
                detail,
                " The site rejected the current extraction request. Update yt-dlp first; if the site now requires a browser session, configure cookies or impersonation in the extension settings or via the COVE_YTDLP_* environment variables.");
        }

        return detail;
    }

    private static string? GetConfiguredSetting(
        IConfiguration? configuration,
        CoveConfiguration? coveConfiguration,
        string extensionId,
        string key,
        params string[] environmentVariables)
    {
        foreach (var variable in environmentVariables)
        {
            var value = Environment.GetEnvironmentVariable(variable);
            if (!string.IsNullOrWhiteSpace(value))
                return value.Trim();
        }

        if (coveConfiguration?.PluginConfigurations.TryGetValue(extensionId, out var values) == true
            && values.TryGetValue(key, out var configuredValue))
        {
            var normalizedValue = NormalizeConfiguredValue(configuredValue);
            if (!string.IsNullOrWhiteSpace(normalizedValue))
                return normalizedValue;
        }

        var configured = configuration?[$"Extensions:{extensionId}:{key}"]
            ?? configuration?[$"Cove:PluginConfigurations:{extensionId}:{key}"];
        return string.IsNullOrWhiteSpace(configured) ? null : configured.Trim();
    }

    private static string? NormalizeConfiguredValue(object? value)
    {
        return value switch
        {
            null => null,
            string text => text.Trim(),
            bool boolean => boolean ? "true" : "false",
            JsonElement { ValueKind: JsonValueKind.String } element => element.GetString()?.Trim(),
            JsonElement { ValueKind: JsonValueKind.True } => "true",
            JsonElement { ValueKind: JsonValueKind.False } => "false",
            JsonElement { ValueKind: JsonValueKind.Number } element => element.ToString(),
            JsonElement { ValueKind: JsonValueKind.Null } => null,
            IFormattable formattable => formattable.ToString(null, CultureInfo.InvariantCulture)?.Trim(),
            _ => value.ToString()?.Trim(),
        };
    }

    private static async Task<YtDlpCommandResult> RunProcessAsync(string executable, IEnumerable<string> arguments, CancellationToken ct)
    {
        using var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = executable,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            }
        };

        foreach (var argument in arguments)
            process.StartInfo.ArgumentList.Add(argument);

        try
        {
            process.Start();
        }
        catch (Exception ex) when (ex is Win32Exception or FileNotFoundException)
        {
            throw new InvalidOperationException($"Unable to start yt-dlp using '{executable}'. Install yt-dlp, set COVE_YTDLP_PATH / YT_DLP_PATH, or allow the extension to download its managed copy.", ex);
        }

        using var registration = ct.Register(() =>
        {
            try
            {
                if (!process.HasExited)
                    process.Kill(entireProcessTree: true);
            }
            catch
            {
            }
        });

        var stdoutTask = process.StandardOutput.ReadToEndAsync();
        var stderrTask = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync(ct);

        return new YtDlpCommandResult(process.ExitCode, (await stdoutTask).Trim(), (await stderrTask).Trim());
    }

    private sealed record YtDlpMediaInfo(string NormalizedUrl, string Title, string? MediaId, bool HasVideo, bool HasAudio, IReadOnlyList<VideoResolution> AvailableResolutions, ScrapedVideoDto VideoMetadata);

    private sealed record VideoResolution(int Height, int? Width);

    private sealed class ProcessYtDlpCommandRunner(YtDlpExecutableResolver executableResolver, ILogger logger) : IYtDlpCommandRunner
    {
        public async Task<YtDlpCommandResult> RunAsync(IEnumerable<string> arguments, CancellationToken ct)
        {
            var argumentList = arguments.ToList();
            var executable = await executableResolver.ResolveAsync(ct);
            var isVersionProbe = argumentList.Count == 1 && string.Equals(argumentList[0], "--version", StringComparison.Ordinal);
            if (!isVersionProbe)
            {
                logger.LogInformation(
                    "Running yt-dlp executable {Executable}. Arguments: {Arguments}",
                    executable,
                    FormatYtDlpArguments(argumentList));
            }

            var result = await RunProcessAsync(executable, argumentList, ct);
            if (isVersionProbe)
            {
                logger.LogDebug("Resolved yt-dlp executable {Executable}. Version: {Version}", executable, result.StandardOutput);
            }
            else if (result.ExitCode == 0)
            {
                logger.LogInformation(
                    "yt-dlp process completed from {Executable}. ExitCode: {ExitCode}. stdoutLength: {StandardOutputLength}. stderrLength: {StandardErrorLength}. stderr: {StandardError}. stdout: {StandardOutput}",
                    executable,
                    result.ExitCode,
                    result.StandardOutput.Length,
                    result.StandardError.Length,
                    SummarizeProcessOutput(result.StandardError),
                    SummarizeProcessOutput(result.StandardOutput));
            }
            else
            {
                logger.LogWarning(
                    "yt-dlp process failed from {Executable}. ExitCode: {ExitCode}. stdoutLength: {StandardOutputLength}. stderrLength: {StandardErrorLength}. stderr: {StandardError}. stdout: {StandardOutput}",
                    executable,
                    result.ExitCode,
                    result.StandardOutput.Length,
                    result.StandardError.Length,
                    SummarizeProcessOutput(result.StandardError),
                    SummarizeProcessOutput(result.StandardOutput));
            }

            return result;
        }
    }

    private sealed class YtDlpExecutableResolver(
        string extensionId,
        string extensionRoot,
        IConfiguration? configuration,
        CoveConfiguration? coveConfiguration,
        IHttpClientFactory httpClientFactory,
        ILogger logger)
    {
        private readonly SemaphoreSlim _resolutionLock = new(1, 1);
        private string? _resolvedExecutable;

        public async Task<string> ResolveAsync(CancellationToken ct)
        {
            if (!string.IsNullOrWhiteSpace(_resolvedExecutable) && await IsUsableAsync(_resolvedExecutable, ct))
                return _resolvedExecutable;

            await _resolutionLock.WaitAsync(ct);
            try
            {
                if (!string.IsNullOrWhiteSpace(_resolvedExecutable) && await IsUsableAsync(_resolvedExecutable, ct))
                    return _resolvedExecutable;

                var configuredPath = GetConfiguredExecutable();
                if (!string.IsNullOrWhiteSpace(configuredPath))
                {
                    var normalizedConfiguredPath = Path.GetFullPath(configuredPath);
                    if (!await IsUsableAsync(normalizedConfiguredPath, ct))
                        throw new InvalidOperationException($"yt-dlp is configured at '{normalizedConfiguredPath}' but is not executable.");

                    _resolvedExecutable = normalizedConfiguredPath;
                    logger.LogInformation("Using configured yt-dlp executable: {Executable}", normalizedConfiguredPath);
                    await WarnWhenImpersonationIsMissingAsync(normalizedConfiguredPath, ct);
                    return normalizedConfiguredPath;
                }

                var managedPath = GetManagedBinaryPath();
                if (await IsUsableAsync(managedPath, ct) && await SupportsImpersonationAsync(managedPath, ct))
                {
                    _resolvedExecutable = managedPath;
                    logger.LogInformation("Using managed yt-dlp executable: {Executable}", managedPath);
                    return managedPath;
                }

                var pathExecutable = ResolveExecutableFromPath("yt-dlp") ?? "yt-dlp";
                if (await IsUsableAsync(pathExecutable, ct))
                {
                    _resolvedExecutable = pathExecutable;
                    logger.LogInformation("Using yt-dlp executable from PATH: {Executable}", pathExecutable);
                    await WarnWhenImpersonationIsMissingAsync(pathExecutable, ct);
                    return pathExecutable;
                }

                if (File.Exists(managedPath))
                {
                    logger.LogInformation(
                        "The managed yt-dlp copy at {Path} cannot run or cannot impersonate a browser, so it is being replaced with the current standalone build.",
                        managedPath);
                }

                _resolvedExecutable = await DownloadManagedBinaryAsync(ct);
                return _resolvedExecutable;
            }
            finally
            {
                _resolutionLock.Release();
            }
        }

        private string? GetConfiguredExecutable()
        {
            return GetConfiguredSetting(configuration, coveConfiguration, extensionId, "YtDlpPath", "COVE_YTDLP_PATH", "YT_DLP_PATH");
        }

        private string GetManagedBinaryPath()
        {
            var fileName = OperatingSystem.IsWindows() ? "yt-dlp.exe" : "yt-dlp";
            return Path.Combine(extensionRoot, "tools", fileName);
        }

        /// <summary>
        /// Picks the yt-dlp release asset to download. The plain "yt-dlp" asset is a zipimport build that
        /// needs a system Python and ships without curl_cffi, so it can never impersonate a browser. The
        /// PyInstaller standalone builds named here carry their own Python and curl_cffi, which is what
        /// sites that only serve their higher resolutions to browser-shaped requests require.
        /// </summary>
        private static string GetManagedBinaryAsset()
        {
            if (OperatingSystem.IsWindows())
                return RuntimeInformation.OSArchitecture == Architecture.X86 ? "yt-dlp_x86.exe" : "yt-dlp.exe";

            return RuntimeInformation.OSArchitecture switch
            {
                Architecture.X64 => "yt-dlp_linux",
                Architecture.Arm64 => "yt-dlp_linux_aarch64",
                var architecture => throw new InvalidOperationException(
                    $"yt-dlp publishes no standalone build for {architecture} Linux. Install yt-dlp with curl_cffi support manually and set COVE_YTDLP_PATH or YT_DLP_PATH."),
            };
        }

        /// <summary>
        /// True when the executable can impersonate a browser. yt-dlp lists its impersonation targets only
        /// when a provider (curl_cffi) is present, so an empty list means --impersonate will always fail.
        /// </summary>
        private async Task<bool> SupportsImpersonationAsync(string executable, CancellationToken ct)
        {
            try
            {
                var result = await RunProcessAsync(executable, ["--list-impersonate-targets"], ct);
                return result.ExitCode == 0
                    && result.StandardOutput.Contains("curl_cffi", StringComparison.OrdinalIgnoreCase);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogDebug(ex, "Could not list impersonation targets for {Executable}.", executable);
                return false;
            }
        }

        private async Task WarnWhenImpersonationIsMissingAsync(string executable, CancellationToken ct)
        {
            if (await SupportsImpersonationAsync(executable, ct))
                return;

            logger.LogWarning(
                "The yt-dlp at {Executable} reports no impersonation targets, so downloads from sites that require browser impersonation will fail. Install yt-dlp with curl_cffi (pip install \"yt-dlp[default,curl-cffi]\") or one of the official standalone builds.",
                executable);
        }

        private static string? ResolveExecutableFromPath(string executable)
        {
            if (Path.IsPathRooted(executable)
                || executable.Contains(Path.DirectorySeparatorChar)
                || executable.Contains(Path.AltDirectorySeparatorChar))
            {
                return File.Exists(executable) ? executable : null;
            }

            var pathValue = Environment.GetEnvironmentVariable("PATH");
            if (string.IsNullOrWhiteSpace(pathValue))
                return null;

            var extensions = OperatingSystem.IsWindows()
                ? (Environment.GetEnvironmentVariable("PATHEXT") ?? ".COM;.EXE;.BAT;.CMD")
                    .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                : [string.Empty];

            if (Path.HasExtension(executable))
                extensions = [string.Empty];

            foreach (var directory in pathValue.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                foreach (var extension in extensions)
                {
                    var candidate = Path.Combine(directory, executable + extension.ToLowerInvariant());
                    if (File.Exists(candidate))
                        return candidate;

                    candidate = Path.Combine(directory, executable + extension.ToUpperInvariant());
                    if (File.Exists(candidate))
                        return candidate;
                }
            }

            return null;
        }

        private async Task<bool> IsUsableAsync(string executable, CancellationToken ct)
        {
            try
            {
                if (Path.IsPathRooted(executable))
                {
                    if (!File.Exists(executable))
                        return false;

                    EnsureExecutablePermissions(executable);
                }

                var result = await RunProcessAsync(executable, ["--version"], ct);
                return result.ExitCode == 0;
            }
            catch
            {
                return false;
            }
        }

        private async Task<string> DownloadManagedBinaryAsync(CancellationToken ct)
        {
            if (!OperatingSystem.IsWindows() && !OperatingSystem.IsLinux())
            {
                throw new InvalidOperationException(
                    "Automatic yt-dlp provisioning is currently supported only on Windows and Linux. Install yt-dlp manually and set COVE_YTDLP_PATH or YT_DLP_PATH.");
            }

            var asset = GetManagedBinaryAsset();
            var url = $"https://github.com/yt-dlp/yt-dlp/releases/latest/download/{asset}";
            var fileName = Path.GetFileName(GetManagedBinaryPath());

            var toolsDir = Path.Combine(extensionRoot, "tools");
            Directory.CreateDirectory(toolsDir);

            var finalPath = Path.Combine(toolsDir, fileName);
            var tempPath = Path.Combine(toolsDir, fileName + ".tmp");
            logger.LogInformation("yt-dlp was not found on PATH. Downloading a managed copy for extension {ExtensionId} to {Path}", extensionId, finalPath);

            try
            {
                using var client = httpClientFactory.CreateClient();
                using var response = await client.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct);
                response.EnsureSuccessStatusCode();

                await using (var input = await response.Content.ReadAsStreamAsync(ct))
                await using (var output = File.Create(tempPath))
                {
                    await input.CopyToAsync(output, ct);
                }

                EnsureExecutablePermissions(tempPath);

                if (File.Exists(finalPath))
                    File.Delete(finalPath);

                File.Move(tempPath, finalPath);
                EnsureExecutablePermissions(finalPath);

                if (!await IsUsableAsync(finalPath, ct))
                    throw new InvalidOperationException($"yt-dlp was downloaded to '{finalPath}' but could not be executed.");

                if (!await SupportsImpersonationAsync(finalPath, ct))
                {
                    logger.LogWarning(
                        "The yt-dlp build downloaded from {Url} reports no impersonation targets, so sites that require browser impersonation will fail. Install a yt-dlp with curl_cffi support and point COVE_YTDLP_PATH at it.",
                        url);
                }

                return finalPath;
            }
            catch (Exception ex) when (ex is HttpRequestException or IOException or UnauthorizedAccessException)
            {
                throw new InvalidOperationException(
                    "yt-dlp is not installed and the extension could not download its managed copy. Install yt-dlp on PATH, set COVE_YTDLP_PATH / YT_DLP_PATH, or in Docker bake yt-dlp into the image or allow the extension directory to persist a downloaded copy.",
                    ex);
            }
            finally
            {
                if (File.Exists(tempPath))
                    File.Delete(tempPath);
            }
        }

        private static void EnsureExecutablePermissions(string filePath)
        {
            if (OperatingSystem.IsWindows() || !File.Exists(filePath))
                return;

            File.SetUnixFileMode(
                filePath,
                UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute |
                UnixFileMode.GroupRead | UnixFileMode.GroupExecute |
                UnixFileMode.OtherRead | UnixFileMode.OtherExecute);
        }
    }
}
