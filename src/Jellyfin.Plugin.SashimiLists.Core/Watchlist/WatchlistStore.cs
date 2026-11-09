using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Jellyfin.Plugin.SashimiLists.Core.Watchlist;

/// <summary>
/// One <c>{userId:N}.json</c> per user. Writes go to a temp file and are moved
/// over the old one, so a crash never leaves half a file. A per-user lock makes
/// each <see cref="Update{T}"/> an atomic read-modify-write. The files hold
/// Plex tokens, so on Unix the directory is 0700 and each file 0600.
/// </summary>
public sealed class FileWatchlistStore : IWatchlistStore
{
    private static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = false,
        Converters = { new JsonStringEnumConverter() },
    };

    private readonly string _directory;
    private const UnixFileMode DirectoryMode = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute;
    private const UnixFileMode FileMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;

    private readonly ConcurrentDictionary<Guid, object> _locks = new();
    private volatile bool _directoryReady;

    private readonly Action<Guid, Exception>? _onCorrupt;

    /// <param name="directory">Where the per-user files live.</param>
    /// <param name="onCorrupt">Told when a user's file can't be read and is moved aside.</param>
    public FileWatchlistStore(string directory, Action<Guid, Exception>? onCorrupt = null)
    {
        _directory = directory;
        _onCorrupt = onCorrupt;
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

        UserWatchlist watchlist;
        try
        {
            using var stream = File.OpenRead(path);
            watchlist = JsonSerializer.Deserialize<UserWatchlist>(stream, Json) ?? new UserWatchlist();
        }
        catch (JsonException e)
        {
            // Keep the bytes for recovery, and let the user carry on with an empty
            // (unlinked) list rather than a 500 on every request. Unlinked means no
            // sync reads the empty list as removals.
            var aside = path + ".corrupt-" + DateTime.UtcNow.ToString("yyyyMMddHHmmss", System.Globalization.CultureInfo.InvariantCulture);
            File.Move(path, aside, overwrite: true);
            _onCorrupt?.Invoke(userId, e);
            return new UserWatchlist();
        }

        // The deserializer builds a default-comparer set; keep ratingKeys ordinal.
        watchlist.LastPlexSnapshot = new HashSet<string>(watchlist.LastPlexSnapshot, StringComparer.Ordinal);
        return watchlist;
    }

    private void Save(Guid userId, UserWatchlist watchlist)
    {
        EnsureDirectory();
        var path = PathFor(userId);
        var temp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        var options = new FileStreamOptions { Mode = System.IO.FileMode.CreateNew, Access = FileAccess.Write };
        if (!OperatingSystem.IsWindows())
        {
            options.UnixCreateMode = FileMode;
        }

        try
        {
            using (var stream = new FileStream(temp, options))
            {
                JsonSerializer.Serialize(stream, watchlist, Json);
                // On disk before the rename, so a power cut can't leave an empty file in its place.
                stream.Flush(flushToDisk: true);
            }

            File.Move(temp, path, overwrite: true);
        }
        catch
        {
            File.Delete(temp);
            throw;
        }
    }

    private void EnsureDirectory()
    {
        if (_directoryReady)
        {
            return;
        }

        if (OperatingSystem.IsWindows())
        {
            Directory.CreateDirectory(_directory);
        }
        else
        {
            Directory.CreateDirectory(_directory, DirectoryMode);
            // CreateDirectory leaves an existing directory's mode alone; tighten it.
            File.SetUnixFileMode(_directory, DirectoryMode);
        }

        _directoryReady = true;
    }
}
