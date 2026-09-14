# Changelog — Stream Arc TV for Linux

All notable changes to the Linux edition are listed here. The section for each version is shown
to users inside the app when an update is available.

## v1.0.0 — 2026-09-14

### Added
- First Linux release: the same app as the macOS edition (shared Avalonia code base) playing through
  the distribution's VLC library, for x86-64 and 64-bit ARM, as a .deb package and a portable archive.
  - Home screen (TV/tablet and Phone layouts), Live TV and Video on Demand sign-ins with the account
    expiry on the cards, Update and Settings buttons, and the Home / Search / Downloads / Recordings /
    Profile bar.
  - TV guide with the programme grid, details panel, live preview, favorites, hidden categories and
    default category, whole-guide or per-channel guide source; multi-view with two or four channels.
  - Video on Demand home with the featured banner, Continue watching, Next episodes, My List, new
    titles and genre rows; the poster grid browser with provider categories and genre groups; series
    pages with seasons, episodes and watched marks.
  - Player with the LIVE / behind-live badge, skip 10 s, playback buffer levels including the disk
    timeshift buffers, resume, auto-play next episode with countdown, subtitle choice, reconnects and
    the buffering diagnostics line.
  - Downloads (one at a time, retries, resume, paused while you watch) and scheduled recordings that
    start even when the app is closed (systemd user timers), with a tray icon that keeps transfers running.
  - Settings, category diagnostics, exportable logs, the crash safety net, and in-app updates from
    this repository's Linux release feed.
