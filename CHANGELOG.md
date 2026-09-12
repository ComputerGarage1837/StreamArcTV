# Changelog

All notable changes to Stream Arc TV are listed here. The section for each
version is shown to users inside the app when an update is available.

## v1.0.43 — 2026-09-12

### Fixed
- Movies and episodes could sit "buffering" with minutes of video already downloaded and the
  play button showing paused. The resume question is now asked before the stream is opened, so
  playback never waits on it, and a watchdog now steps in when plenty is buffered but nothing
  plays: it nudges the player, then reopens the stream, then explains that the device's video
  decoder is not producing a picture rather than spinning forever.
- The line under the spinner now also names the video format, resolution and decoder in use and
  whether a picture has been produced.

## v1.0.42 — 2026-09-12

### Fixed
- Movies and episodes could sit on "0 B received": a download paused for playback still held
  its connection open in the app's keep-alive pool, and the provider counted it as your one
  allowed stream, so the player's request was left waiting. Paused downloads now close their
  connection outright and the player drops any idle provider connections before it asks for a
  stream.
- The "received" figure under the spinner now counts live instead of only when a file finished.
- Connections to the provider are cut the moment playback stops or a download or recording
  finishes, and every stream request asks the server not to keep the connection alive, so the
  account's stream slot is free straight away for the next thing you open.

## v1.0.41 — 2026-09-12

