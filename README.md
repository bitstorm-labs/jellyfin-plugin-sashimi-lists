# Sashimi Lists

A read-only Jellyfin plugin that tells signed-in users who owns a playlist.

Sashimi's shared lists are Jellyfin playlists. Jellyfin 12.4 has no non-admin
API that exposes a playlist's owner, so clients used to share each list with its
own creator to record it. Deleting **any** Jellyfin user wipes that self-share
from every public playlist (`UserController.DeleteUser` →
`PlaylistManager.RemovePlaylistsAsync`), leaving creators unknown. This plugin
reads `Playlist.OwnerUserId` directly instead.

- Plugin GUID: `d95b4484-62cb-4873-92c5-66a65b86b420`
- Requires Jellyfin 12.4 (`Jellyfin.Controller` 12.4.0, net10.0)

## API

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

## Build

```sh
export PATH="$HOME/.dotnet:$PATH" DOTNET_ROOT="$HOME/.dotnet"   # .NET 10 SDK
dotnet test tests/Jellyfin.Plugin.SashimiLists.Core.Tests
dotnet publish src/Jellyfin.Plugin.SashimiLists -c Release -o publish
```

The rule lives in `Jellyfin.Plugin.SashimiLists.Core` (net9.0, no Jellyfin
reference) so it tests in milliseconds; the plugin shell only adapts Jellyfin's
playlists and users to it.

## Install by hand

Copy `Jellyfin.Plugin.SashimiLists.dll` and `Jellyfin.Plugin.SashimiLists.Core.dll`
into `<config>/plugins/Sashimi Lists_<version>/` with a `meta.json`, then restart
Jellyfin. **Stop Jellyfin before replacing the DLLs of an installed version:**
overwriting an assembly the server has mapped corrupts it and fakes a crash at
the next shutdown. To upgrade, stop, put the new version in a new folder, remove
the old folder, start.

## License

MIT
