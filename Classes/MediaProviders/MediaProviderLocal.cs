using static Diyokee.MediaProviders.IMediaProvider;

namespace Diyokee.MediaProviders;

public class MediaProviderLocal : MediaProviderBase {
    public MediaProviderLocal(string name, string rootPath) : base(name, rootPath) { }

    public MediaProviderLocal(string name, string rootPath, string initialPath) : base(name, rootPath) {
        InitialPath = initialPath;
    }

    public override List<MediaFolder> Directories(string relativePath) {
        string path = Path.Combine(RootPath, relativePath);
        return [.. Directory
                    .GetDirectories(path).Append(relativePath == "" ? "." : "").ToList()
                    .OrderBy(d => d)
                    .Where(d => d != "")
                    .Select(d => new MediaFolder(
                        Path.GetFileName(d),
                        d != "." ? Path.GetRelativePath(RootPath, d) : "",
                        d != "." && Directory.GetDirectories(d).Length != 0))];
    }

    public override List<string> Files(string relativePath) {
        string path = Path.Combine(RootPath, relativePath);
        if(!Directory.Exists(path)) return [];

        return [.. new DirectoryInfo(path)
                        .EnumerateFiles()
                        .Where(f => !f.Name.StartsWith("._"))
                        .Where(f => supportedExtensions.Contains(f.Extension))
                        .OrderBy(f => f.Name)
                        .Select(f => f.Name)];
    }

    public override List<string> Search(string relativePath, string query, bool recursive) {
        string path = Path.Combine(RootPath, relativePath);
        if(!Directory.Exists(path)) return [];

        return [.. new DirectoryInfo(path)
                        .EnumerateFiles("*" + query + "*", recursive ? SearchOption.AllDirectories : SearchOption.TopDirectoryOnly)
                        .Where(f => supportedExtensions.Contains(f.Extension))
                        .OrderBy(f => f.Name)
                        .Select(f => Path.GetRelativePath(relativePath, f.FullName))];
    }

    public static string DefaultDirectory {
        get {
            string path = Environment.GetFolderPath(Environment.SpecialFolder.MyMusic);
            if(path == "" || !Directory.Exists(path)) {
                path = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            }
            return path;
        }
    }
}
