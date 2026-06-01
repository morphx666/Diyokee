using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using Un4seen.Bass;

namespace Diyokee.MediaProviders;

// Wraps a seekable Stream so it can be fed to BASS via BASS_StreamCreateFileUser.
// The instance (and therefore the callback delegates) is kept alive in a static
// registry until BASS invokes the close callback, preventing premature GC.
public class DropboxBassFile {
    private static readonly ConcurrentDictionary<int, DropboxBassFile> active = new();
    private static int nextId;

    private readonly Stream stream;
    private readonly int id;

    private readonly FILECLOSEPROC closeProc;
    private readonly FILELENPROC lenProc;
    private readonly FILEREADPROC readProc;
    private readonly FILESEEKPROC seekProc;

    private byte[] temp = new byte[65536];

    public BASS_FILEPROCS Procs { get; }
    public IntPtr User => id;

    public DropboxBassFile(Stream stream) {
        this.stream = stream;
        id = Interlocked.Increment(ref nextId);

        closeProc = OnClose;
        lenProc = OnLength;
        readProc = OnRead;
        seekProc = OnSeek;
        Procs = new BASS_FILEPROCS(closeProc, lenProc, readProc, seekProc);

        active[id] = this;
    }

    private void OnClose(IntPtr user) {
        active.TryRemove(id, out _);
        try { stream.Dispose(); } catch { }
    }

    private long OnLength(IntPtr user) {
        try { return stream.Length; } catch { return 0; }
    }

    private bool OnSeek(long offset, IntPtr user) {
        try {
            stream.Seek(offset, SeekOrigin.Begin);
            return true;
        } catch {
            return false;
        }
    }

    private int OnRead(IntPtr buffer, int length, IntPtr user) {
        try {
            if(temp.Length < length) temp = new byte[length];
            int read = stream.Read(temp, 0, length);
            if(read <= 0) return 0;
            Marshal.Copy(temp, 0, buffer, read);
            return read;
        } catch {
            return 0;
        }
    }
}
