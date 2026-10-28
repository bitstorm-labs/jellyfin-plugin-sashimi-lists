using Jellyfin.Plugin.SashimiLists.Core;

namespace Jellyfin.Plugin.SashimiLists.Core.Tests;

public class PlaylistOwnerServiceTests
{
    private static readonly Guid Alice = Guid.Parse("aaaaaaaa-0000-0000-0000-000000000001");
    private static readonly Guid Bob = Guid.Parse("bbbbbbbb-0000-0000-0000-000000000002");
    private static readonly Guid Carol = Guid.Parse("cccccccc-0000-0000-0000-000000000003");
    private static readonly Guid Admin = Guid.Parse("dddddddd-0000-0000-0000-000000000004");
    private static readonly Guid Gone = Guid.Parse("eeeeeeee-0000-0000-0000-000000000005");

    private static readonly Guid PrivateList = Guid.Parse("10000000-0000-0000-0000-000000000001");
    private static readonly Guid PublicList = Guid.Parse("20000000-0000-0000-0000-000000000002");
    private static readonly Guid SharedWithBob = Guid.Parse("30000000-0000-0000-0000-000000000003");
    private static readonly Guid OrphanedPublic = Guid.Parse("40000000-0000-0000-0000-000000000004");
    private static readonly Guid ParentalBlocked = Guid.Parse("50000000-0000-0000-0000-000000000005");
    private static readonly Guid Missing = Guid.Parse("90000000-0000-0000-0000-000000000009");

    private static Caller User(Guid id) => new(id, IsAdmin: false);

    private static readonly Caller AdminCaller = new(Admin, IsAdmin: true);

    private sealed class FakeDirectory : IPlaylistDirectory
    {
        public Dictionary<Guid, PlaylistRecord> Playlists { get; } = new()
        {
            [PrivateList] = new(PrivateList, Alice, false, []),
            [PublicList] = new(PublicList, Alice, true, []),
            [SharedWithBob] = new(SharedWithBob, Alice, false, [Bob]),
            [OrphanedPublic] = new(OrphanedPublic, Gone, true, []),
            [ParentalBlocked] = new(ParentalBlocked, Alice, true, [], ServerAllows: false),
        };

        public int NameLookups { get; private set; }

        public PlaylistRecord? FindPlaylist(Guid playlistId, Caller caller) =>
            Playlists.GetValueOrDefault(playlistId);

        public string? FindUserName(Guid userId)
        {
            NameLookups++;
            return userId == Alice ? "alice" : userId == Bob ? "bob" : null;
        }
    }

    private static PlaylistOwnerService Service(FakeDirectory? dir = null) => new(dir ?? new FakeDirectory());

    [Fact]
    public void Owner_sees_their_private_list()
    {
        var owner = Service().GetOwner(User(Alice), PrivateList);
        Assert.Equal(new OwnerInfo(Alice.ToString("N"), "alice"), owner);
    }

    [Fact]
    public void Private_list_is_hidden_from_everyone_else()
    {
        Assert.Null(Service().GetOwner(User(Bob), PrivateList));
        Assert.Null(Service().GetOwner(User(Carol), PrivateList));
    }

    [Fact]
    public void Shared_list_is_visible_to_the_share_only()
    {
        Assert.Equal(new OwnerInfo(Alice.ToString("N"), "alice"), Service().GetOwner(User(Bob), SharedWithBob));
        Assert.Null(Service().GetOwner(User(Carol), SharedWithBob));
    }

    [Fact]
    public void Public_list_is_visible_to_any_signed_in_user()
    {
        Assert.Equal(new OwnerInfo(Alice.ToString("N"), "alice"), Service().GetOwner(User(Carol), PublicList));
    }

    [Fact]
    public void Admin_sees_lists_that_are_neither_theirs_nor_shared_nor_public()
    {
        Assert.Equal(new OwnerInfo(Alice.ToString("N"), "alice"), Service().GetOwner(AdminCaller, PrivateList));
        Assert.NotNull(Service().GetOwner(AdminCaller, ParentalBlocked));
    }

    [Fact]
    public void Jellyfin_refusing_the_list_hides_it_from_non_admins()
    {
        Assert.Null(Service().GetOwner(User(Carol), ParentalBlocked));
        Assert.Null(Service().GetOwner(User(Alice), ParentalBlocked));
    }

