# Stream Arc TV

Stream Arc TV is a branded Android TV client for authorized Xtream Codes services.

This repository is the public release feed for the Android TV application. It contains release APKs and the signed update.json metadata used by the in-app updater. Application source and service connection details are intentionally not published here.

## Release assets

Each stable GitHub release contains:

- Stream-Arc-TV-<version>.apk — the installable Android TV application.
- update.json — package identity, version, file size, SHA-256 checksum, and release information.

The updater validates the release tag, package identity, version, APK size, complete APK hash, and signing certificate before handing an update to Android.

## Installation

The first updater-enabled version must be installed manually. Later versions can be discovered from inside Stream Arc TV. Android TV devices may require the one-time Allow from this source permission for the app before an update can be installed.

## Branding

The working brand assets are in branding/. The launcher icon and in-app logo are kept separate so the icon can be adapted for Android TV launcher sizing without changing the wordmark.

## Update format

See UPDATE_FORMAT.md for the release metadata contract.
