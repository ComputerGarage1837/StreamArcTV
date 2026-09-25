# Service notice (announcement) format

Both apps show an optional notice on the home screen, just above the **Live TV** and
**Video on Demand** buttons. The text lives in `release/announcement.json` on the `main` branch of
this repository, so it can be posted, changed and removed at any time **without shipping an app
update**. When there is nothing to say the section is hidden completely.

The apps read the file every time the home screen is shown (and again every few minutes while it
stays open), first through the GitHub Contents API (no CDN cache, so a change is visible within a
minute) and, if that fails, from `raw.githubusercontent.com` (up to five minutes behind). The last
notice seen is remembered on the device so it still shows while offline.

```json
{
  "schemaVersion": 1,
  "active": true,
  "level": "outage",
  "title": "Live TV outage",
  "message": "Our Live TV provider is having server problems. Channels may not load until it is fixed. No action is needed on your end.",
  "until": "2026-09-26T18:00:00-04:00",
  "link": "",
  "platforms": ["android", "windows"]
}
```

| Field | Required | Meaning |
| --- | --- | --- |
| `schemaVersion` | yes | Always `1`. Apps ignore files with a different number. |
| `active` | yes | `true` shows the notice, `false` hides it. The quickest way to take a notice down is to set this to `false`. |
| `level` | no | Colour of the banner: `info` (blue, default), `warning` (amber) or `outage` (red). |
| `title` | no | Short bold heading, e.g. "Live TV outage". Leave empty for none. |
| `message` | yes | The notice itself. Plain text; line breaks with `\n`. The banner is hidden when this is empty. |
| `until` | no | ISO-8601 date and time (with offset) after which the notice hides itself, even if `active` is still `true`. Leave empty for "until removed". |
| `link` | no | Optional web address. When set, selecting the banner opens it. |
| `platforms` | no | Which apps show it: any of `android`, `windows`. Leave out or empty for both. |

## Posting a notice

1. Open `release/announcement.json` on GitHub (main branch) and press the pencil (Edit) icon.
2. Set `active` to `true`, fill in `message` (and `title` / `level` / `until` if wanted).
3. Commit directly to `main`. The apps pick it up on their next home-screen visit.

## Removing it

Set `active` to `false` (or empty the `message`) and commit. Or give the notice an `until` time
when you post it and it takes itself down.

Editing this file does not build or release anything: the Android and Windows workflows skip it.
