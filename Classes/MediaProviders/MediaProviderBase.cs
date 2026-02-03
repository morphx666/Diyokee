using static Diyokee.MediaProviders.IMediaProvider;

namespace Diyokee.MediaProviders;

public abstract class MediaProviderBase(string name, string rootPath) : IMediaProvider {
    internal string[] supportedExtensions = [ ".mp3", ".wav", ".flac", ".aac", ".ac3", ".m4a" ];
    internal bool isBusy = false;

    public string Name { get; init; } = name;
    public string RootPath { get; init; } = rootPath;
    public string InitialPath { get; set; } = "";

    public abstract List<MediaFolder> Directories(string relativePath);
    public abstract List<string> Files(string relativePath);
    public abstract List<string> Search(string relativePath, string query, bool recursive);
}
