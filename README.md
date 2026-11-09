# Sashimi Lists

A Jellyfin plugin for Sashimi clients. It tells signed-in users who owns a
playlist, and keeps each user's **Watchlist**: a private list that can hold
titles the server doesn't have, with optional two-way sync to the user's own
Plex watchlist.

Sashimi's shared lists are Jellyfin playlists. Jellyfin 12.4 has no non-admin
API that exposes a playlist's owner, so clients used to share each list with its
own creator to record it. Deleting **any** Jellyfin user wipes that self-share
from every public playlist (`UserController.DeleteUser` →
`PlaylistManager.RemovePlaylistsAsync`), leaving creators unknown. This plugin
reads `Playlist.OwnerUserId` directly instead.

- Plugin GUID: `d95b4484-62cb-4873-92c5-66a65b86b420`
- Requires Jellyfin 12.4 (`Jellyfin.Controller` 12.4.0, net10.0)

## Playlist owner API

Both endpoints need a signed-in user (any user, standard Jellyfin auth).

### `GET /SashimiLists/Playlists/{playlistId}/Owner`

- `200 {"OwnerUserId":"<32-hex guid>","OwnerName":"<user name>"}` when the caller
  can see the playlist.
- `404` when it doesn't exist, isn't a playlist, or the caller can't see it.

### `GET /SashimiLists/Owners?ids=<id>,<id>,...`

- At most 200 ids (N or dashed format); more is a `400`.
- `200 {"<playlistId N-format>":{"OwnerUserId":"...","OwnerName":"..."}, ...}`
  holding only the playlists the caller can see. Others, and ids that aren't
  GUIDs, are left out without comment.

### Who can see a playlist

Admins see every playlist. Anyone else sees one they own, one shared with them,
or a public (OpenAccess) one, and only when Jellyfin's own
`Playlist.IsVisible(user)` agrees (that adds the parental-rating check). If the
owner has been deleted, `OwnerName` is `""`.

## Watchlist API

Every user has exactly one Watchlist. All routes need a signed-in user
(standard Jellyfin auth) and act on **the caller's own** Watchlist only: there
is no user id in any route and no admin override. Responses are **camelCase**
JSON, unlike the rest of Jellyfin's API. Request bodies are read
case-insensitively. A `404` from `GET /SashimiLists/Watchlist` means the server
has no Watchlist (plugin missing or older than 0.2.0).

Entries are movies or series stored by external id (TMDB, TVDB, IMDb), not as
library items. Two entries are the same title when they have the same type and
any id in common.

### `GET /SashimiLists/Watchlist`

`200`:

```json
{
  "plex": { "username": "alice", "needsReconnect": false },
  "cardDismissed": false,
  "plexOptedOut": false,
  "entries": [
    {
      "id": "<32-hex>", "type": "Movie", "title": "Dune", "year": 2021,
      "posterUrl": "https://image.tmdb.org/...", "tmdbId": "438631",
      "tvdbId": null, "imdbId": "tt1160419",
      "addedAt": "2026-11-09T12:00:00Z", "itemId": "<32-hex or null>"
    }
  ]
}
```

- `plex` is `null` when the user hasn't linked Plex.
- `plexOptedOut` is `true` once the user has disconnected Plex. Clients must not
  link Plex automatically (for example on a Requests sign-in with Plex) while it
  is `true`; an explicit Connect Plex still works and clears it. Missing (servers
  before 0.2.2) means `false`.
- `entries` are newest `addedAt` first. `type` is `"Movie"` or `"Series"`.
- `itemId` is the library item (N format) **the caller** can see, or `null`
  when it isn't on the server or their library access or parental limits hide it.
- `posterUrl` is an absolute http(s) URL or `null`.
- `addedAt` is UTC with whole seconds, e.g. `2026-11-09T12:00:00Z`.

### `POST /SashimiLists/Watchlist/Items`

Body `{ "type", "title", "year"?, "posterUrl"?, "tmdbId"?, "tvdbId"?, "imdbId"? }`.

- `200 { "entry": <entry>, "added": true }` when added.
- `200 { "entry": <existing entry>, "added": false }` when an entry for the same
  title is already there.
- `400` for a type other than `Movie`/`Series`, no title, or no external id.
- A `posterUrl` that isn't absolute http(s) is dropped. When none is given and
  the title is on the server, the plugin fills it from Jellyfin's remote image
  providers so the tile keeps artwork if the file is later deleted.

### `DELETE /SashimiLists/Watchlist/Items/{id}`

`204`, also when the entry is already gone.

### `POST /SashimiLists/Watchlist/Plex`

