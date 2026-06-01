using System.Collections.Concurrent;
using Dropbox.Api;
using Dropbox.Api.Files;

namespace Diyokee.MediaProviders;

// Represents an authenticated Dropbox session for a single media provider.
// Connections are cached by provider name so the streaming layer can resolve a
// connection from a "dropbox://" filename without going through the UI.
public class DropboxConnection {
    public static readonly HttpClient Http = new();
    private static readonly ConcurrentDictionary<string, DropboxConnection> connections = new();

    public string Name { get; }
    public string BaseFolder { get; }
    public DropboxClient Client { get; }

    private readonly Dictionary<string, (string Url, long Size, DateTime Timestamp)> linkCache = [];
    private readonly Lock linkLock = new();

    private DropboxConnection(string name, string refreshToken, string? baseFolder) {
        Name = name;
        BaseFolder = baseFolder ?? "";
        Client = new DropboxClient(refreshToken, DropboxOAuth.AppKey);
    }

    public static DropboxConnection GetOrCreate(string name, string refreshToken, string baseFolder) {
        return connections.AddOrUpdate(name,
            _ => new DropboxConnection(name, refreshToken, baseFolder),
            (_, existing) => existing.BaseFolder == (baseFolder ?? "")
                                ? existing
                                : new DropboxConnection(name, refreshToken, baseFolder));
    }

    public static DropboxConnection? TryGet(string name) {
        return connections.TryGetValue(name, out DropboxConnection? c) ? c : null;
    }

    // Drops the cached connection for the named provider, if any. Used when the user
    // revokes a Dropbox connection so the stale client is not reused.
    public static void Remove(string name) {
        connections.TryRemove(name, out _);
    }

    // Creates a standalone connection that is not added to the shared cache. Used by
    // the folder browser, which needs to list the whole account (BaseFolder empty)
    // without disturbing the live connection registered for a provider.
    public static DropboxConnection CreateTransient(string refreshToken) {
        return new DropboxConnection("", refreshToken, "");
    }

    // Lists the names of the immediate sub-folders of the given Dropbox API path
    // (use "" for the account root). Folders are returned sorted by name.
    public async Task<List<string>> ListFolders(string dropboxPath) {
        List<string> folders = [];
        ListFolderResult result = await Client.Files.ListFolderAsync(dropboxPath);
        folders.AddRange(result.Entries.Where(e => e.IsFolder).Select(e => e.Name));
        while(result.HasMore) {
            result = await Client.Files.ListFolderContinueAsync(result.Cursor);
            folders.AddRange(result.Entries.Where(e => e.IsFolder).Select(e => e.Name));
        }
        return [.. folders.OrderBy(n => n, StringComparer.OrdinalIgnoreCase)];
    }

    // Translates an OS-relative path (as used by the rest of the app) into a
    // Dropbox API path. The Dropbox root is represented by an empty string.
    public string DropboxPath(string osRelative) {
        string rel = (osRelative ?? "").Replace('\\', '/').Trim('/');
        string baseFolder = BaseFolder.Replace('\\', '/').Trim('/');
        string combined = $"{baseFolder}/{rel}".Trim('/');
        return combined == "" ? "" : "/" + combined;
    }

    public (string Url, long Size) GetTemporaryLink(string dropboxPath) {
        lock(linkLock) {
            if(linkCache.TryGetValue(dropboxPath, out var cached) && (DateTime.UtcNow - cached.Timestamp).TotalHours < 3) {
                return (cached.Url, cached.Size);
            }
        }

        GetTemporaryLinkResult result = Client.Files.GetTemporaryLinkAsync(dropboxPath).GetAwaiter().GetResult();
        (string Url, long Size, DateTime Timestamp) entry = (result.Link, (long)result.Metadata.Size, DateTime.UtcNow);
        lock(linkLock) {
            linkCache[dropboxPath] = entry;
        }
        return (entry.Url, entry.Size);
    }

    // Parses a "dropbox://{provider}/{relative}" filename and returns the
    // matching connection together with the resolved Dropbox API path.
    public static (DropboxConnection? Connection, string Path) ResolveFromFilename(string filename) {
        string rest = filename[BassStreamFactory.DropboxScheme.Length..];
        int idx = rest.IndexOfAny(['\\', '/']);
        string name = idx < 0 ? rest : rest[..idx];
        string osRelative = idx < 0 ? "" : rest[(idx + 1)..];
        DropboxConnection? conn = TryGet(name);
        return (conn, conn?.DropboxPath(osRelative) ?? "");
    }

    public static TagLib.File? CreateTagLibFile(string filename) {
        (DropboxConnection? conn, string path) = ResolveFromFilename(filename);
        if(conn == null) return null;

        (string url, long size) = conn.GetTemporaryLink(path);
        DropboxRandomAccessStream stream = new(Http, url, size);
        return TagLib.File.Create(new DropboxFileAbstraction(System.IO.Path.GetFileName(path), stream));
    }
}
