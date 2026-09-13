# Changelog — Stream Arc TV for Windows

All notable changes to the Windows edition are listed here. The section for each version is shown
to users inside the app when an update is available.

## v1.0.1 — 2026-09-13

### Changed
- Signing in to Video on Demand now opens the streaming-service style home (featured banner,
  Continue watching, My List, new titles and genre rows) straight away instead of the grid browser.
- When the provider refuses a download, the Downloads list and the log now show the provider's
  exact reply (status, reason and message) instead of just "Provider error (HTTP …)". HTTP 401 and
  503 are treated as "too many streams open" like 403 / 429 / 458, and retried automatically.

## v1.0.0 — 2026-09-13

### Added
- First Windows release: the Android app's screens and features on a Windows PC, built with WPF and
  playing through LibVLC.
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
    start even when the app is closed, with a tray icon that keeps transfers running.
  - Settings, category diagnostics, exportable logs, the crash safety net, and in-app updates from
    this repository's Windows release feed.
