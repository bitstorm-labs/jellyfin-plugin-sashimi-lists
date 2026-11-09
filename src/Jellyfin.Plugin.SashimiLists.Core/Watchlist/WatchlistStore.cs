using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Jellyfin.Plugin.SashimiLists.Core.Watchlist;

/// <summary>
/// One <c>{userId:N}.json</c> per user. Writes go to a temp file and are moved
/// over the old one, so a crash never leaves half a file. A per-user lock makes
/// each <see cref="Update{T}"/> an atomic read-modify-write.
/// </summary>
public sealed class FileWatchlistStore : IWatchlistStore
{
    private static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = false,
        Converters = { new JsonStringEnumConverter() },
    };

    private readonly string _directory;
    private readonly ConcurrentDictionary<Guid, object> _locks = new();

    public FileWatchlistStore(string directory)
    {
        _directory = directory;
    }

    public T Update<T>(Guid userId, Func<UserWatchlist, T> change)
    {
        ArgumentNullException.ThrowIfNull(change);
        lock (LockFor(userId))
        {
            var watchlist = Load(userId);
            var result = change(watchlist);
            Save(userId, watchlist);
            return result;
        }
    }

    public UserWatchlist Read(Guid userId)
    {
        lock (LockFor(userId))
        {
            return Load(userId);
        }
    }

    public IEnumerable<Guid> AllUsers()
    {
        if (!Directory.Exists(_directory))
        {
            return [];
        }

        return Directory.EnumerateFiles(_directory, "*.json")
            .Select(f => Guid.TryParseExact(Path.GetFileNameWithoutExtension(f), "N", out var id) ? id : Guid.Empty)
            .Where(id => id != Guid.Empty)
            .ToList();
    }

    public IEnumerable<Guid> UsersWithPlex() => AllUsers().Where(u => Read(u).Plex is not null).ToList();

    public void Delete(Guid userId)
    {
        lock (LockFor(userId))
        {
            File.Delete(PathFor(userId));
        }
    }

    private object LockFor(Guid userId) => _locks.GetOrAdd(userId, _ => new object());

    private string PathFor(Guid userId) => Path.Combine(_directory, userId.ToString("N") + ".json");

    private UserWatchlist Load(Guid userId)
    {
        var path = PathFor(userId);
        if (!File.Exists(path))
        {
            return new UserWatchlist();
        }

        using var stream = File.OpenRead(path);
        var watchlist = JsonSerializer.Deserialize<UserWatchlist>(stream, Json) ?? new UserWatchlist();
        // The deserializer builds a default-comparer set; keep ratingKeys ordinal.
        watchlist.LastPlexSnapshot = new HashSet<string>(watchlist.LastPlexSnapshot, StringComparer.Ordinal);
        return watchlist;
    }

    private void Save(Guid userId, UserWatchlist watchlist)
    {
        Directory.CreateDirectory(_directory);
        var path = PathFor(userId);
        var temp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        File.WriteAllBytes(temp, JsonSerializer.SerializeToUtf8Bytes(watchlist, Json));
        File.Move(temp, path, overwrite: true);
    }
}
