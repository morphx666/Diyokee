using System;
using static Diyokee.IMediaProvider;

namespace Diyokee.MediaProviders;

public class MediaProviderLocal(string name, string rootPath) : MediaProviderBase(name, rootPath) {

    public override List<MediaFolder> Directories(string relativePath) {
        string path = Path.Combine(RootPath, relativePath);
        return [.. Directory
                    .GetDirectories(path)
                    .OrderBy(d => d)
                    .Select(d => new MediaFolder(Path.GetFileName(d), Path.GetRelativePath(RootPath, d), Directory.GetDirectories(d).Length != 0))];
    }

    public override List<string> Files(string relativePath) {
        string path = Path.Combine(RootPath, relativePath);
        return [.. new DirectoryInfo(path)
                        .EnumerateFiles()
                        .Where(f => supportedExtensions.Contains(f.Extension))
                        .OrderBy(f => f.Name)
                        .Select(f => f.Name)];
    }

    public override List<string> Search(string relativePath, string query, bool recursive) {
        string path = Path.Combine(RootPath, relativePath);
        return [.. new DirectoryInfo(path)
                        .EnumerateFiles("*" + query + "*", recursive ? SearchOption.AllDirectories : SearchOption.TopDirectoryOnly)
                        .Where(f => supportedExtensions.Contains(f.Extension))
                        .OrderBy(f => f.Name)
                        .Select(f => Path.GetRelativePath(relativePath, f.FullName))];
    }
}
