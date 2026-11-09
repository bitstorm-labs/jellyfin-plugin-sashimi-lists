using System.Net.Mime;
using System.Security.Claims;
using System.Text.Json;
using Jellyfin.Plugin.SashimiLists.Core.Watchlist;
using MediaBrowser.Controller.Library;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

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
    private readonly IUserManager _userManager;

    public WatchlistController(WatchlistService service, PosterFinder posters, IUserManager userManager)
    {
        _service = service;
        _posters = posters;
        _userManager = userManager;
    }

    [HttpGet]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public IActionResult Get()
    {
        if (!TryGetCaller(out var user))
        {
            return Unauthorized();
        }

        return Json(WatchlistContract.ToDto(_service.View(user)));
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

        if (PosterUrls.Clean(entry.PosterUrl) is null)
        {
            entry = entry with { PosterUrl = await _posters.Find(user, entry.Type, entry.Ids, ct).ConfigureAwait(false) };
        }

        var (view, added) = _service.Add(user, entry);
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

    private static JsonResult Json(object value, int status = StatusCodes.Status200OK)
        => new(value, WatchlistContract.JsonOptions) { StatusCode = status };

    private bool TryGetCaller(out Guid user)
    {
        user = Guid.Empty;
        if (!Guid.TryParse(User.FindFirstValue(UserIdClaim), out var userId) || _userManager.GetUserById(userId) is null)
        {
            return false;
        }

        user = userId;
        return true;
    }
}
