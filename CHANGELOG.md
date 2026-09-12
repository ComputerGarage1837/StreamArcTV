# Changelog

All notable changes to Stream Arc TV are listed here. The section for each
version is shown to users inside the app when an update is available.

## v1.0.7 — 2026-09-12

### Changed
- Updater now reads the signed `release/update.json` feed from this repository, compares the
  Android version code, and downloads the APK named in the feed from the matching GitHub release.
- Update downloads show a progress dialog and are verified against the feed's SHA-256 and size
  before Android is asked to install them.
- Release workflow publishes `Stream-Arc-TV-<version>.apk` and writes the update feed back to `main`.

## v1.0.0 — 2026-09-12

### Added
- Home screen with two big buttons: **Live TV** and **Video on Demand**.
- Separate Xtream Codes sign-in for each service (username + password only) with the server addresses built in.
- Account expiry date shown under each button once you're signed in.
- Stay signed in until you log out from the profile screen or Settings.
- Live TV: category list, channel list, search, HLS/MPEG-TS playback.
- Video on Demand: category list, poster grid, search, movie playback.
- Built-in player (ExoPlayer) with retry on failure.
- Settings: auto-update toggle, live stream format, account sign-out, version info.
- Update button that checks GitHub Releases, shows the changelog, downloads and installs the new APK, with a **Skip this version** option.
- Works on Android TV / Fire TV (remote/D-pad) and phones/tablets (touch).
