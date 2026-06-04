using Dropbox.Api.Files;
using static Diyokee.MediaProviders.IMediaProvider;

namespace Diyokee.MediaProviders;

public class MediaProviderDropbox : MediaProviderBase {
    private readonly DropboxConnection connection;

    public MediaProviderDropbox(string name, DropboxConnection connection, string initialPath = "")
        : base(name, $"{BassStreamFactory.DropboxScheme}{name}") {
        this.connection = connection;
        InitialPath = initialPath;
    }

    public override List<MediaFolder> Directories(string relativePath) {
        string path = connection.DropboxPath(relativePath);
        return [.. ListAll(path, false)
                    .Where(e => e.IsFolder)
                    .Select(e => new MediaFolder(
                        e.Name,
                        relativePath == "" ? e.Name : Path.Combine(relativePath, e.Name),
                        true))
                    .OrderBy(f => f.Name)];
    }

    public override List<string> Files(string relativePath) {
        string path = connection.DropboxPath(relativePath);
        return [.. ListAll(path, false)
                    .Where(e => e.IsFile && supportedExtensions.Contains(Path.GetExtension(e.Name).ToLower()))
                    .Select(e => e.Name)
                    .OrderBy(n => n)];
    }

    public override List<string> Search(string relativePath, string query, bool recursive) {
        string basePath = connection.DropboxPath(relativePath);
        return [.. ListAll(basePath, recursive)
                    .Where(e => e.IsFile
                                && supportedExtensions.Contains(Path.GetExtension(e.Name).ToLower())
                                && e.Name.Contains(query, StringComparison.OrdinalIgnoreCase))
                    .Select(e => RelativeTo(basePath, e.PathDisplay))
                    .OrderBy(n => n)];
    }

    private static string RelativeTo(string basePath, string fullPath) {
        string b = basePath.Trim('/');
        string f = fullPath.Trim('/');
        string rel = b == "" ? f : (f.StartsWith(b + "/") ? f[(b.Length + 1)..] : f);
        return rel.Replace('/', Path.DirectorySeparatorChar);
    }

    // The Dropbox SDK calls are async, but IMediaProvider exposes synchronous
    // methods. Running the awaits via Task.Run moves them onto a thread-pool thread
    // with no captured synchronization context, so blocking on the result here does
    // not deadlock Blazor's renderer context (which it would otherwise capture).
    private List<Metadata> ListAll(string path, bool recursive) {
        return Task.Run(async () => {
            List<Metadata> entries = [];
            ListFolderResult result = await connection.Client.Files.ListFolderAsync(path, recursive);
            entries.AddRange(result.Entries);
            while(result.HasMore) {
                result = await connection.Client.Files.ListFolderContinueAsync(result.Cursor);
                entries.AddRange(result.Entries);
            }
            return entries;
        }).GetAwaiter().GetResult();
    }
}
