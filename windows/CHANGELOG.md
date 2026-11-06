# Changelog — Stream Arc TV for Windows

All notable changes to the Windows edition are listed here. The section for each version is shown
to users inside the app when an update is available.

## v1.1.30 — 2026-11-06

### Added
- Cast photos. Movie and show pages now show the cast as round photos with their names and roles, followed by a "More like this" row of similar movies or shows that are available on your VOD, below everything else.
- Weekly automatic backup. Once a week the app quietly saves a backup of everything (settings, users and their lists, watched positions, sign-ins) in its own folder and deletes the previous one, so only the newest is kept. Restore it with Import in Settings.

### Fixed
- Import now also restores VOD users and each user's own watched list and My List.

## v1.1.29 — 2026-11-06

### Added
- Users on Video on Demand. Each person gets their own watched list and progress, My List, sort choice and new-episode tracking. The first time VOD is opened you're asked to make a user straight away; everything that was already watched or in My List becomes that first user's. A round avatar at the top of the VOD home switches user (or adds, renames and removes users), and with more than one user "Who's watching?" is offered each time the app starts. Users and their lists are included in Export and Import.

## v1.1.28 — 2026-11-06

### Changed
- Subscription expiry reminders and new episode alerts are now two separate switches in Settings, so either can be turned off without the other.
- The player controls now sit in one row underneath the progress bar: play/pause, skip back and forward, channel up and down, audio and subtitles, mini player, full screen and back. Previously the skip and channel buttons floated over the video.
- Live TV and 24/7 channels opened from the guide now show what is on now and what is next at the top of the player, whenever the controls are up or the video is paused.

## v1.1.27 — 2026-11-06

### Added
- Always-on-top mini player. A new button in the player (or the M key) shrinks the window to a
  small player in the bottom-right corner that stays above every other window, so a channel can keep
  playing while you work. Press it again to put the window back where it was; leaving the player does
  too.

## v1.1.26 — 2026-11-06

### Added
- Reminders. A tray notification a week before a subscription ends, another the day before, and
  one when it has ended, for both Live TV and Video on Demand. The app checks the account itself
  every few hours while it is running (it stays in the tray when the window is closed).
- New episode alerts. When a show in My List gets a new episode, a tray notification says so.
- Auto-download new episodes (Settings → Reminders and alerts, off by default): new episodes of
  My List shows are downloaded by themselves overnight (1 am to 7 am), and queued when the app starts.

## v1.1.25 — 2026-11-06

### Added
- Movie pages. Choosing a movie now opens its own page with the poster, year, rating, running
  time, genre, plot, cast and director when the provider supplies them, and buttons for Play (or
  Resume from where you stopped), Start over, Trailer, add to favorites and mark watched. Holding
  Enter on a movie still plays it straight away.
- Show pages show the rating, year, genre, cast and a Trailer button too.
- Sort and filter for the movie and show lists: a new button sorts by newest added, highest rated,
  newest year or A to Z, and narrows the list to one genre. The sort choice is remembered.
- A "Because you watched …" row on the Video on Demand home, suggesting unwatched titles that share
  a genre with what you finished most recently, best rated first.

## v1.1.24 — 2026-11-06

### Added
- Audio and subtitle picker in the player. The subtitle button now lists every audio and subtitle
  track of the stream with the current ones ticked; choosing one switches straight away. The audio
  language you pick is remembered and preferred on every stream from then on.
- Automatic stream format fallback. When a live channel fails, the app now tries the other format
  (HLS or raw TS) once before showing an error, because some channels only work in one of them.
- Channel up / down in the player. Opened from the guide, the player can move to the next or
  previous channel of the list you were browsing: CH − / CH + buttons, Page Up / Page Down, and
  up / down when the controls are hidden.

## v1.1.23 — 2026-11-06

### Fixed
- The guide no longer floods the provider. Opening a guide used to ask for each channel's
  programme listing one by one for every channel in the list (nearly four thousand requests for
  the 24/7 channels), which can get an account blocked or refused for a while: that is what made
  the previews and channels fail with errors. Now only the first screens are fetched up front and
  the rest as you scroll, and after forty empty answers in a row the per-channel lookups pause for
  six hours.
