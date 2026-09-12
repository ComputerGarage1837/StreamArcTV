# Update manifest format

The in-app updater reads `release/update.json` from the `main` branch of this repository (via
`raw.githubusercontent.com`). It compares `versionCode` with the installed build and only offers a
newer one. The APK is downloaded from the GitHub release tagged `v<versionName>` using the asset
named in the feed, then checked against `sha256` and `sizeBytes` before installation.

The metadata uses schema version 1:

```json
{
  "schemaVersion": 1,
  "packageName": "com.computergarage.streamarctv",
  "versionName": "1.0.0",
  "versionCode": 10000,
  "apk": {
    "assetName": "Stream-Arc-TV-1.0.0.apk",
    "sha256": "64 lowercase hexadecimal characters",
    "sizeBytes": 12345678
  }
}
```

The release workflow writes this file automatically after a `v*` tag builds and publishes
successfully, so it normally does not need to be edited by hand. If it is edited manually, the
named APK asset must exist in the release and `sha256` / `sizeBytes` must match it exactly, or the
app will reject the download.

The application ID and signing certificate must remain unchanged for all later updates. The Android
version code must increase with every release.
