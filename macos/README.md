# Stream Arc TV for macOS

The macOS edition of Stream Arc TV: the same screens and features as the Android TV / phone app
and the Windows app, built with Avalonia on .NET 8 and playing through LibVLC. It runs natively on
Apple silicon and Intel Macs.

<p align="center"><img src="StreamArcTV/Assets/tv_banner.png" width="320" alt="Stream Arc TV"></p>

## Features

Everything the Android and Windows apps do, on a Mac:

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
  and the buffering diagnostics line. Hardware decoding through VideoToolbox.
- **Downloads and recordings**: one download at a time with automatic retries and resume, downloads
  pause while you watch, recordings by start and end clock time that start on their own even when
  the app is closed (a launchd agent starts it in the background), Delete all, the folder
  setting, and a menu-bar icon that keeps transfers running when the window is closed.
- **Settings**: exactly the Android list (updates, playback, live TV, display, storage, accounts,
  about, category diagnostics, export logs).
- **Self-updating**: the Update button (and an optional check on launch) reads this repository's
  macOS release feed, shows the changelog, downloads the package for this Mac's architecture with a
  progress dialog, verifies its SHA-256, swaps the new app in and relaunches. You can skip any version.
- **Crash safety net**: if the app ever crashes, the next start offers to save or copy the log.

Keyboard: arrow keys move the highlight (like a remote), Enter is OK, **holding Enter** (or a
right-click / Control-click) is "hold OK" for the item menus, Escape / Delete go back, F11 or
Control-Command-F toggles full screen, Space pauses in the player and Left / Right skip 10 s while
the controls are hidden. Command-Q quits, even while downloads are running.

## Install

1. Download `Stream-Arc-TV-<version>-macOS-arm64.dmg` (Apple silicon: M1 and later) or
   `…-macOS-x64.dmg` (Intel) from the [Releases](../../../releases) page (the releases tagged
   `macos-v…`), open it and drag **Stream Arc TV** into your Applications folder. Nothing else
   needs installing: .NET and LibVLC are inside the app.
2. The app is not notarized with Apple, so the first launch is blocked with "Apple could not verify…".
   Open **System Settings → Privacy & Security**, scroll down and press **Open Anyway** next to the
   Stream Arc TV notice (older macOS: right-click the app → Open → Open). This is needed once.
   Alternatively, in Terminal: `xattr -dr com.apple.quarantine "/Applications/Stream Arc TV.app"`.
3. Future updates install from inside the app: it downloads the new version, verifies it, replaces
   itself and starts again. Keep the app in a folder your account can write to (Applications is fine
   for an administrator account; otherwise use the Applications folder inside your home folder).

Uninstall by moving the app to the Bin. Your settings, downloads and recordings are kept (see below);
scheduled recordings are launchd agents named `com.computergarage.streamarctv.recording.*` under
`~/Library/LaunchAgents`, which you can delete as well.

Requires macOS 11 (Big Sur) or later.

## Configuration

The version, update repository and the two Xtream Codes server addresses live in
`macos/Directory.Build.props`:

```xml
<Version>1.0.0</Version>
<GitHubRepo>ComputerGarage1837/StreamArcTV</GitHubRepo>
<LiveUrl>https://mediahere.ca/</LiveUrl>
<VodUrl>https://onlypuds.fans:2083/</VodUrl>
```

Users only ever enter a username and password; the server is chosen by the card they press.

Build locally (any OS with the .NET 8 SDK for a compile check):

```
dotnet build macos/StreamArcTV/StreamArcTV.csproj -c Release -r osx-arm64
```

