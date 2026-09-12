# Stream Arc TV

Stream Arc TV is a branded Android TV / Fire TV client (also usable on phones and tablets) for authorized Xtream Codes services.

This repository holds the application source, the GitHub Actions release pipeline, and the public release feed (`release/update.json`) used by the in-app updater.

<p align="center"><img src="app/src/main/res/drawable-xhdpi/tv_banner.png" width="320" alt="Stream Arc TV"></p>

## Features

- Two-button home screen: **Live TV** and **Video on Demand**, each with its own server and sign-in.
- Sign in once with username and password; the app remembers you until you log out.
- Expiry date for each account shown right on the home screen.
- Categories, search, poster grid for movies, ExoPlayer playback (HLS or MPEG-TS for live).
- **Self-updating**: the Update button (and an optional check on launch) reads this repository's
  release feed, shows the changelog, downloads the APK with a progress dialog, verifies its
  SHA-256, and hands it to Android to install. You can skip any version.
- Remote/D-pad friendly UI with clear focus states; touch-friendly on phones.

## Install

1. Download the latest `Stream-Arc-TV-<version>.apk` from the [Releases](../../releases) page.
2. Sideload it on your device (on Android TV / Fire TV use an app like Downloader, or `adb install`).
   Allow installs from unknown sources when asked.
3. Future updates install from inside the app. Android only accepts a later APK when it is signed
   with the same release key, so every release must be signed with the original Stream Arc TV key.

## Configuration

The version, update repository and the two Xtream Codes server addresses live in `gradle.properties`:

```
VERSION_NAME=1.0.18
GITHUB_REPO=ComputerGarage1837/StreamArcTV
LIVE_URL=https://mediahere.ca/
VOD_URL=https://onlypuds.fans:2083/
```

Users only ever enter a username and password; the server is chosen by the button they press.

Build locally with:

```
./gradlew assembleDebug
```

## Releasing

Releases are built, signed and published by GitHub Actions (`.github/workflows/build.yml`).

Repository secrets required (Settings → Secrets and variables → Actions):

| Secret | Value |
| --- | --- |
| `KEYSTORE_BASE64` | `base64 -w0 streamarctv-release.jks` of the release keystore |
| `KEYSTORE_PASSWORD` | the keystore password |

Optional: `KEY_ALIAS` (defaults to `streamarctv`) and `KEY_PASSWORD` (defaults to the keystore password).

To ship a version:

1. Bump `VERSION_NAME` in `gradle.properties` (the version code is `major*10000 + minor*100 + patch`
   and must exceed the `versionCode` in `release/update.json`).
2. Add a `## vX.Y.Z — date` section to `CHANGELOG.md`; it becomes the release notes shown in the app.
3. Commit and push to `main` (or merge a pull request).

When `main` carries a `VERSION_NAME` that has no GitHub release yet, the workflow builds the signed
APK, creates the `vX.Y.Z` tag and release with `Stream-Arc-TV-X.Y.Z.apk` attached, and then commits
the matching `release/update.json` (with SHA-256 and size) to `main`. Pushing a `v*` tag by hand or
running the workflow manually does the same. Pull requests and pushes that don't bump the version
only build the APK as a check.

## Update format

See [`UPDATE_FORMAT.md`](UPDATE_FORMAT.md) for the release metadata contract.

## Branding

The working brand assets are in [`branding/`](branding/). The launcher icon and in-app logo are kept separate so the icon can be adapted for Android TV launcher sizing without changing the wordmark.

## Disclaimer

Stream Arc TV is a media player. It does not provide, host or distribute any content; users are responsible for the services they connect to.

## License

MIT
