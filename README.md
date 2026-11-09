# Jellyfin Catch-up

Start-over and replay for Jellyfin Live TV.

Clients ask the server which past or currently airing programmes can be played **from the beginning**.
The plugin answers with the best source:

1. **A recording** of that programme, if one exists (also while it is still recording), otherwise
2. **The IPTV provider's catch-up archive** via [Dispatcharr](https://github.com/Dispatcharr/Dispatcharr), proxied through Jellyfin with seeking (HTTP Range).

Provider credentials stay on the server. Clients only talk to Jellyfin, so it works wherever Jellyfin is reachable.

## Requirements

- Jellyfin 12.x
- Optional: Dispatcharr with XC accounts that offer catch-up (`tv_archive`), and an API key of a **standard** Dispatcharr user

## Settings (Dashboard → Plugins → Catch-up)

| Setting | Meaning |
|---|---|
| Dispatcharr URL | As reachable from the Jellyfin server, e.g. `http://dispatcharr:9191` |
| Dispatcharr API key | Key of a non-admin user. Stored on the server only |
| Channel profile | Dispatcharr profile used by Jellyfin's tuner (XMLTV for past programmes) |
| Look back (hours) | How far back programmes are offered (provider limits still apply) |
| Use provider catch-up | Off = recordings only |

Channels are matched between Jellyfin and Dispatcharr by **channel number**.

## API (all JSON endpoints require a signed-in user)

| Endpoint | Purpose |
|---|---|
| `GET /Catchup/Info` | Feature detection; channels with provider catch-up and their days |
| `GET /Catchup/Programs?channelIds=&startDate=&endDate=` | Past/airing programmes that can be started over (with `RecordingId` when a recording exists) |
| `GET /Catchup/Resolve?channelId=&startDate=&endDate=` | `{Kind:"recording", ItemId}` or `{Kind:"catchup", StreamPath, DurationSeconds}` |
| `GET /Catchup/Stream/{token}` | MPEG-TS stream with Range support (token from Resolve, valid 12 h) |

API-key requests may pass `userId=` like Jellyfin's own endpoints.

## Build

```sh
dotnet publish src/Jellyfin.Plugin.Catchup -c Release -o out
```

Copy `Jellyfin.Plugin.Catchup.dll` and `meta.json` into `<config>/data/plugins/Catchup_<version>/` and restart Jellyfin.

## License

GPL-3.0
