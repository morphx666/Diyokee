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
    public string AppKey { get; }
    public string BaseFolder { get; }
    public DropboxClient Client { get; }

    private readonly Dictionary<string, (string Url, long Size, DateTime Timestamp)> linkCache = [];
    private readonly Lock linkLock = new();

    private DropboxConnection(string name, string appKey, string refreshToken, string? baseFolder) {
        Name = name;
        AppKey = appKey;
        BaseFolder = baseFolder ?? "";
        Client = new DropboxClient(refreshToken, appKey);
    }

    public static DropboxConnection GetOrCreate(string name, string appKey, string refreshToken, string baseFolder) {
        return connections.AddOrUpdate(name,
            _ => new DropboxConnection(name, appKey, refreshToken, baseFolder),
            (_, existing) => existing.AppKey == appKey && existing.BaseFolder == (baseFolder ?? "")
                                ? existing
                                : new DropboxConnection(name, appKey, refreshToken, baseFolder));
    }

    public static DropboxConnection? TryGet(string name) {
        return connections.TryGetValue(name, out DropboxConnection? c) ? c : null;
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
