# Update manifest format

Before publishing a stable GitHub release, update `release/update.json` on `main`. The release must use a tag matching `v<versionName>` and attach the APK asset named in that feed. The metadata uses schema version 1:

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

The named APK asset must exist in the release. Keep `sha256` and `sizeBytes` accurate for release management and auditing. The app compares `versionCode` with the installed version and only offers a newer build.

The application ID and signing certificate must remain unchanged for all later updates. The Android version code must increase with every release.