Run from source on a Mac that has [VLC](https://www.videolan.org/vlc/) installed in /Applications
(the app borrows VLC.app's LibVLC when it is not running from its own bundle):

```
dotnet run --project macos/StreamArcTV/StreamArcTV.csproj
```

Build the full app bundle, disk image and zip on a Mac (downloads the VLC disk image once):

```
macos/bundle/make-app.sh 1.0.0 arm64 out     # or x64
```

Settings, accounts, watch progress, the transfer list and logs live under
`~/Library/Application Support/StreamArcTV`; caches (catalogues, guides, posters, timeshift chunks)
under `~/Library/Caches/StreamArcTV`. Downloads and recordings go to `Movies/Stream Arc TV/Downloads`
and `…/Recordings` unless you pick another folder.

## How the port is put together

The macOS edition is a port of the Windows edition. `Data/`, `Player/TimeshiftServer.cs` and
`Transfer/Transfers.cs` are the same code; the differences are:

- `UI/` is Avalonia instead of WPF: the same pages and behaviour, with styles as classes
  (`Classes="primary"`) and the drawn TV guide on Avalonia's `DrawingContext`. Dialogs still block
  the caller like Android's, through a nested dispatcher frame.
- `Player/PlayerCore.cs` loads LibVLC from `Contents/Frameworks/libvlc` inside the bundle (copied from
  the official VLC.app by `bundle/make-app.sh`), falling back to an installed VLC.app for development.
- `Transfer/TransferService.cs` registers launchd agents instead of Windows scheduled tasks; the app
  started by launchd with `--tray` stays in the menu bar. A lock file keeps a second copy from starting.
- `Update/Installer.cs` unpacks the release zip with `ditto` and swaps the app bundle in place, then a
  small helper relaunches it once the old process has exited.
- `Util/Mac.cs` holds the macOS specifics: bundle paths, Notification Center banners, `open`, launchctl.

## Releasing

Releases are built and published by GitHub Actions (`.github/workflows/macos.yml`) on a macOS
runner. No secrets are needed (the build is ad-hoc signed, not notarized).

To ship a version:

1. Bump `<Version>` in `macos/Directory.Build.props` (the version code is
   `major*10000 + minor*100 + patch` and must exceed the `versionCode` in `release/update-macos.json`).
2. Add a `## vX.Y.Z — date` section to `macos/CHANGELOG.md`; it becomes the release notes shown
   in the app.
3. Commit and push to `main` (or merge a pull request).

When `main` carries a version that has no `macos-vX.Y.Z` release yet, the workflow publishes the
Apple silicon and Intel disk images and zips on a release named "Stream Arc TV for macOS X.Y.Z",
then commits the matching `release/update-macos.json` (with SHA-256 and size of each) to `main`.
Pushing a `macos-v*` tag by hand or running the workflow manually does the same. Pull requests and
pushes that don't bump the version only build the app as a check. The build runs the app's
`--selftest` inside the finished bundle, which fails the job if the bundled LibVLC cannot start.

## Update format

`release/update-macos.json` uses the same schema as the Android feed, with an `arm64` and an `x64`
section that each hold `dmg` and `zip` entries:

```json
{
  "schemaVersion": 1,
  "packageName": "com.computergarage.streamarctv.macos",
  "versionName": "1.0.0",
  "versionCode": 10000,
  "arm64": {
    "dmg": { "assetName": "Stream-Arc-TV-1.0.0-macOS-arm64.dmg", "sha256": "…", "sizeBytes": 123 },
    "zip": { "assetName": "Stream-Arc-TV-1.0.0-macOS-arm64.zip", "sha256": "…", "sizeBytes": 123 }
  },
  "x64": {
    "dmg": { "assetName": "Stream-Arc-TV-1.0.0-macOS-x64.dmg", "sha256": "…", "sizeBytes": 123 },
    "zip": { "assetName": "Stream-Arc-TV-1.0.0-macOS-x64.zip", "sha256": "…", "sizeBytes": 123 }
  }
}
```

The app compares `versionCode` with its own and downloads the `zip` for its own architecture from
the release tagged `macos-v<versionName>`; `sha256` and `sizeBytes` are verified first. The `dmg`
entries are for people installing by hand. A `versionCode` of 0 means no macOS release has been
published yet.

## Disclaimer

Stream Arc TV is a media player. It does not provide, host or distribute any content; users are
responsible for the services they connect to.
