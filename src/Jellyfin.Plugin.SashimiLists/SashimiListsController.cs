using System.Net.Mime;
using System.Security.Claims;
using Jellyfin.Data;
using Jellyfin.Database.Implementations.Enums;
using Jellyfin.Plugin.SashimiLists.Core;
using MediaBrowser.Controller.Library;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace Jellyfin.Plugin.SashimiLists;

/// <summary>
/// Who owns a playlist. Jellyfin has no non-admin API for that, and deleting any
/// user wipes the self-share clients used to record it. Read-only.
/// </summary>
[ApiController]
[Authorize]
[Route("SashimiLists")]
[Produces(MediaTypeNames.Application.Json)]
public class SashimiListsController : ControllerBase
{
    // Jellyfin.Api.Constants.InternalClaimTypes.UserId; Jellyfin.Api isn't a plugin reference.
    private const string UserIdClaim = "Jellyfin-UserId";

    private readonly ILibraryManager _libraryManager;
    private readonly IUserManager _userManager;

    public SashimiListsController(ILibraryManager libraryManager, IUserManager userManager)
    {
        _libraryManager = libraryManager;
        _userManager = userManager;
    }

    /// <summary>The owner of one playlist the caller can see; 404 otherwise.</summary>
    [HttpGet("Playlists/{playlistId}/Owner")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public ActionResult<OwnerInfo> GetOwner([FromRoute] string playlistId)
    {
        if (!Guid.TryParse(playlistId, out var id) || !TryGetCaller(out var caller, out var service))
        {
            return NotFound();
        }

        var owner = service.GetOwner(caller, id);
        return owner is null ? NotFound() : owner;
    }

    /// <summary>Owners of up to 200 playlists, keyed by playlist id; ones the caller can't see are left out.</summary>
    [HttpGet("Owners")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public ActionResult<Dictionary<string, OwnerInfo>> GetOwners([FromQuery] string? ids)
    {
        if (!PlaylistOwnerService.TryParseIds(ids, out var parsed))
        {
            return BadRequest($"At most {PlaylistOwnerService.MaxBatchIds} ids per request.");
        }

        if (!TryGetCaller(out var caller, out var service))
        {
            return new Dictionary<string, OwnerInfo>();
        }

        return service.GetOwners(caller, parsed);
    }

    private bool TryGetCaller(out Caller caller, out PlaylistOwnerService service)
    {
        caller = new Caller(Guid.Empty, false);
        service = null!;
        var claim = User.FindFirstValue(UserIdClaim);
        if (!Guid.TryParse(claim, out var userId))
        {
            return false;
        }

        var user = _userManager.GetUserById(userId);
        if (user is null)
        {
            return false;
        }

        caller = new Caller(user.Id, user.HasPermission(PermissionKind.IsAdministrator));
        service = new PlaylistOwnerService(new JellyfinPlaylistDirectory(_libraryManager, _userManager, user));
        return true;
    }
}
