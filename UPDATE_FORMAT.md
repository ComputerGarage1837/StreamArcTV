# Update manifest format

Each stable GitHub release must use a tag matching v<versionName> and attach exactly one update.json file with schema version 1:

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

The named APK asset must exist exactly once in the same release. Its GitHub asset size must match sizeBytes. The app rejects prerelease or draft releases and rejects unknown fields, mismatched tags, package names, asset names, sizes, hashes, versions, or signing certificates.

The application ID and signing certificate must remain unchanged for all later updates. The Android version code must increase with every release.
