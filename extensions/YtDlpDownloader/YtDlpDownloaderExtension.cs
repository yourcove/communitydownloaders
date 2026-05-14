using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
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
    private const string ExtensionId = "cove.official.downloaders.ytdlp";
    private const string VideoDownloaderId = "cove.official.downloaders.ytdlp/video";
    private const string AudioDownloaderId = "cove.official.downloaders.ytdlp/audio";

    private static readonly DownloaderDescriptor VideoDownloader = new(
        VideoDownloaderId,
        "yt-dlp Video",
        DownloaderEntity.Scene,
        ["https://*/*", "http://*/*"],
        DownloaderCapabilities.MultiQuality | DownloaderCapabilities.ResumeSupported | DownloaderCapabilities.InlineMetadata);

    private static readonly DownloaderDescriptor AudioDownloader = new(
        AudioDownloaderId,
        "yt-dlp Audio",
        DownloaderEntity.Audio,
        ["https://*/*", "http://*/*"],
        DownloaderCapabilities.ResumeSupported);

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
    public string Version => "1.0.0";
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
                BuildQualityOptions(info.AvailableHeights),
                info.Title));
        }

        if (info.HasAudio && !info.HasVideo)
            matches.Add(new DownloaderUrlMatch(AudioDownloader.Id, info.NormalizedUrl, null, info.Title));

        return matches;
    }

    public async Task<DownloaderResult?> DownloadAsync(DownloaderRequest request, IDownloaderHost host, CancellationToken ct)
    {
        if (string.Equals(request.DownloaderId, VideoDownloader.Id, StringComparison.OrdinalIgnoreCase))
            return await DownloadMediaAsync(request, host, DownloaderEntity.Scene, ct);

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

        host.ReportProgress(0.05d, "Resolving yt-dlp metadata...");
        var info = await GetMediaInfoAsync(request.Url, ct);
        if (expectedEntity == DownloaderEntity.Scene && !info.HasVideo)
            throw new InvalidOperationException("yt-dlp did not report a downloadable video stream for this URL.");

        if (expectedEntity == DownloaderEntity.Audio && !info.HasAudio)
            throw new InvalidOperationException("yt-dlp did not report a downloadable audio stream for this URL.");

        host.ReportProgress(0.15d, $"Downloading {info.Title}...");
        var outputTemplate = Path.Combine(host.TempDirectory, "downloaded.%(ext)s");
        var command = await GetRunner().RunAsync(
            BuildYtDlpArguments([
                "--no-playlist",
                "--no-warnings",
                "--newline",
                "--no-part",
                "--output",
                outputTemplate,
                "--format",
                expectedEntity == DownloaderEntity.Scene ? BuildVideoFormatSelector(request.QualityId) : "bestaudio/best",
                info.NormalizedUrl,
            ]),
            ct);

        EnsureSuccess(command, "yt-dlp failed to download the media");

        var downloadedFile = FindDownloadedFile(host.TempDirectory)
            ?? throw new InvalidOperationException("yt-dlp completed successfully but did not leave a downloaded media file in the temp directory.");

        host.ReportProgress(0.95d, "Download completed.");
        var originalFilename = BuildOriginalFileName(info.Title, info.MediaId, Path.GetExtension(downloadedFile), expectedEntity == DownloaderEntity.Audio ? ".m4a" : ".mp4");
        return new DownloaderResult(
            Path.GetFileName(downloadedFile),
            originalFilename,
            InlineSceneMetadata: expectedEntity == DownloaderEntity.Scene ? info.SceneMetadata : null);
    }

    private async Task<YtDlpMediaInfo?> TryGetMediaInfoAsync(string url, CancellationToken ct)
    {
        try
        {
            return await GetMediaInfoAsync(url, ct);
        }
        catch
        {
            return null;
        }
    }

    private async Task<YtDlpMediaInfo> GetMediaInfoAsync(string url, CancellationToken ct)
    {
        var command = await GetRunner().RunAsync(
            BuildYtDlpArguments(["--skip-download", "--dump-single-json", "--no-playlist", "--no-warnings", url]),
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
                ExtractAvailableHeights(root),
                BuildSceneMetadata(root, normalizedUrl.Trim(), title.Trim(), string.IsNullOrWhiteSpace(mediaId) ? null : mediaId.Trim()));
        }
        catch (JsonException ex)
        {
            throw new InvalidOperationException("yt-dlp returned invalid JSON for the URL.", ex);
        }
    }

    private IYtDlpCommandRunner GetRunner() => _runner ??= CreateRunner();

    private IReadOnlyList<string> BuildYtDlpArguments(IReadOnlyList<string> commandArguments)
    {
        var args = new List<string>();
        args.AddRange(ReadSettings().BuildArguments());
        args.AddRange(commandArguments);
        return args;
    }

    private YtDlpSettings ReadSettings()
    {
        return new YtDlpSettings(
            GetSetting("Impersonate", "COVE_YTDLP_IMPERSONATE", "YT_DLP_IMPERSONATE"),
            GetSetting("CookiesPath", "COVE_YTDLP_COOKIES", "YT_DLP_COOKIES"),
            GetSetting("CookiesFromBrowser", "COVE_YTDLP_COOKIES_FROM_BROWSER", "YT_DLP_COOKIES_FROM_BROWSER"),
            GetSetting("Proxy", "COVE_YTDLP_PROXY", "YT_DLP_PROXY"),
            GetSetting("Username", "COVE_YTDLP_USERNAME", "YT_DLP_USERNAME"),
            GetSetting("Password", "COVE_YTDLP_PASSWORD", "YT_DLP_PASSWORD"),
            GetBooleanSetting("UseNetrc", "COVE_YTDLP_NETRC", "YT_DLP_NETRC"));
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

        return new ProcessYtDlpCommandRunner(resolver);
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

    private static IReadOnlyList<DownloaderQualityOption> BuildQualityOptions(IReadOnlyList<int> heights)
    {
        var options = new List<DownloaderQualityOption>
        {
            new("best", "Best available", "Highest quality stream that yt-dlp can download")
        };

        foreach (var height in heights)
        {
            options.Add(new DownloaderQualityOption(
                $"max-height-{height}",
                $"{height}p",
                $"Best downloadable stream at or below {height}p"));
        }

        return options;
    }

    private static IReadOnlyList<int> ExtractAvailableHeights(JsonElement root)
    {
        var heights = new SortedSet<int>(Comparer<int>.Create((left, right) => right.CompareTo(left)));
        if (root.TryGetProperty("formats", out var formatsElement) && formatsElement.ValueKind == JsonValueKind.Array)
        {
            foreach (var format in formatsElement.EnumerateArray())
            {
                if (!format.TryGetProperty("height", out var heightElement) || !heightElement.TryGetInt32(out var height) || height <= 0)
                    continue;

                if (format.TryGetProperty("vcodec", out var videoCodecElement) && string.Equals(videoCodecElement.GetString(), "none", StringComparison.OrdinalIgnoreCase))
                    continue;

                heights.Add(height);
            }
        }

        if (heights.Count == 0 && root.TryGetProperty("height", out var fallbackHeightElement) && fallbackHeightElement.TryGetInt32(out var fallbackHeight) && fallbackHeight > 0)
            heights.Add(fallbackHeight);

        return heights.ToList();
    }

    private static (bool HasVideo, bool HasAudio) DetectMediaCapabilities(JsonElement root)
    {
        var hasVideo = false;
        var hasAudio = false;
        if (!root.TryGetProperty("formats", out var formatsElement) || formatsElement.ValueKind != JsonValueKind.Array)
        {
            var vcodec = GetString(root, "vcodec");
            var acodec = GetString(root, "acodec");
            return (!string.IsNullOrWhiteSpace(vcodec) && !string.Equals(vcodec, "none", StringComparison.OrdinalIgnoreCase),
                !string.IsNullOrWhiteSpace(acodec) && !string.Equals(acodec, "none", StringComparison.OrdinalIgnoreCase));
        }

        foreach (var format in formatsElement.EnumerateArray())
        {
            var vcodec = GetString(format, "vcodec");
            var acodec = GetString(format, "acodec");
            if (!string.IsNullOrWhiteSpace(vcodec) && !string.Equals(vcodec, "none", StringComparison.OrdinalIgnoreCase))
                hasVideo = true;
            if (!string.IsNullOrWhiteSpace(acodec) && !string.Equals(acodec, "none", StringComparison.OrdinalIgnoreCase))
                hasAudio = true;
        }

        return (hasVideo, hasAudio);
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

    private static ScrapedSceneDto BuildSceneMetadata(JsonElement root, string normalizedUrl, string title, string? mediaId)
    {
        var performerNames = ExtractStringArray(root, "cast");
        AddIfPresent(performerNames, GetString(root, "uploader", "creator"));

        var tagNames = ExtractStringArray(root, "tags");
        if (tagNames.Count == 0)
            tagNames = ExtractStringArray(root, "categories");

        return new ScrapedSceneDto
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

    private static string? ResolveSceneUrl(SceneScrapeInput input)
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

        if (fullDetail.Contains("HTTP Error 403", StringComparison.OrdinalIgnoreCase)
            || fullDetail.Contains("Forbidden", StringComparison.OrdinalIgnoreCase))
        {
            return string.Concat(
                detail,
                " If this site requires a logged-in or browser-like request, configure cookies, browser cookies, or impersonation in the extension settings or via the COVE_YTDLP_* environment variables.");
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

    private sealed record YtDlpMediaInfo(string NormalizedUrl, string Title, string? MediaId, bool HasVideo, bool HasAudio, IReadOnlyList<int> AvailableHeights, ScrapedSceneDto SceneMetadata);

    private sealed class ProcessYtDlpCommandRunner(YtDlpExecutableResolver executableResolver) : IYtDlpCommandRunner
    {
        public async Task<YtDlpCommandResult> RunAsync(IEnumerable<string> arguments, CancellationToken ct)
        {
            var executable = await executableResolver.ResolveAsync(ct);
            return await RunProcessAsync(executable, arguments, ct);
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
                    return normalizedConfiguredPath;
                }

                var managedPath = GetManagedBinaryPath();
                if (await IsUsableAsync(managedPath, ct))
                {
                    _resolvedExecutable = managedPath;
                    return managedPath;
                }

                if (await IsUsableAsync("yt-dlp", ct))
                {
                    _resolvedExecutable = "yt-dlp";
                    return "yt-dlp";
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

            var (url, fileName) = OperatingSystem.IsWindows()
                ? ("https://github.com/yt-dlp/yt-dlp/releases/latest/download/yt-dlp.exe", "yt-dlp.exe")
                : ("https://github.com/yt-dlp/yt-dlp/releases/latest/download/yt-dlp", "yt-dlp");

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