### Changed
- While the spinner is up for more than a few seconds it now shows how much data has arrived,
  how long it has been waiting and any network error (timed out, refused, can't connect), so a
  stalled stream is understood at a glance instead of spinning silently.
- Buffer time thresholds again take priority over the memory ceiling when deciding when to
  start, matching the settings the app used before the buffer options were added.

## v1.0.40 — 2026-09-12

### Changed
- Playback starts, and resumes after a stall, as soon as one second of video is ready on every
  buffer level. The buffer keeps filling ahead while you watch.

### Fixed
- Pressing play while a download was running could spin for minutes: most providers allow one
  stream per account and the download was holding it. Downloads now pause the moment you open
  the player ("Paused while you watch") and carry on from where they stopped when you leave.
  If the provider still refuses a stream, the player says so within seconds instead of
  buffering, with the reason (for example HTTP 458, too many streams).

## v1.0.39 — 2026-09-12

### Fixed
- Bigger buffer sizes made you wait before anything played (Huge waited for 6 seconds of video
  to start and 12 after a stall). Playback now starts as soon as about 2 seconds are ready on
  every level; the buffer size only changes how far ahead the app keeps downloading.

## v1.0.38 — 2026-09-12

### Added
- New Video on Demand home in the style of a streaming service: a featured banner that rotates
  through what's new (Play / Open and My List), then rows for Continue watching (with time left),
  Next episodes for series you have started, My List, New movies, New series, and the most
  common genres for movies and series. Hold OK on any card for play, favorites, download and
  watched options. "See all" on a row opens that group in the grid browser.
- Home, Search, Categories, Downloads and Profile sit in a side rail on TVs and landscape, and
  as a row of tabs on an upright phone. Categories and Search open the existing grid browser
  with every provider category, genre group and the search box, so nothing is lost.

### Fixed
- Opening Movies or Series with the catalogue already on disk lost the genre groups from the
  category list until the next refresh.

## v1.0.37 — 2026-09-12

### Added
- Movies, episodes and downloaded files remember where you left off. Opening one again asks
  whether to resume from that point or start over. Posters and episode rows show a progress bar,
  and a tick once watched (near the end counts as watched). Hold OK on a movie or episode for
  "Mark as watched" / "Mark as unwatched".
- Subtitle control: Settings → Playback → Subtitles chooses whether subtitle tracks are shown by
  default (now off unless you turn them on), and the player's CC button lets you pick a track or
  turn them off while watching. Your choice in the player is remembered.

### Fixed
- The Downloads list fell far behind the real progress. Each row was asking Android for its
  folder's name on every refresh; that is now looked up once, the list reads progress directly
  from the download service twice a second, and shows percentage, size and current speed.

## v1.0.36 — 2026-09-12

### Changed
- The playback buffer now defaults to Huge (10 minutes ahead) on every device. It is still capped
  to half of the memory Android gives the app, so smaller boxes automatically get as much as
  they can hold. Anyone who already picked a size keeps their choice.
- Downloads and recordings no longer ask where to save each time. The folder picker appears
  only the first time (or if the saved folder is no longer reachable); after that everything
  goes to the saved folder until you change it under Settings → Downloads & recordings.

### Fixed
- Season and whole-series downloads failing with "Failed": episodes were downloaded two at a
  time and most providers only allow one connection per account. Downloads now run one after
  another, and any download that is refused or cut off is retried automatically (up to six
  attempts with growing delays), resuming from where it stopped when the provider allows it.
  While waiting to retry the item shows the reason and the countdown. A download that still
  fails after that is cleared from the Downloads list together with its partial file; the
  failure notification tells you why. The list shows the remaining queue in order.

### Added
- "Delete all" button at the top of the Downloads and Recordings lists, with a confirmation
  that says how many items go and whether any in-progress or scheduled ones will be cancelled.

## v1.0.35 — 2026-09-12

### Fixed
- Live video could freeze after 20–30 seconds while the sound carried on, then jump ahead. The
  MPEG-TS fast-start flags, the back buffer and the fixed HLS live offset added in 1.0.33 are
  removed; playback is back on ExoPlayer's proven defaults, with only the buffer sizes and the
  no-catch-up pause kept.

### Added
- Skip back and forward 10 seconds on live TV: on-screen « 10 s / 10 s » buttons on phones, and
  left/right on the remote (or the rewind / fast-forward keys) while the controls are hidden.
  HLS channels skip within the provider's window; with a stored buffer you can skip anywhere in
  what has been kept. A plain MPEG-TS channel without a stored buffer explains why it cannot.

### Changed
- The buffer setting is now "Playback buffer (live TV & VOD)": the chosen size applies to movies
  and series as well as live channels. The storage options give VOD the Huge memory buffer.

## v1.0.34 — 2026-09-12

### Added
- Live TV buffer options of 20 minutes, 30 minutes and 1 hour. These keep the channel on device
  storage (the app cache) rather than in memory, so you can pause for up to an hour and pick up
  where you left off. Choosing one shows a notice about storage use and your current free space;
  the app always leaves at least 1 GB free, drops the oldest video first, and deletes the cache
  when you leave the channel.
- While a storage buffer is in use the connection to the provider is kept alive and re-tried by
  the app itself, so short outages no longer interrupt what you are watching.

### Changed
- The LIVE badge now switches to "Paused" or "behind live" (amber) the instant you pause or
  rewind, shown whenever the player controls are up.

## v1.0.33 — 2026-09-12

### Added
- Settings → Playback → **Live TV buffer**: Small, Normal, Large, Very large or Huge (up to 10
  minutes ahead). Bigger buffers ride out patchy connections and let you pause for longer. Huge
  buffers are capped to what the device's memory can safely hold.
- Pause on live TV now keeps your place: the app keeps filling the buffer while paused and play
  resumes exactly where you stopped, without creeping or snapping back to live. The top-right
  badge shows LIVE or how far behind you are; select it to jump back to live.
- The buffering spinner now shows how many seconds are ready, and dropped live connections
  reconnect on their own (up to four tries) before showing the Retry button.

### Changed
- Live playback starts slightly behind the live edge so there is always video ready ahead, uses
  faster MPEG-TS start-up, hardware-decoder fallback, more download retries and network wake
  locks so the stream does not stall when the device dozes.

## v1.0.32 — 2026-09-12

### Changed
- TV guide on phones held upright: categories stay as a row across the top, the channel column
  is slimmer with smaller logos, and the top panel is shorter, giving the programme grid more of
  the screen.

## v1.0.31 — 2026-09-12

### Changed
- The guide download asks the provider for compressed data and reports the real amount sent
  over the network.
- When the provider's whole-guide file is oversized (over 60 MB), the app stops downloading it
  and fills the guide channel by channel instead, fetching only the channels you actually list.
  Settings → Live TV → TV guide source lets you force either method.

## v1.0.30 — 2026-09-12

### Changed
- The TV guide no longer gets lost: the last good guide is kept on the device and shown
  immediately (even after a restart or with no network), it is refreshed quietly in the
  background after three hours while the old one stays on screen, a download that comes back
  empty or half-built (as panels do while regenerating their EPG) is ignored and retried later,
  and a channel's programmes are never wiped by a failed lookup. The saved guide covers two days.

## v1.0.29 — 2026-09-12

### Fixed
- Guide download progress now always shows a total and a percentage with a filling bar. When the
  provider doesn't report the file size, the size from the previous download is used as the
  estimate (shown with a ~), with a sensible guess the very first time.

## v1.0.28 — 2026-09-12

### Changed
- Movies and Series open faster: the category list appears at once, genre groups are added as
  soon as the catalogue arrives, and the catalogue is kept on the device so later visits are
  instant (refreshed quietly in the background when it is more than 30 minutes old).
- The guide download shows real progress: megabytes received, a percentage when the provider
  reports the file size, and a clearly visible bar. Filling in channels afterwards runs several
  at a time, so that stage is much quicker.

## v1.0.27 — 2026-09-12

### Added
- First-time setup: right after choosing the layout, the app asks to be allowed to install
  updates and opens that exact system setting, so a new box is ready before its first update.
  The same setting can be opened later from Settings → Updates.
- Genre categories for Series (and Movies when the provider supplies genres): Comedy, Action,
  Drama and so on are built from each title's genre information and listed ahead of the
  provider's own groups, so a provider that only offers A–Z groups still gets a proper genre list.

## v1.0.26 — 2026-09-12

### Changed
- Movies and Series list every category the provider attaches to its items, even ones the
  provider's category list leaves out, and items that belong to several categories appear in
  each of them.
- Settings → Category diagnostics shows what the provider returns (categories listed, items,
  category ids used but not listed) to help track down missing categories.

## v1.0.25 — 2026-09-12

### Fixed
- With a remote, the focused Movies / Series / Downloads tab (and category chip) is now solid
  white with dark text, so it is obvious which one is highlighted; the active one stays cyan.

## v1.0.24 — 2026-09-12

### Added
- Live preview in the TV guide: pressing OK on a channel plays it in the small box at the top
  left while you keep browsing; pressing OK on the same channel again goes full screen, and
  backing out of full screen returns it to the box.

## v1.0.23 — 2026-09-12

### Changed
- The TV guide shows about two hours across the screen instead of four, so programme titles have
  room to be read. Scroll right for later times.

## v1.0.22 — 2026-09-12

### Changed
- The TV guide shows a progress bar in its top panel while the listing downloads and fills in,
  with a channel count, so it's clear it isn't stuck. Browsing keeps working meanwhile.
- The automatic default category is "General Streams" (falling back to any "General" category,
  then All).

## v1.0.21 — 2026-09-12

### Added
- Settings → Live TV: switch off categories you don't use (their channels disappear from the
  guide, All and search) and choose the category the guide opens on: any category, Favorites or
  All. By default it opens on the provider's "General" category when there is one.

