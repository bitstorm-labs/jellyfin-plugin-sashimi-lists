using System.Net.Mime;
using System.Security.Claims;
using System.Text.Json;
using Jellyfin.Plugin.SashimiLists.Core;
using Jellyfin.Plugin.SashimiLists.Core.Watchlist;
using MediaBrowser.Controller.Library;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.SashimiLists.Watchlist;

/// <summary>
/// The caller's own Watchlist. Every route acts on the signed-in user only; there
/// is no user id in any route and no admin override. Responses are camelCase
/// (<see cref="WatchlistContract"/>), unlike the rest of Jellyfin's API.
/// </summary>
[ApiController]
[Authorize]
[Route("SashimiLists/Watchlist")]
[Produces(MediaTypeNames.Application.Json)]
public class WatchlistController : ControllerBase
{
    // Jellyfin.Api.Constants.InternalClaimTypes.UserId; Jellyfin.Api isn't a plugin reference.
    private const string UserIdClaim = "Jellyfin-UserId";

    private readonly WatchlistService _service;
    private readonly PosterFinder _posters;
    private readonly PlexSync _sync;
    private readonly PlaylistMerger _merger;
    private readonly IWatchlistStore _store;
    private readonly JellyfinLibraryLookup _library;
    private readonly RemoteTitles _remote;
    private readonly IUserManager _userManager;
    private readonly ILogger<WatchlistController> _logger;

    public WatchlistController(
        WatchlistService service,
        PosterFinder posters,
        PlexSync sync,
        PlaylistMerger merger,
        IWatchlistStore store,
        JellyfinLibraryLookup library,
        RemoteTitles remote,
        IUserManager userManager,
        ILogger<WatchlistController> logger)
    {
        _service = service;
        _posters = posters;
        _sync = sync;
        _merger = merger;
        _store = store;
        _library = library;
        _remote = remote;
        _userManager = userManager;
        _logger = logger;
    }

    [HttpGet]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public IActionResult Get()
    {
        if (!TryGetCaller(out var user))
        {
            return Unauthorized();
        }

        MergeOldPlaylist(user);
        return Json(WatchlistContract.ToDto(_service.View(user)));
    }

