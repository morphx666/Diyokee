using System.Net.Http.Headers;

namespace Diyokee.MediaProviders;

// A read-only, seekable Stream that serves data from a Dropbox temporary link
// using HTTP range requests. Data is fetched in fixed-size blocks and cached so
// that BASS's many small reads don't translate into one HTTP request each.
public class DropboxRandomAccessStream : Stream {
    private const int BlockSize = 512 * 1024;

    private readonly HttpClient http;
    private readonly string url;
    private readonly long length;
    private long position;

    private byte[] block = [];
    private long blockStart = -1;
    private int blockLen;

    public DropboxRandomAccessStream(HttpClient http, string url, long length) {
        this.http = http;
        this.url = url;
        this.length = length;
    }

    public override bool CanRead => true;
    public override bool CanSeek => true;
    public override bool CanWrite => false;
    public override long Length => length;
    public override long Position { get => position; set => position = value; }

    public override void Flush() { }
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

    public override long Seek(long offset, SeekOrigin origin) {
        position = origin switch {
            SeekOrigin.Begin => offset,
            SeekOrigin.Current => position + offset,
            SeekOrigin.End => length + offset,
            _ => position
        };
        return position;
    }

    public override int Read(byte[] buffer, int offset, int count) {
        if(count <= 0 || position >= length) return 0;

        int total = 0;
        while(count > 0 && position < length) {
            EnsureBlock(position);
            int within = (int)(position - blockStart);
            int available = blockLen - within;
            if(available <= 0) break;

            int n = Math.Min(available, count);
            Array.Copy(block, within, buffer, offset, n);
            offset += n;
            count -= n;
            position += n;
            total += n;
        }
        return total;
    }

    private void EnsureBlock(long pos) {
        if(blockStart >= 0 && pos >= blockStart && pos < blockStart + blockLen) return;

        long start = pos / BlockSize * BlockSize;
        long end = Math.Min(start + BlockSize, length) - 1;
        int len = (int)(end - start + 1);

        using HttpRequestMessage request = new(HttpMethod.Get, url);
        request.Headers.Range = new RangeHeaderValue(start, end);
        using HttpResponseMessage response = http.Send(request);
        response.EnsureSuccessStatusCode();

        using Stream content = response.Content.ReadAsStream();
        byte[] buffer = new byte[len];
        int read = 0;
        while(read < len) {
            int r = content.Read(buffer, read, len - read);
            if(r <= 0) break;
            read += r;
        }

        block = buffer;
        blockStart = start;
        blockLen = read;
    }
}
