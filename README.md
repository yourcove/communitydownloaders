# Cove Community Downloaders

Community-maintained downloader extensions published through the official Cove extension registry.

## Extensions

- `cove.community.downloaders` - manifest-only bundle that installs the common downloader set.
- `cove.community.downloaders.common-audio` - Soundgasm and Whyp audio downloads.
- `cove.community.downloaders.common-text` - Literotica text downloads.
- `cove.community.downloaders.reddit` - Reddit-hosted media downloads and downloader delegation for linked posts.
- `cove.community.downloaders.ytdlp` - generic video/audio downloads through `yt-dlp`.

## Development

Clone this repository beside `cove` to build against local Cove contracts, or set `UseLocalCovePlugins=false` to consume published packages.

```powershell
npm run validate:extensions
dotnet build extensions/CommonAudioDownloader/CommonAudioDownloader.csproj
dotnet build extensions/CommonTextDownloader/CommonTextDownloader.csproj
dotnet build extensions/RedditDownloader/RedditDownloader.csproj
dotnet build extensions/YtDlpDownloader/YtDlpDownloader.csproj
```

## Releases

Create release tags with the lowercase `tagPrefix` from `extensions/catalog.json`, for example `common/v1.0.0` or `ytdlp/v1.0.0`.

The workflow accepts any `<tagPrefix>v<semver>` tag, packages only the matching catalog entry, and uploads a zip named `<extension-id>-<version>.zip` for the registry.
