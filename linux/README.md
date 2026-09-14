# Stream Arc TV for Linux

The Linux edition of Stream Arc TV: the same screens and features as the Android TV / phone app,
the Windows app and the macOS app, built with Avalonia on .NET 8 and playing through the VLC
library installed on your system. Packages are built for x86-64 and 64-bit ARM.

<p align="center"><img src="../desktop/Shared/Assets/tv_banner.png" width="320" alt="Stream Arc TV"></p>

## Features

Everything the other editions do:

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
  Next episodes, My List, New movies, New series and genre rows. Categories and Search open the
  poster grid browser with every provider category and genre group.
- **Series** pages with seasons, episodes, watched marks and the Download options.
- **Player**: HLS or MPEG-TS live streams, the LIVE / Paused / behind-live badge, skip back and
  forward 10 s, playback buffer levels including the 20 min / 30 min / 1 hour storage buffers
  (timeshift kept on disk), resume-where-you-left-off, auto-play the next episode with the
  10-second countdown and "Still watching?", subtitle track choice (CC), automatic reconnects,
  and the buffering diagnostics line.
- **Downloads and recordings**: one download at a time with automatic retries and resume, downloads
  pause while you watch, recordings by start and end clock time that start on their own even when
  the app is closed (a systemd user timer starts it in the background), Delete all, the folder
  setting, and a tray icon that keeps transfers running when the window is closed.
- **Settings**: exactly the Android list (updates, playback, live TV, display, storage, accounts,
  about, category diagnostics, export logs).
- **Self-updating**: the Update button (and an optional check on launch) reads this repository's
  Linux release feed, shows the changelog, downloads the package for this machine's architecture,
  verifies its SHA-256 and installs it (a portable folder is swapped in place and the app restarts;
  a .deb install is handed to your package installer). You can skip any version.
- **Crash safety net**: if the app ever crashes, the next start offers to save or copy the log.

Keyboard: arrow keys move the highlight (like a remote), Enter is OK, **holding Enter** (or a
right-click) is "hold OK" for the item menus, Escape / Backspace go back, F11 toggles full screen,
Space pauses in the player and Left / Right skip 10 s while the controls are hidden.

## Install

Stream Arc TV needs VLC's library from your distribution (the app does not bundle it):

```
sudo apt install vlc          # Debian, Ubuntu, Mint
sudo dnf install vlc          # Fedora
sudo pacman -S vlc            # Arch
```

Then pick one of the packages from the [Releases](../../../releases) page (the releases tagged
`linux-v…`):

- **Debian / Ubuntu / Mint**: `streamarctv_<version>_amd64.deb` (or `_arm64.deb`), installed with
  `sudo apt install ./streamarctv_<version>_amd64.deb`. It puts the app in `/opt/streamarctv`, adds a
  menu entry and the `streamarctv` command, and pulls in the VLC packages it needs. Future updates
  download the new .deb and open it in your package installer.
- **Any distribution**: `Stream-Arc-TV-<version>-linux-x64.tar.gz` (or `-arm64`) is a portable folder:
  unpack it somewhere you can write to and run `./StreamArcTV`. Future updates swap the files in
  place and restart the app. The folder holds a `streamarctv.desktop` you can adapt for a menu entry.

The tray icon needs a status-notifier host (KDE, and GNOME with the AppIndicator extension); without
one the app still runs, and closing the window while downloads run keeps it going in the background.

## Configuration

The version, update repository and the two Xtream Codes server addresses live in
`linux/Directory.Build.props`:

```xml
<Version>1.0.0</Version>
<GitHubRepo>ComputerGarage1837/StreamArcTV</GitHubRepo>
<LiveUrl>https://mediahere.ca/</LiveUrl>
<VodUrl>https://onlypuds.fans:2083/</VodUrl>
```

Users only ever enter a username and password; the server is chosen by the card they press.

Build and run from source (needs the .NET 8 SDK and VLC installed):

```
dotnet run --project linux/StreamArcTV/StreamArcTV.csproj
```

Build the packages (needs `dpkg-deb`, present on Debian-based systems):

```
linux/package/make-packages.sh 1.0.0 x64 out     # or arm64
```

Settings, accounts, watch progress, the transfer list and logs live under
`~/.local/share/StreamArcTV` (or `$XDG_DATA_HOME/StreamArcTV`); caches under `~/.cache/StreamArcTV`.
Downloads and recordings go to `Videos/Stream Arc TV/Downloads` and `…/Recordings` unless you pick
another folder. Scheduled recordings are systemd user timers named `streamarctv-recording-<id>`.

## How the port is put together

The Linux and macOS editions share one code base in [`desktop/Shared`](../desktop/Shared) (screens,
data layer, player, transfer engine, updater); each edition adds a `Util/Platform.cs` with its
specifics. On Linux that is: XDG folders, the system `libvlc.so.5` (found without the -dev package),
`notify-send`, `xdg-open`, `systemd-run --user` for recordings that start while the app is closed,
and the two ways of installing an update.

## Releasing

Releases are built and published by GitHub Actions (`.github/workflows/linux.yml`) on an Ubuntu
runner. No secrets are needed.

To ship a version:

1. Bump `<Version>` in `linux/Directory.Build.props` (the version code is
   `major*10000 + minor*100 + patch` and must exceed the `versionCode` in `release/update-linux.json`).
2. Add a `## vX.Y.Z — date` section to `linux/CHANGELOG.md`; it becomes the release notes shown
   in the app.
3. Commit and push to `main` (or merge a pull request).

When `main` carries a version that has no `linux-vX.Y.Z` release yet, the workflow publishes the
x64 and arm64 .deb packages and portable archives on a release named "Stream Arc TV for Linux X.Y.Z",
then commits the matching `release/update-linux.json` (with SHA-256 and size of each) to `main`.
Pushing a `linux-v*` tag by hand or running the workflow manually does the same. Pull requests and
pushes that don't bump the version only build the packages as a check. The build runs the app's
`--selftest` against the runner's VLC packages, which fails the job if the system LibVLC cannot be
found and started.

## Update format

`release/update-linux.json` uses the same schema as the Android feed, with an `x64` and an `arm64`
section that each hold `deb` and `tar` entries:

```json
{
  "schemaVersion": 1,
  "packageName": "com.computergarage.streamarctv.linux",
  "versionName": "1.0.0",
  "versionCode": 10000,
  "x64": {
    "deb": { "assetName": "streamarctv_1.0.0_amd64.deb", "sha256": "…", "sizeBytes": 123 },
    "tar": { "assetName": "Stream-Arc-TV-1.0.0-linux-x64.tar.gz", "sha256": "…", "sizeBytes": 123 }
  },
  "arm64": {
    "deb": { "assetName": "streamarctv_1.0.0_arm64.deb", "sha256": "…", "sizeBytes": 123 },
    "tar": { "assetName": "Stream-Arc-TV-1.0.0-linux-arm64.tar.gz", "sha256": "…", "sizeBytes": 123 }
  }
}
```

The app compares `versionCode` with its own and downloads, for its own architecture, the `tar` when
it runs from a folder it can write to (portable install) or the `deb` otherwise, from the release
tagged `linux-v<versionName>`; `sha256` and `sizeBytes` are verified first. A `versionCode` of 0
means no Linux release has been published yet.

## Disclaimer

Stream Arc TV is a media player. It does not provide, host or distribute any content; users are
responsible for the services they connect to.
