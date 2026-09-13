# Stream Arc TV for Windows

The Windows edition of Stream Arc TV: the same screens and features as the Android TV / phone app,
built with WPF on .NET 8 and playing through LibVLC.

<p align="center"><img src="StreamArcTV/Assets/tv_banner.png" width="320" alt="Stream Arc TV"></p>

## Features

Everything the Android app does, on a Windows PC:

- Home screen with the **Live TV** and **Video on Demand** cards, each with its own server and sign-in,
  the expiry date of each account, round Update and Settings buttons, and the Home / Search /
  Downloads / Recordings / Profile bar. The TV/tablet and Phone layouts are both there
  (asked on first launch, changeable under Settings → Display).
- **TV guide**: channels down the side with logos, a time ruler with a "now" marker, programme blocks
  sized by duration, a details panel with the focused programme, and a live preview box (Enter on a
  channel previews it, Enter again goes full screen). Favorites, hidden categories, a default
  category, and the whole-guide / per-channel guide source setting.
- **Multi-view**: two or four live channels at once; the highlighted tile carries the sound.
- **Video on Demand home** in the style of a streaming service: featured banner, Continue watching,
  Next episodes, My List, New movies, New series and genre rows, with a side rail (or a tab row when
  the window is taller than wide). Categories and Search open the poster grid browser with every
  provider category and genre group.
- **Series** pages with seasons, episodes, watched marks and the Download options (next 3 / 5 / 10
  unwatched, all unwatched, entire series, or a season).
- **Player**: HLS or MPEG-TS live streams, the LIVE / Paused / behind-live badge, skip back and
  forward 10 s, playback buffer levels including the 20 min / 30 min / 1 hour storage buffers
  (timeshift kept on disk), resume-where-you-left-off, auto-play the next episode with the
  10-second countdown and "Still watching?", subtitle track choice (CC), automatic reconnects,
  and the buffering diagnostics line.
- **Downloads and recordings**: one download at a time with automatic retries and resume, downloads
  pause while you watch, recordings by start and end clock time that start on their own even when
  the app is closed (a Windows scheduled task starts it in the background), Delete all, the folder
  setting, and a tray icon that keeps transfers running when the window is closed.
- **Settings**: exactly the Android list (updates, playback, live TV, display, storage, accounts,
  about, category diagnostics, export logs).
- **Self-updating**: the Update button (and an optional check on launch) reads this repository's
  Windows release feed, shows the changelog, downloads the package with a progress dialog, verifies
  its SHA-256, and installs it (silently through the setup program, or in place for a portable
  copy). You can skip any version.
- **Crash safety net**: if the app ever crashes, the next start offers to save or copy the log.

Keyboard: arrow keys move the highlight (like a remote), Enter is OK, **holding Enter** (or a
right-click, or the context-menu key) is "hold OK" for the item menus, Escape / Backspace go back,
F11 toggles full screen, Space pauses in the player and Left / Right skip 10 s while the controls
are hidden.

## Install

1. Download `Stream-Arc-TV-Setup-<version>.exe` from the [Releases](../../../releases) page (the
   releases tagged `windows-v…`) and run it. It installs for the current user without asking for
   administrator rights, adds Start menu and desktop shortcuts and an Apps & features entry, and can
   start the app when it finishes. Nothing else needs installing: .NET and LibVLC are included.
2. Windows SmartScreen may warn the first time because the build is not code-signed; choose
   "More info" → "Run anyway".
3. Future updates install from inside the app: it downloads the new setup program, verifies it,
   closes, installs and starts again.

Prefer no installer? `Stream-Arc-TV-Windows-<version>-x64.zip` is the same build as a portable
folder: unpack it anywhere you have write access and run `StreamArcTV.exe`; in-app updates then
swap the files in place.

Uninstall from Settings → Apps (or the Start menu entry). Your settings, downloads and recordings
under your profile are kept.

Requires Windows 10 or 11, 64-bit.

## Configuration

The version, update repository and the two Xtream Codes server addresses live in
`windows/Directory.Build.props`:

```xml
<Version>1.0.0</Version>
<GitHubRepo>ComputerGarage1837/StreamArcTV</GitHubRepo>
<LiveUrl>https://mediahere.ca/</LiveUrl>
<VodUrl>https://onlypuds.fans:2083/</VodUrl>
```

Users only ever enter a username and password; the server is chosen by the card they press.

Build locally (Windows, or any OS with the .NET 8 SDK for a compile check):

```
dotnet build windows/StreamArcTV/StreamArcTV.csproj -c Release
```

Publish a runnable folder:

```
dotnet publish windows/StreamArcTV/StreamArcTV.csproj -c Release -r win-x64 --self-contained -o out/StreamArcTV
```

Settings, accounts, watch progress, the transfer list, logs and caches live under
`%LocalAppData%\StreamArcTV`. Downloads and recordings go to `Videos\Stream Arc TV\Downloads` and
`…\Recordings` unless you pick another folder.

## Releasing

Releases are built and published by GitHub Actions (`.github/workflows/windows.yml`). No secrets
are needed (the build is unsigned).

To ship a version:

1. Bump `<Version>` in `windows/Directory.Build.props` (the version code is
   `major*10000 + minor*100 + patch` and must exceed the `versionCode` in `release/update-windows.json`).
2. Add a `## vX.Y.Z — date` section to `windows/CHANGELOG.md`; it becomes the release notes shown
   in the app.
3. Commit and push to `main` (or merge a pull request).

When `main` carries a version that has no `windows-vX.Y.Z` release yet, the workflow publishes the
setup program (built with Inno Setup from `windows/installer/StreamArcTV.iss`) and the portable
x64 zip on a release named "Stream Arc TV for Windows X.Y.Z", then commits the matching
`release/update-windows.json` (with SHA-256 and size of each) to `main`. Pushing a `windows-v*`
tag by hand or running the workflow manually does the same. Pull requests and pushes that don't
bump the version only build the app as a check.

## Update format

`release/update-windows.json` uses the same schema as the Android feed, with `setup` and `zip`
entries instead of `apk`:

```json
{
  "schemaVersion": 1,
  "packageName": "com.computergarage.streamarctv.windows",
  "versionName": "1.1.0",
  "versionCode": 10100,
  "setup": {
    "assetName": "Stream-Arc-TV-Setup-1.1.0.exe",
    "sha256": "64 lowercase hexadecimal characters",
    "sizeBytes": 12345678
  },
  "zip": {
    "assetName": "Stream-Arc-TV-Windows-1.1.0-x64.zip",
    "sha256": "64 lowercase hexadecimal characters",
    "sizeBytes": 12345678
  }
}
```

The app compares `versionCode` with its own and downloads the matching asset from the release
tagged `windows-v<versionName>`: an installed copy takes `setup` and runs it silently, a portable
copy takes `zip` and swaps the files in its own folder; `sha256` and `sizeBytes` are verified first.
`setup` is optional (feeds older than 1.1.0 have none), `zip` is required. A `versionCode` of 0
means no Windows release has been published yet.

## Disclaimer

Stream Arc TV is a media player. It does not provide, host or distribute any content; users are
responsible for the services they connect to.