    [Fact]
    public void Missing_or_non_playlist_item_is_not_found()
    {
        Assert.Null(Service().GetOwner(User(Alice), Missing));
        Assert.Null(Service().GetOwner(AdminCaller, Missing));
    }

    [Fact]
    public void Deleted_owner_has_an_empty_name()
    {
        Assert.Equal(new OwnerInfo(Gone.ToString("N"), string.Empty), Service().GetOwner(User(Carol), OrphanedPublic));
    }

    [Fact]
    public void Empty_owner_never_matches_a_caller()
    {
        var dir = new FakeDirectory();
        var noOwner = Guid.NewGuid();
        dir.Playlists[noOwner] = new(noOwner, Guid.Empty, false, []);
        Assert.Null(Service(dir).GetOwner(new Caller(Guid.Empty, false), noOwner));
    }

    [Fact]
    public void Batch_keeps_only_what_the_caller_can_see()
    {
        var owners = Service().GetOwners(User(Bob), [PrivateList, PublicList, SharedWithBob, OrphanedPublic, ParentalBlocked, Missing]);

        Assert.Equal(
            new[] { PublicList, SharedWithBob, OrphanedPublic }.Select(g => g.ToString("N")).Order(),
            owners.Keys.Order());
        Assert.Equal(new OwnerInfo(Alice.ToString("N"), "alice"), owners[SharedWithBob.ToString("N")]);
        Assert.Equal(string.Empty, owners[OrphanedPublic.ToString("N")].OwnerName);
    }

    [Fact]
    public void Batch_for_a_stranger_omits_private_and_shared_lists()
    {
        var owners = Service().GetOwners(User(Carol), [PrivateList, SharedWithBob]);
        Assert.Empty(owners);
    }

    [Fact]
    public void Batch_for_an_admin_includes_every_existing_playlist()
    {
        var owners = Service().GetOwners(AdminCaller, [PrivateList, PublicList, SharedWithBob, OrphanedPublic, ParentalBlocked, Missing]);
        Assert.Equal(5, owners.Count);
        Assert.DoesNotContain(Missing.ToString("N"), owners.Keys);
    }

    [Fact]
    public void Batch_looks_each_owner_name_up_once()
    {
        var dir = new FakeDirectory();
        Service(dir).GetOwners(AdminCaller, [PrivateList, PublicList, SharedWithBob, PrivateList]);
        Assert.Equal(1, dir.NameLookups);
    }

    [Fact]
    public void Ids_parse_in_either_format_skip_junk_and_dedupe()
    {
        var ids = $"{PublicList:N}, {PublicList:D},not-a-guid,,{PrivateList:N}";
        Assert.True(PlaylistOwnerService.TryParseIds(ids, out var parsed));
        Assert.Equal([PublicList, PrivateList], parsed);
    }

    [Fact]
    public void Empty_ids_parse_to_nothing()
    {
        Assert.True(PlaylistOwnerService.TryParseIds(null, out var parsed));
        Assert.Empty(parsed);
    }

    [Fact]
    public void More_than_200_ids_is_rejected_and_200_is_fine()
    {
        var ok = string.Join(',', Enumerable.Range(0, 200).Select(_ => Guid.NewGuid().ToString("N")));
        var tooMany = ok + "," + Guid.NewGuid().ToString("N");
        Assert.True(PlaylistOwnerService.TryParseIds(ok, out _));
        Assert.False(PlaylistOwnerService.TryParseIds(tooMany, out _));
    }

    [Theory]
    [InlineData(false, false, false, true, false)] // stranger, private
    [InlineData(true, false, false, true, true)]   // owner
    [InlineData(false, true, false, true, true)]   // shared
    [InlineData(false, false, true, true, true)]   // public
    [InlineData(false, false, true, false, false)] // public but Jellyfin says no
    public void Visibility_rule_table(bool isOwner, bool isShared, bool open, bool serverAllows, bool expected)
    {
        var caller = User(Carol);
        var record = new PlaylistRecord(
            PrivateList,
            isOwner ? Carol : Alice,
            open,
            isShared ? [Carol] : [Bob],
            serverAllows);
        Assert.Equal(expected, PlaylistVisibility.CanSee(caller, record));
        Assert.True(PlaylistVisibility.CanSee(AdminCaller, record));
    }
}