- A channel that the provider refuses because the previous stream is still closing (HTTP 403,
  typically right after a preview) is retried a few times before an error is shown, and opening a
  channel full screen now closes the preview first.
- Error messages are plain and accurate: a web page where data was expected (a block or
  maintenance page) is reported as such instead of as a parsing problem, and a refusal (HTTP 403)
  no longer says the password is wrong.
- The spinner no longer reports "'G' name cannot be found" in the log.

### Added
- The exportable log now records every request to the service with its outcome and timing, and
  the guide's download, disk copy and per-channel activity, so the next problem can be read from it.

## v1.1.22 — 2026-11-05

### Changed
- 24/7 channels: the Video on Demand menu (Home, Search, Categories, 24/7 Channels, Downloads,
  Profile) now stays on the left instead of being replaced by a category list; the categories
  are a row above the guide, and only Favorites and the provider's 24/7 group are listed.
  The duplicate category label next to the title is gone.

### Fixed
- Channel names in the TV guide are no longer cut off after a few letters: long names take a
  second line, and the channel column is wider for 24/7 channels. This applies to Live TV too.

## v1.1.21 — 2026-11-05

### Fixed
- Show and movie names are no longer cut off. Names on the Video on Demand cards, the poster
  grid, the show page, the episode rows and the guide panel now wrap onto up to three lines
  instead of ending in "…", and the banner title and the headings on the show and browse pages
  have room for long names too.

## v1.1.20 — 2026-11-05

### Fixed
- The "24/7 Channels" entry in Video on Demand was cut off at both ends (it read "4/7") because
  its label was wider than the navigation rail. Navigation labels now wrap onto two lines.

## v1.1.19 — 2026-11-05

### Added
- 24/7 channels in Video on Demand. When the provider supplies live "24/7" channels on the
  Video on Demand account, a new "24/7 Channels" entry appears in the Video on Demand navigation
  (next to Categories). It opens the same TV guide as Live TV: category list, programme grid,
  preview, favourites, recording, full-guide or per-channel programme data, and the storage
  buffer with pause and skip. The entry only shows when the provider has such channels and
  disappears when it doesn't.

## v1.1.18 — 2026-09-29

### Fixed
- "The app crashed last time" could appear after a screen with a loading spinner had been shown:
  the spinner's animation sometimes could not find its rotation target and threw an error. The
  animation now reaches the rotation through the spinner itself. Errors the app recovers from on
  its own are also no longer reported as crashes on the next start; only real crashes are.

## v1.1.17 — 2026-09-25

### Changed
- Maintenance release to confirm the in-app update from 1.1.16. No feature changes.

## v1.1.16 — 2026-09-25

### Fixed
- In-app updates failed with "The process cannot access the file … because it is being used by
  another process" right after the download: the app verified the downloaded package while its
  own writer still held the file open. The file is now closed before it is verified. This was the
  cause of every failed update so far.

## v1.1.15 — 2026-09-25

### Changed
- Maintenance release to confirm the in-app update from 1.1.14. No feature changes.

## v1.1.14 — 2026-09-25

### Fixed
- An update could fail before it even started, with a brief "file is in use" message: the
  download tried to overwrite the package left by an earlier attempt while something still held
  it open. The download and the unpack folder now use a fresh name whenever the old one is
  locked, and old packages are cleaned up when they can be.

## v1.1.13 — 2026-09-25

### Changed
- Every step of an update (check, offer, download, verify, install) is written to the app log,
  and a failed download or verify now shows a message box instead of a brief toast.

## v1.1.12 — 2026-09-25

### Fixed
- In-app updates no longer depend on PowerShell or any script. A copy run from the zip unpacks
  the new version and starts the new version's own program from that folder as the helper: it
  waits for the old one to exit (ending it if it lingers), copies the files in with retries, and
  starts the app again, logging every step to the app log. An installed copy hands over to the
  setup program, which closes the app itself (forced if needed), replaces the files and restarts
  it; the setup keeps a log next to the app's own. The exact reason for any failure is shown on
  the next start.

## v1.1.11 — 2026-09-25

### Changed
- Maintenance release to confirm the reworked in-app update from 1.1.10. No feature changes.

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
