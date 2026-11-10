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
- The first `GET` for a user (plugin 0.3.0+) first merges their old
  "Watchlist" playlist; see [Old Watchlist playlist](#old-watchlist-playlist).
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

### `GET /SashimiLists/Watchlist/Search?q=<text>`

Since 0.3.0. Search for titles to add, for any signed-in user (Jellyfin's own
`/Items/RemoteSearch` is admin-only). `q` is required, 1-200 characters after
trimming; otherwise `400`.

`200`:

```json
{
  "query": "dune",
  "onServer": [ <hit> ],
  "notOnServer": [ <hit> ]
}
```

A hit:

```json
{
  "type": "Movie", "title": "Dune", "year": 2021,
  "tmdbId": "438631", "tvdbId": null, "imdbId": "tt1160419",
  "posterUrl": "https://image.tmdb.org/...", "overview": "...",
  "itemId": "<32-hex or null>", "onWatchlist": true, "watchlistEntryId": "<32-hex or null>"
}
```

- `onServer`: library movies and series matching `q` that the caller can see,
  best match first, then remote hits the library has under another name.
  `itemId` is always set; use the Jellyfin image API for their artwork
  (`posterUrl` is filled only when a remote hit supplied one).
- `notOnServer`: movies and series from the server's remote metadata providers
  (TMDB by default), movies and series interleaved. `itemId` is `null`. Hits
  with no TMDB/TVDB/IMDb id are left out.
- A remote hit that agrees with a library item on any provider id appears once,
  under `onServer`.
- At most 20 hits per group.
- `onWatchlist` / `watchlistEntryId`: the caller's Watchlist entry for the
  title (same type and any agreeing id), if there is one.
- A hit's fields match the `POST /SashimiLists/Watchlist/Items` body, so it can
  be posted back as it is.
- If the metadata providers fail or time out (10 s), `notOnServer` is empty;
  it is not an error.

### `GET /SashimiLists/Watchlist/Details?type=movie|series&tmdbId=&tvdbId=&imdbId=`

Since 0.3.0. What the server's metadata providers know about one title, for the
details page of a title that isn't on the server.

- `type` is `movie` or `series` (any case). At least one id; `tmdbId` and
  `tvdbId` are numbers, `imdbId` looks like `tt1160419`. Otherwise `400`.
- `404` when no provider knows the title.
- `200`, with every field the providers don't know **left out** (not `null`):

```json
{
  "type": "Movie", "title": "Dune", "year": 2021, "overview": "...",
  "posterUrl": "https://image.tmdb.org/...", "backdropUrl": "https://image.tmdb.org/...",
  "runtimeMinutes": 155, "officialRating": "PG-13", "genres": ["Science Fiction"],
  "communityRating": 7.8, "source": "TMDB",
  "tmdbId": "438631", "imdbId": "tt1160419",
  "itemId": "<32-hex>", "onWatchlist": false, "watchlistEntryId": "<32-hex>"
}
```

- `type`, `title` and `onWatchlist` are always present.
- `runtimeMinutes` is for movies, `seasonCount` for series.
- `source` names the provider ("TMDB" for TheMovieDb), for a "Details from TMDB" line.
- `itemId` is present when the title is on the server and the caller can see it
  (open the normal item page then).
- Image URLs are absolute provider URLs (image.tmdb.org, or another image
  provider such as fanart.tv when the metadata provider has none) that clients
  load directly; the plugin doesn't proxy them.
- Runtime and season count come from Jellyfin's built-in TMDB client, which the
  plugin reaches by name; if a Jellyfin update renames it they are left out.

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

### Old Watchlist playlist

Before the Watchlist existed, people kept a playlist called "Watchlist". The
first `GET /SashimiLists/Watchlist` for a user (0.3.0+) moves it in, once:

- Every playlist the user **owns** whose name is "watchlist" (any case,
  surrounding spaces ignored). Shared and public playlists owned by others are
  never touched.
- Movies and series go in as they are; an episode or season brings in its
  series. Anything without a TMDB, TVDB or IMDb id (home videos, YouTube,
  music) is skipped; the log lists the skipped item ids.
- Playlist order is kept (the first item shows first), and titles already on
  the Watchlist, or repeated in the playlist, are added once.
- The playlist is then **deleted**. For users linked to Plex the new titles go
  to Plex with the next sync.
- A per-user flag records that it ran, so it never runs again, even if the user
  later makes a new playlist called Watchlist. If deleting the playlist fails,
  the merge still counts as done and the error is logged. If anything fails
  before the merge is saved, nothing changes and the next `GET` tries again.

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

## Server settings

Dashboard › Plugins › Sashimi Lists has one setting, **Requests address**: the
Seerr/Jellyseerr address the Sashimi apps should use, for example
`https://requests.example.com`. It is empty by default. When it is set, the
apps lock their Requests server to it ("Set by your server") and only ask
users to sign in.

### `GET /SashimiLists/Config`

Since 0.3.0. Any signed-in user. `200 { "requestsUrl": "https://requests.example.com" }`,
trimmed and without a trailing slash, or `{ "requestsUrl": null }` when the
setting is empty or isn't an absolute http(s) URL. A `404` means the plugin is
missing or older than 0.3.0: treat it as not set.

## Build

```sh
export PATH="$HOME/.dotnet:$PATH" DOTNET_ROOT="$HOME/.dotnet"   # .NET 10 SDK
dotnet test tests/Jellyfin.Plugin.SashimiLists.Core.Tests
dotnet publish src/Jellyfin.Plugin.SashimiLists -c Release -o publish
```

The rules live in `Jellyfin.Plugin.SashimiLists.Core` (net9.0, no Jellyfin
reference) so they test in milliseconds: playlist visibility, the Watchlist
store, sync, drop-off, the old-playlist merge, search grouping, details
clean-up, the HTTP contract's JSON shape and plex.tv parsing. The
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