    /// <summary>
    /// Movies and series matching <c>q</c>, in two groups: on the server (visible to
    /// the caller) and not on the server (from the server's metadata providers).
    /// </summary>
    [HttpGet("Search")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> Search([FromQuery] string? q, CancellationToken ct)
    {
        if (!TryGetCaller(out var user))
        {
            return Unauthorized();
        }

        if (!WatchlistSearch.TryReadQuery(q, out var query))
        {
            return BadRequest($"q is required (1-{WatchlistSearch.MaxQueryLength} characters).");
        }

        var library = _library.Search(user, query, WatchlistSearch.GroupLimit);
        var remote = await _remote.Search(query, ct).ConfigureAwait(false);
        var matches = remote.Count == 0 ? [] : _library.FindVisibleItemIds(user, remote.Select(r => (r.Type, r.Ids)).ToList());
        var results = WatchlistSearch.Combine(library, remote, matches, _store.Read(user).Entries);
        return Json(WatchlistContract.ToSearchDto(query, results));
    }

    /// <summary>What the server's metadata providers know about one movie or series, for its details page.</summary>
    [HttpGet("Details")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> Details(
        [FromQuery] string? type,
        [FromQuery] string? tmdbId,
        [FromQuery] string? tvdbId,
        [FromQuery] string? imdbId,
        CancellationToken ct)
    {
        if (!TryGetCaller(out var user))
        {
            return Unauthorized();
        }

        if (!TitleDetailsRules.TryReadQuery(type, tmdbId, tvdbId, imdbId, out var titleType, out var ids))
        {
            return BadRequest("type must be movie or series, with a numeric tmdbId or tvdbId, or an imdbId like tt123.");
        }

        var source = await _remote.Describe(titleType, ids, ct).ConfigureAwait(false);
        var itemId = _library.FindVisibleItemId(user, titleType, WatchlistSearch.Union(source.Ids, ids));
        var details = TitleDetailsRules.Build(titleType, source, itemId, _store.Read(user).Entries, ids);
        return details is null ? NotFound() : Json(WatchlistContract.ToDetailsDto(details));
    }

    [HttpPost("Items")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> AddItem([FromBody] JsonElement body, CancellationToken ct)
    {
        if (!TryGetCaller(out var user))
        {
            return Unauthorized();
        }

        if (!WatchlistContract.TryReadAdd(body, out var entry))
        {
            return BadRequest("Only movies and series with a TMDB, TVDB or IMDb id can go on the Watchlist.");
        }

        var (view, added) = await _service.AddAsync(user, entry, c => _posters.Find(user, entry.Type, entry.Ids, c), ct).ConfigureAwait(false);
        return Json(WatchlistContract.ToAddResponse(view, added));
    }

    [HttpDelete("Items/{id}")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    public IActionResult RemoveItem([FromRoute] string id)
    {
        if (!TryGetCaller(out var user))
        {
            return Unauthorized();
        }

        if (Guid.TryParse(id, out var entryId))
        {
            _service.Remove(user, entryId);
        }

        return NoContent();
    }

    [HttpPost("Plex")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    [ProducesResponseType(StatusCodes.Status422UnprocessableEntity)]
    [ProducesResponseType(StatusCodes.Status503ServiceUnavailable)]
    public async Task<IActionResult> LinkPlex([FromBody] JsonElement body, CancellationToken ct)
    {
        if (!TryGetCaller(out var user))
        {
            return Unauthorized();
        }

        if (!WatchlistContract.TryReadLink(body, out var token, out var replace))
        {
            return BadRequest("A Plex token is required.");
        }

        var result = await _service.LinkPlex(user, token, replace, ct).ConfigureAwait(false);
        if (result is Linked)
        {
            // Sync now rather than at the next scheduled run; the response doesn't wait for it.
            _ = Task.Run(async () =>
            {
                try
                {
                    await _sync.SyncUser(user, CancellationToken.None).ConfigureAwait(false);
                }
                catch (Exception e)
                {
                    _logger.LogWarning("Watchlist sync after linking Plex failed for user {UserId}: {Error}", user.ToString("N"), e.GetType().Name);
                }
            });
        }

        var (status, response) = WatchlistContract.ForLink(result);
        return response is null ? StatusCode(status) : Json(response, status);
    }

    [HttpDelete("Plex")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    public IActionResult UnlinkPlex()
    {
        if (!TryGetCaller(out var user))
        {
            return Unauthorized();
        }

        _service.UnlinkPlex(user);
        return NoContent();
    }

    [HttpPost("DismissCard")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    public IActionResult DismissCard()
    {
        if (!TryGetCaller(out var user))
        {
            return Unauthorized();
        }

        _service.DismissCard(user);
        return NoContent();
    }

    /// <summary>
    /// The one-time merge of the caller's old "Watchlist" playlist. A failure is
    /// logged and the GET still answers; the merge retries on the next GET.
    /// </summary>
    private void MergeOldPlaylist(Guid user)
    {
        try
        {
            var result = _merger.MergeOnce(user);
            if (!result.Ran)
            {
                return;
            }

            _logger.LogInformation(
                "Merged the old Watchlist playlist for user {UserId}: {Added} title(s) added, {Skipped} item(s) skipped",
                user.ToString("N"),
                result.Added,
                result.SkippedItemIds.Count);
            if (result.SkippedItemIds.Count > 0)
            {
                _logger.LogInformation(
                    "Watchlist merge for user {UserId} skipped items that aren't movies or shows with a TMDB/TVDB/IMDb id: {ItemIds}",
                    user.ToString("N"),
                    string.Join(',', result.SkippedItemIds));
            }

            foreach (var (playlistId, error) in result.DeleteFailures)
            {
                _logger.LogError(
                    "Watchlist merge for user {UserId} couldn't delete playlist {PlaylistId} ({Error}); the merge still counts as done",
                    user.ToString("N"),
                    playlistId.ToString("N"),
                    error.GetType().Name);
            }
        }
        catch (Exception e)
        {
            _logger.LogError(
                "Watchlist merge of the old playlist failed for user {UserId} ({Error}); it will retry on the next request",
                user.ToString("N"),
                e.GetType().Name);
        }
    }

    private static JsonResult Json(object value, int status = StatusCodes.Status200OK)
        => new(value, WatchlistContract.JsonOptions) { StatusCode = status };

    private bool TryGetCaller(out Guid user)
    {
        user = Guid.Empty;
        if (!CallerClaim.TryParse(User.FindFirstValue(UserIdClaim), out var userId) || _userManager.GetUserById(userId) is null)
        {
            return false;
        }

        user = userId;
        return true;
    }
}
