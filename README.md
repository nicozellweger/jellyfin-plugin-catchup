# Jellyfin Catch-up

Start-over and replay for Jellyfin Live TV.

Pick a programme that already aired, or one that is on right now, and watch it **from the beginning** in the
normal player of any Jellyfin client. The plugin answers with the best source:

1. **A recording** of that programme, if one exists (also while it is still recording), otherwise
2. **The provider's catch-up archive**, fetched on demand and served as a seekable stream.

Provider credentials stay on the server. Clients only talk to Jellyfin, so it works wherever Jellyfin is reachable.

## What you get

- A channel **Replay** (Channels → Replay → channel → Today / Yesterday / … → programme) in every Jellyfin client.
- An API that clients can use to offer "start over" in the guide and the live player and to rewind live TV
  beyond what the player has buffered.
- Seeking by time anywhere in the programme, also while it is still airing.
- Downloads only while someone watches, on disk with a size limit.

## What works with which Live TV source

| Feature | Needs |
|---|---|
| Start-over from a **recording** (finished or in progress) | Any Jellyfin Live TV source |
| Past programmes that were **recorded** in the guide API | Any Jellyfin Live TV source |
| **Provider catch-up** (replay without recording) | A catch-up backend, see below |

Catch-up backends are pluggable (`ICatchupBackend`: channels with an archive, what aired, a stream for a time
span). Included today:

- **[Dispatcharr](https://github.com/Dispatcharr/Dispatcharr)** with XC accounts that offer catch-up (`tv_archive`).

Without a configured backend the plugin still offers everything in the first two rows.

## Requirements

- Jellyfin 12.x
- For provider catch-up: Dispatcharr and an API key of a **standard** (non-admin) Dispatcharr user
- Disk space for the cache (default limit 30 GB; roughly 3.5 GB per hour of HD)

## Install

Add the repository in Dashboard → Plugins → Repositories:

```
https://raw.githubusercontent.com/nicozellweger/jellyfin-plugin-catchup/main/manifest.json
```

Install **Catch-up** from the catalogue and restart Jellyfin.

## Settings (Dashboard → Plugins → Catch-up)

| Setting | Meaning |
|---|---|
| Dispatcharr URL | As reachable from the Jellyfin server, e.g. `http://dispatcharr:9191` |
| Dispatcharr API key | Key of a non-admin user. Stored on the server only |
| Channel profile | Dispatcharr profile used by Jellyfin's tuner (XMLTV for past programmes) |
| Look back (hours) | How far back programmes are offered (provider limits still apply) |
| Use provider catch-up | Off = recordings only |
| Cache directory / limit | Where segments are kept and how much (oldest programmes go first; unused ones after 6 h) |

Channels are matched between Jellyfin and the backend by **channel number**.

## How it behaves

- **One provider connection at a time.** The programme opened last owns it. Another user who starts a replay
  while someone is watching one gets "busy" (HTTP 409) instead of interrupting them.
- A download stops 90 seconds after the last viewer left and continues from disk next time.
- A seek to a part that is not on disk restarts the download there (a few seconds) instead of waiting for
  everything before it.
- Jellyfin remuxes the stream (video and audio are copied), so clients use their normal player, subtitles and
  audio selection.

## API (JSON endpoints require a signed-in user)

| Endpoint | Purpose |
|---|---|
| `GET /Catchup/Info` | Feature detection; channels with provider catch-up and their days |
| `GET /Catchup/Programs?channelIds=&startDate=&endDate=` | Past/airing programmes that can be started over (with `RecordingId` when a recording exists) |
| `GET /Catchup/Resolve?channelId=&startDate=&endDate=[&offset=]` | `{Kind:"recording", ItemId, ItemStartUtc}` or `{Kind:"catchup", ItemId, StreamPath, DurationSeconds}`; `409 {Error:"busy"}` when the connection is in use |

`ItemId` plays in the client's normal player. `ItemStartUtc` is the wall-clock time of position zero of a
recording (recordings start before the programme), for clients that seek to "20 minutes ago".
`offset` (seconds into the programme) lets the server start downloading where playback will begin.

The HLS endpoints under `/Catchup/Hls/` are read by the server's own ffmpeg; from elsewhere they need a signed-in user.

API-key requests may pass `userId=` like Jellyfin's own endpoints.

## Limits

- Programme boundaries come from the guide; a 5-minute tail covers overruns.
- Seeking past "now" in a programme that is still airing has nothing to show (clients should cap the seek).
- Tested with Dispatcharr and XC providers that allow one connection.

## Build

```sh
dotnet publish src/Jellyfin.Plugin.Catchup -c Release -o out
```

Copy `Jellyfin.Plugin.Catchup.dll` and `meta.json` into `<config>/data/plugins/Catchup_<version>/` and restart Jellyfin.

## Disclaimer

This plugin contains no content and no provider access. Use it with sources you are entitled to use.

## License

GPL-3.0
