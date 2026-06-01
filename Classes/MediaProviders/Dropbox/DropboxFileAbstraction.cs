namespace Diyokee.MediaProviders;

// Allows TagLib# to read metadata from a remote Dropbox file by reading through
// a seekable stream instead of a local file path.
public class DropboxFileAbstraction : TagLib.File.IFileAbstraction {
    private readonly Stream stream;

    public DropboxFileAbstraction(string name, Stream stream) {
        Name = name;
        this.stream = stream;
    }

    public string Name { get; }
    public Stream ReadStream => stream;
    public Stream WriteStream => throw new NotSupportedException();

    public void CloseStream(Stream stream) => stream.Dispose();
}