### Changed
- The recording hour wheel runs through the whole day (11 AM rolls into 12 PM, 11 PM into
  12 AM of the next day) instead of a separate AM/PM switch.

## v1.0.20 — 2026-09-12

### Fixed
- The recording time wheels could not be turned by touch. Start and End are now plain hour,
  minute and AM/PM wheels that work by finger and with the remote.

## v1.0.19 — 2026-09-12

### Changed
- Recording is set by start and end clock times, to the minute (for example 7:00 PM to 8:00 PM).
  A start time already past means tomorrow; an end before the start rolls into the next day.
- The Recordings screen shows a notice at the top with what is recording now and what is
  scheduled, ahead of finished recordings.
- Portrait phones: the Movies / Series / Downloads tabs sit on their own full-width row instead
  of being squeezed into the top bar.

## v1.0.18 — 2026-09-12

### Added
- Live TV recording: hold OK on a channel in the guide and choose Record. Pick a start time (now
  or later, in 5-minute steps), a duration (5-minute steps), and the folder. Scheduled recordings
  start on their own, even if the app is closed. A Recordings section on the home bar lists them
  with progress, Play and Delete.
- Download whole series or seasons: hold OK on a series poster for "Download entire series", or
  use the Download all / per-season buttons inside a series.
- Downloads tab next to Movies and Series.
- Real folder choice: downloads and recordings save to a folder you pick from the device's
  storage (the picker remembers your choice; change it any time under Settings → Storage or from
  the Downloads / Recordings screens).

