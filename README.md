# Cove Community Downloaders

Community-maintained downloader extensions published through the official Cove extension registry.

## Extensions

- `cove.community.downloaders` - manifest-only bundle that installs the common downloader set.
- `cove.community.downloaders.common-audio` - Soundgasm and Whyp audio downloads.
- `cove.community.downloaders.common-text` - Literotica text downloads.
- `cove.community.downloaders.reddit` - Reddit-hosted media downloads and downloader delegation for linked posts.
- `cove.community.downloaders.ytdlp` - generic video/audio downloads through `yt-dlp`.

## Signing in to a site

Cove's downloader settings hold per-site logins, and the yt-dlp extension uses the one that matches
the URL it is downloading (it wins over the extension-wide `Username` / `Password` settings).

Two things follow from a site login, because every yt-dlp run is a separate process that would
otherwise sign in again:

- The site's session is kept in a cookie jar under the extension's data directory, so yt-dlp finds
  the session it already has and skips the sign-in. Each run works on a private copy and a successful
  run publishes its copy back, because yt-dlp rewrites its cookie file in place when it exits.
- Until a session is on file, runs against the site take turns, so a batch signs in once. After that
  they overlap freely, starting at least `siteRequestIntervalSeconds` apart (default 0.15, also
  settable as `COVE_YTDLP_SITE_REQUEST_INTERVAL_SECONDS`).
- After a failed sign-in the session is discarded and runs that use the login fail immediately for an
  hour instead of retrying on every item, which is the pattern sites answer by locking the account.
  The pause message quotes the site's reply. Saving the site login again in Cove's settings, even
  unchanged, lifts the pause; so does restarting Cove.

The extension provisions yt-dlp's standalone build for the current platform, which bundles curl_cffi
so `--impersonate` works. Sites that only serve their higher resolutions to a signed-in browser need
both the login and impersonation.

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
