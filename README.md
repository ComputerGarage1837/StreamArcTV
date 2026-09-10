# Stream Arc TV

Stream Arc TV is a branded Android TV client for authorized Xtream Codes services.

This repository is the public release feed for the Android TV application. It contains the signed `update.json` metadata used by the in-app updater and the APKs attached to GitHub Releases. Application source and service connection details are intentionally not published here.

## Release assets

Each stable GitHub release should contain:

- `Stream-Arc-TV-<version>.apk` — the installable Android TV application.
- The matching `release/update.json` update feed is committed on `main` before publishing the release.

The updater checks the feed automatically when Stream Arc TV opens, compares the Android version code, and only shows the update screen when a newer build is available. The download screen reports progress before handing the APK to Android for installation. Android will only accept a later APK when it is signed with the same release key.

## Installation

The first updater-enabled version must be installed manually. Later versions can be discovered from inside Stream Arc TV. Android TV devices may require the one-time **Allow from this source** permission for the app before an update can be installed.

## Branding

The working brand assets are in [`branding/`](branding/). The launcher icon and in-app logo are kept separate so the icon can be adapted for Android TV launcher sizing without changing the wordmark.

## Update format

See [`UPDATE_FORMAT.md`](UPDATE_FORMAT.md) for the release metadata contract.
