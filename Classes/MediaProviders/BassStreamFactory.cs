using Un4seen.Bass;

namespace Diyokee.MediaProviders;

// Central entry point for creating BASS streams from a DFile.Filename. Local
// files go straight to BASS_StreamCreateFile; "dropbox://" files are streamed on
// demand through BASS_StreamCreateFileUser backed by HTTP range requests.
public static class BassStreamFactory {
    public const string DropboxScheme = "dropbox://";

    public static bool IsRemote(string filename) {
        return filename.StartsWith(DropboxScheme, StringComparison.OrdinalIgnoreCase);
    }

    public static int CreateStreamFile(string filename, long offset, long length, BASSFlag flags) {
        if(IsRemote(filename)) {
            (DropboxConnection? conn, string path) = DropboxConnection.ResolveFromFilename(filename);
            if(conn == null) return 0;

            (string url, long size) = conn.GetTemporaryLink(path);
            DropboxRandomAccessStream stream = new(DropboxConnection.Http, url, size);
            DropboxBassFile file = new(stream);
            return Bass.BASS_StreamCreateFileUser(BASSStreamSystem.STREAMFILE_NOBUFFER, flags, file.Procs, file.User);
        }

        return Bass.BASS_StreamCreateFile(filename, offset, length, flags);
    }
}