Body `{ "token": "<the user's plex.tv token>", "replace": false }`.

- `200 { "username", "needsReconnect" }` when linked. Linking the same Plex
  account again (same `uuid`) refreshes the token and clears `needsReconnect`.
  A successful link starts a sync straight away and clears `plexOptedOut`.
  A failed link (`409`, `422`, `503`) leaves it unchanged.
- `422` when plex.tv rejects the token (not `401`: clients treat any `401` as
  an expired Jellyfin session).
- `503` (no body) when plex.tv is unreachable, times out or returns an error,
  so the token couldn't be checked. Nothing is stored; try again later.
  (Since 0.2.1.)
- `409 { "username", "newUsername" }` when the Watchlist is linked to a
  different Plex account and `replace` is false. `username` is the linked
  account, `newUsername` the account the token belongs to.

### `DELETE /SashimiLists/Watchlist/Plex`

`204`. Syncing stops; the entries stay. Sets `plexOptedOut` to `true`.

### `POST /SashimiLists/Watchlist/DismissCard`

`204`. Remembers that the user said "Not now" to the Connect Plex card.

### How the Watchlist behaves

- **Plex sync** runs as the scheduled task *Sync Watchlists with Plex* (every
  5 minutes by default) and right after linking. The first sync after linking
  an account is a union of both lists and removes nothing on either side.
  After that, adds and removes on either side reach the other.
- If plex.tv rejects a stored token, syncing stops for that user and
  `needsReconnect` turns `true`; nothing is deleted. Linking again resumes.
- If plex.tv fails mid-sync, nothing from that run is saved, so a later run
  never mistakes missing items for removals.
- A Plex title that Plex hasn't matched to a TMDB/TVDB/IMDb id yet isn't
  imported; every run retries it, and the log notes how many are waiting
  (a count, never titles).
- **Finished titles drop off**: a movie when it is played, a show when the
  user has no unplayed regular episodes left. Only a play newer than the
  entry's `addedAt` counts, so re-adding something already watched keeps it.
  A generic user-data save counts only when it carries a play date.
  Linked users' Plex watchlists lose the title too.
- When a Jellyfin user is deleted, their Watchlist is removed on the next
  scheduled run.
- If a user's file can't be read, it is kept as `<userId>.json.corrupt-<time>`,
  an error is logged, and that user starts an empty, unlinked Watchlist.

### Plex endpoints used

Each call sends the user's token in the `X-Plex-Token` header (never in a URL),
with `X-Plex-Client-Identifier: sashimi-lists-<server id>` and
`X-Plex-Product: Sashimi`.

| Purpose | Call |
|---|---|
| Account | `GET https://plex.tv/api/v2/user` |
| List | `GET https://discover.provider.plex.tv/library/sections/watchlist/all` (paged, 100 per page) |
| External ids | `GET https://metadata.provider.plex.tv/library/metadata/{ratingKey}` |
| Find by id | `GET https://metadata.provider.plex.tv/library/metadata/matches?type=&guid=` |
| Add / remove | `PUT https://discover.provider.plex.tv/actions/addToWatchlist` / `removeFromWatchlist` |

### Where the data lives

One JSON file per user at `<Jellyfin data dir>/sashimi-lists/watchlists/<userId>.json`.
Each file holds that user's entries and, when linked, **their Plex token**. On
Linux the directory is `0700` and each file `0600`, owned by the Jellyfin
process. No endpoint returns a token, and the plugin never logs one. Back up or
restore these files like the rest of the data directory.

## Build

```sh
export PATH="$HOME/.dotnet:$PATH" DOTNET_ROOT="$HOME/.dotnet"   # .NET 10 SDK
dotnet test tests/Jellyfin.Plugin.SashimiLists.Core.Tests
dotnet publish src/Jellyfin.Plugin.SashimiLists -c Release -o publish
```

The rules live in `Jellyfin.Plugin.SashimiLists.Core` (net9.0, no Jellyfin
reference) so they test in milliseconds: playlist visibility, the Watchlist
store, sync, drop-off, the HTTP contract's JSON shape and plex.tv parsing. The
plugin shell only adapts Jellyfin and plex.tv to them.

## Install by hand

Copy `Jellyfin.Plugin.SashimiLists.dll` and `Jellyfin.Plugin.SashimiLists.Core.dll`
into `<config>/plugins/Sashimi Lists_<version>/` with a `meta.json`, then restart
Jellyfin. **Stop Jellyfin before replacing the DLLs of an installed version:**
overwriting an assembly the server has mapped corrupts it and fakes a crash at
the next shutdown. To upgrade, stop, put the new version in a new folder, remove
the old folder, start.

## License

MIT
