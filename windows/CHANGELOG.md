# Changelog — Stream Arc TV for Windows

All notable changes to the Windows edition are listed here. The section for each version is shown
to users inside the app when an update is available.

## v1.1.10 — 2026-09-25

### Fixed
- In-app updates of a copy run from the zip (not the installer) failed with "file in use" and
  left the app open: they tried to swap files while the app was running. They now work like the
  installed copy: the app closes, a helper waits until the process has ended, copies the new
  files in with retries, and starts the app again. The app is also ended for certain when an
  update begins, so nothing in the background can hold it open.

## v1.1.9 — 2026-09-25

### Changed
- Maintenance release to confirm the reworked in-app update path from 1.1.8. No feature changes.

## v1.1.8 — 2026-09-25

### Added
- "Show password" switch on the Live TV and Video on Demand sign-in pages.

### Fixed
- In-app updates on an installed copy could stop with a "file in use" error: the setup program
  was started while the app was still shutting down. A small helper now waits until the app has
  fully exited before running the setup, and if a setup still fails the next start says why.

## v1.1.7 — 2026-09-25

### Added
- Service notice on the home screen, just above the Live TV and Video on Demand cards, read from
  `release/announcement.json` in the repository so it can be posted and taken down without an
  app update (same file and format as the Android app, see ANNOUNCEMENT_FORMAT.md). It is
  checked whenever the home screen is shown and every five minutes while it stays open, remembers
  the last notice for offline use, and is hidden completely when there is nothing to show.
  Levels info / warning / outage colour it blue / amber / red; an optional title, link and
  automatic take-down time are supported.

## v1.1.6 — 2026-09-15

### Fixed
- Opening Video on Demand could land on the category grid instead of the streaming-style home:
  the second click of a double-click on the home card hit whatever the new screen had under the
  pointer (for example its Categories item). Clicks in the first moment after a screen change
  are now ignored, and the home cards ignore a second press while the next screen is opening.

## v1.1.5 — 2026-09-15

### Changed
- The source repository is no longer shown anywhere in the app: the About row linking to it is
  gone (the version stays), the update-failure message no longer points there, and the installer
  no longer puts its links in Apps & features.

## v1.1.4 — 2026-09-14

### Added
- Settings → Backup: "Export settings and watch history" writes accounts, favorites, hidden and
  default categories, watch progress and watched marks, the download list and every setting to
  one JSON file; "Import settings and watch history" reads such a file back, replaces what is on
  the PC and restarts the app. For a fresh install or a second PC.

## v1.1.3 — 2026-09-13

### Fixed
- The box around the download / update progress bar changed size with every progress update,
  pulsing as the "x MB of y MB" text changed. Progress dialogs now keep a fixed size.

## v1.1.2 — 2026-09-13

### Changed
- After you queue a download, the app asks whether to go to the Downloads page or stay where you
  are (a short notice instead when you are already on Downloads).
- Downloaded files get clear names: movies are named by their title, and episodes are named
  "Show - S01E02 - Episode title" with the season and episode always two digits (never "S1E2").
  Downloads completed by earlier versions are renamed to this form the next time the app starts.

## v1.1.1 — 2026-09-13

### Fixed
- Stalls when pressing buttons. Every menu and message box was a transparent window with a
  drop shadow, which Windows draws in software and which made each one open with a hitch; they
  are now plain opaque windows. Opening a channel, movie or the guide preview created a new
  LibVLC engine on the window's thread every time (a good fraction of a second); there is now one
  shared engine, prepared in the background when the app starts. Settings, favorites and watch
  progress were written to disk on the spot with every change, including every few seconds while
  watching; writes are now gathered and done a moment later on a worker thread. Coming back to
  the poster grid from the player rebuilt every row even when nothing had changed.

## v1.1.0 — 2026-09-13

### Added
- A proper Windows installer. Each release now ships `Stream-Arc-TV-Setup-<version>.exe`, which
  installs the app for the current user (no administrator prompt), adds Start menu and desktop
  shortcuts and an Apps & features entry with an uninstaller, and offers to start the app when it
  finishes. Uninstalling removes the app and its scheduled recordings but keeps your settings,
  downloads and recordings.
- In-app updates on an installed copy download the new setup program and run it silently: the app
  closes, the files are replaced, and the app starts again. Portable copies unpacked from the zip
  keep updating in place as before; the zip is still published for them.

## v1.0.5 — 2026-09-13

### Fixed
- In-app updates could still fail. The updater no longer uses a script at all: it unpacks the
  package, swaps the new files into the app's folder itself while running (old files are moved
  aside and cleaned up on the next start), and restarts. If the folder needs administrator rights
  it asks once and does the swap elevated. When something does go wrong, the message now says
  exactly what and where.

## v1.0.4 — 2026-09-13

### Fixed
- Downloads (and storage-buffer live playback) failed with "Provider error (HTTP 302 Found)".
  The provider answers a movie or episode address with a redirect to its file server, often from
  an https:// address to a plain http:// one, which Windows networking refuses to follow on its
  own. The app now follows those redirects itself, carrying its headers and resume position along.

## v1.0.3 — 2026-09-13

### Fixed
- In-app updates failed with a message about not being able to access the folder. The updater no
  longer unpacks over the running app with PowerShell: it unpacks into its own data folder first,
  then copies the files in with retries once the app has closed, asking for administrator rights
  only when the app's folder needs them, and explains what to do if the app was started from
  inside the zip without extracting it.

## v1.0.2 — 2026-09-13

### Fixed
- The Video on Demand home could not be scrolled with the mouse wheel: each row's own horizontal
  scroller swallowed the wheel. The wheel now scrolls the page wherever the pointer is.
- Lag and freezes while loading: the catalogue (thousands of titles) was parsed, sorted and
  filtered on the window's own thread, and the Video on Demand home built all of its rows in one
  go. Parsing and filtering now run in the background, parsed fields are remembered instead of
  re-read on every use, guide lookups use an index, and the home adds its rows one at a time so
  the window stays responsive while it fills in.

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
