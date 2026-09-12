# Changelog

All notable changes to Stream Arc TV are listed here. The section for each
version is shown to users inside the app when an update is available.

## v1.0.8 — 2026-09-12

### Added
- New home screen for both TV/tablet and phone: night-sky backdrop, headline, circular Update and
  Settings buttons with labels, Live TV and Video on Demand cards, and a Home / Search /
  Downloads / Profile bar.
- First launch asks whether the device is a phone or a TV/tablet and picks the matching layout;
  it can be changed any time under Settings → Display.
- Video on Demand has Movies and Series tabs. Series open to a season/episode list; tapping an
  episode plays it.
- Live TV now opens as a TV guide: every channel shows what's on now with a progress bar and
  what's next, and the panel above the list shows the focused channel's programme details and
  upcoming shows.
- Favorites: hold OK (long-press) on any channel or movie for a menu with Play and
  Add / Remove favorites. A ★ Favorites category sits at the top of the list and is opened first
  when you have favorites.

### Changed
- Search box text now sits properly inside its field (Live TV and Video on Demand).

## v1.0.7 — 2026-09-12

### Changed
- New launcher icon, TV banner and in-app wordmark based on the Stream Arc TV logo, with the white
  backdrop removed so it sits cleanly on the dark theme; accent colours now match the logo.
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