### Changed
- Downloads now run inside Stream Arc TV with a progress notification, so they can be cancelled
  and saved anywhere.

## v1.0.17 — 2026-09-12

### Added
- Downloads: hold OK on a movie or an episode and choose Download. You pick where to save it
  (inside the app, or the device's Downloads folder), and the Downloads section on the home screen
  lists every download with progress, Play and Delete.

### Changed
- Phones held upright: the category list becomes a row of chips above the content, so movie and
  series posters use the full width and are no longer narrow.

## v1.0.16 — 2026-09-12

### Changed
- Movies and Series load their whole catalogue once (kept for 30 minutes), so switching categories
  is instant.
- Movies and Series open on a new "Recently added" section showing the newest titles first; "All"
  and the provider's categories follow it.
- Posters are shown whole at their 2:3 shape instead of being cropped or stretched.
- Rotating the phone no longer resets anything: every screen keeps its state.
- The TV guide fills in for every channel in the list right after opening, matching channels to
  the downloaded listing by id or by name and fetching the rest in the background, so scrolling
  never waits.

## v1.0.15 — 2026-09-12

### Changed
- The TV guide now downloads the whole programme listing in one go when it opens and keeps it for
  30 minutes, so scrolling through channels is seamless instead of loading each row on the way.
  Channels missing from the listing still fall back to a per-channel lookup.

## v1.0.14 — 2026-09-12

### Fixed
- Phone portrait home: the card icon and chevron were drawn far too large and pushed the text out of
  the cards. They are now fixed-size and sit inside the card like the design.

## v1.0.13 — 2026-09-12

### Changed
- Home backdrops are the full-size scenery images: the portrait version on phones held upright, the
  wide version on TV and phones in landscape.
- Phone cards use the supplied card artwork: tall cards side by side in portrait, wide cards side by
  side in landscape, at the artwork's own proportions.

## v1.0.12 — 2026-09-12

### Changed
- Phone home: Live TV and Video on Demand are now two full-width cards stacked at the wide
  proportion of the card artwork, with icon, title, expiry and chevron inside, so they no longer
  cover the scenery.

## v1.0.11 — 2026-09-12

### Changed
- Live TV is now a real programme grid: channels down the side with logos, a time ruler with a
  "now" marker, programme blocks sized by duration, and a details panel showing the focused
  programme's picture, time and description. Move with the remote, press OK to watch, hold OK for
  favorites; touch and drag to scroll on phones.
- Home screen uses the original design artwork: the scenery backdrop, card images, round Update and
  Settings buttons and the bottom-bar icons. TV/tablet layout is smaller so the scenery and headline
  stay visible; phone cards are shorter and squarer, and the phone home scrolls in landscape.
- Content no longer sits under the phone's status bar or notch.
- Movies / Series tabs are a clear segmented control with readable labels.

## v1.0.10 — 2026-09-12

### Fixed
- "Check for updates" now reads the update feed through the GitHub API, which is not cached, so a
  new release is offered immediately instead of up to five minutes later. The 1.0.9 workaround
  turned out not to bypass the cache.

## v1.0.9 — 2026-09-12

### Fixed
- Attempted cache bypass for "Check for updates" (superseded by 1.0.10).

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
