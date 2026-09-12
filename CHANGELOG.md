# Changelog

All notable changes to Stream Arc TV are listed here. The section for each
version is shown to users inside the app when an update is available.

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
