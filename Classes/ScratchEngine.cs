using System.Threading;
using Un4seen.Bass;

namespace Diyokee;

// Owns playback position for a Player.
//
// The engine is a user (STREAMPROC) stream that sits where the BASS_FX reverse stream used to
// be: master decode stream -> ScratchEngine -> gain -> tempo -> FX -> splitters -> device mixers.
// It pulls from the master decode stream and hands the data to BASS, tracking exactly which
// source frame each output frame came from.
//
// Phase 1 pins Velocity at 1.0, where the engine is a byte-for-byte pass-through: the data is
// pulled straight into BASS's own buffer with no resampling, no interpolation and no copy, so
// the audio is bit-identical to the previous chain. Phase 2 adds the variable-rate path.
//
// Why the engine has to own position at all: a STREAMPROC stream reports position as bytes
// emitted, which only ever increases - it cannot represent a loop wrap, a seek, or (later)
// playing backwards. So BASS's idea of "where are we" stops being usable as track position and
// the engine has to provide it instead. See SourceSecondsAtOutputFrame.
public sealed class ScratchEngine : IDisposable {
    // Maps a point in the stream we emit to the point in the source file it came from.
    // A new segment starts whenever playback stops being continuous - a seek or a loop wrap.
    private struct Segment {
        public long OutFrame;   // frame index in the stream we emit, since the last output reset
        public long SrcFrame;   // matching frame index in the source file
    }

    private const int SegmentCount = 64;        // power of two
    private const int SegmentMask = SegmentCount - 1;

    private readonly int source;                // master decode stream, owned by the caller
    private readonly int bytesPerFrame;
    private readonly int sampleRate;
    private readonly long lengthFrames;

    private readonly STREAMPROC proc;           // kept alive for as long as the stream exists
    private int stream;

    private readonly Segment[] segments = new Segment[SegmentCount];
    private long segmentHead;                   // total segments ever pushed
    private long segmentVersion;                // seqlock: odd while being written

    private long outputFrames;                  // frames emitted since the last output reset
    private long sourceFrame;                   // playhead, in source frames
    private bool ended;

    // Seek requests are applied by the callback rather than the caller, so the playhead is only
    // ever mutated on one thread.
    private long pendingSeekFrame = -1;
    private double pendingSeekSeconds;
    private volatile bool seekPending;

    public Loop Loop { get; set; } = new();

    public int StreamHandle => stream;
    public int BytesPerFrame => bytesPerFrame;
    public double LengthSeconds => lengthFrames / (double)sampleRate;

    // Phase 1 keeps this at 1.0. Phase 2 drives it from the jog wheel / mouse.
    public double Velocity { get; set; } = 1.0;

    public const int DefaultSampleRate = 44100;

    public ScratchEngine(int sourceHandle, int sampleRate, int channels, Loop loop) {
        source = sourceHandle;
        this.sampleRate = sampleRate <= 0 ? DefaultSampleRate : sampleRate;
        bytesPerFrame = Math.Max(1, channels) * sizeof(float);
        Loop = loop;

        long lengthBytes = Bass.BASS_ChannelGetLength(sourceHandle, BASSMode.BASS_POS_BYTE);
        lengthFrames = lengthBytes > 0 ? lengthBytes / bytesPerFrame : 0;

        sourceFrame = PositionOf(sourceHandle);
        PushSegment(0, sourceFrame);

        proc = StreamProc;
        stream = Bass.BASS_StreamCreate(this.sampleRate, Math.Max(1, channels),
                                        BASSFlag.BASS_SAMPLE_FLOAT | BASSFlag.BASS_STREAM_DECODE,
                                        proc, IntPtr.Zero);
    }

    private long PositionOf(int handle) {
        long pos = Bass.BASS_ChannelGetPosition(handle, BASSMode.BASS_POS_BYTE);
        return pos > 0 ? pos / bytesPerFrame : 0;
    }

    // ---------------------------------------------------------------- control side

    // Asks for the playhead to move. The caller must then flush the chain (see
    // Player.SeekSeconds) so the new position is heard immediately rather than after the
    // buffered audio drains.
    public void RequestSeek(double seconds) {
        if(seconds < 0) seconds = 0;
        double max = LengthSeconds;
        if(max > 0 && seconds > max) seconds = max;

        pendingSeekSeconds = seconds;
        Interlocked.Exchange(ref pendingSeekFrame, (long)(seconds * sampleRate));
        seekPending = true;
    }

    // Track position that is currently being *heard*, given the mixer's report of how far into
    // our emitted stream playback has reached. BASS_Mixer_ChannelGetPosition already compensates
    // for downstream buffering, so this stays honest without the engine having to model latency.
    public double SourceSecondsAtOutputFrame(long outFrame) {
        if(seekPending) return pendingSeekSeconds;

        for(int attempt = 0; attempt < 8; attempt++) {
            long before = Volatile.Read(ref segmentVersion);
            if((before & 1) != 0) continue;              // a write is in progress

            long head = Volatile.Read(ref segmentHead);
            long best = -1;
            long start = Math.Max(0, head - SegmentCount);
            for(long i = head - 1; i >= start; i--) {
                Segment s = segments[i & SegmentMask];
                if(s.OutFrame <= outFrame) {
                    best = s.SrcFrame + (outFrame - s.OutFrame);
                    break;
                }
            }

            if(Volatile.Read(ref segmentVersion) == before) {
                if(best < 0) return 0;
                // A stale output position read across a reset could otherwise project past the
                // end of the track.
                if(lengthFrames > 0 && best > lengthFrames) best = lengthFrames;
                return best / (double)sampleRate;
            }
        }

        return Volatile.Read(ref sourceFrame) / (double)sampleRate;
    }

    // The playhead as the engine sees it - ahead of what is being heard by the pipeline's
    // buffering. Used only where the decode position is genuinely what is wanted.
    public double DecodeSeconds => Volatile.Read(ref sourceFrame) / (double)sampleRate;

    // ---------------------------------------------------------------- audio thread

    private int StreamProc(int handle, IntPtr buffer, int length, IntPtr user) {
        if(seekPending) ApplySeek();

        int produced = 0;
        length -= length % bytesPerFrame;

        while(produced < length) {
            int want = length - produced;

            // Never read past the loop end in one pull: the wrap has to land exactly on the
            // boundary for the loop to be sample-accurate.
            long loopEndFrame = LoopEndFrame();
            if(loopEndFrame > 0) {
                long bytesLeft = (loopEndFrame - sourceFrame) * bytesPerFrame;
                if(bytesLeft < want) want = (int)bytesLeft;
            }

            want -= want % bytesPerFrame;
            if(want <= 0) break;

            int got = Bass.BASS_ChannelGetData(source, IntPtr.Add(buffer, produced), want);
            if(got <= 0) {
                // Only a genuine BASS_ERROR_ENDED means the track is over. The master stream is
                // opened with BASS_ASYNCFILE, so a short read just means the file buffer has not
                // caught up yet - returning less than asked for is allowed, and treating it as the
                // end would stop playback at random points.
                if(got < 0 && Bass.BASS_ErrorGetCode() == BASSError.BASS_ERROR_ENDED) ended = true;
                break;
            }

            produced += got;
            sourceFrame += got / bytesPerFrame;
            outputFrames += got / bytesPerFrame;

            if(loopEndFrame > 0 && sourceFrame >= loopEndFrame) WrapToLoopStart();

            if(got < want) break;                        // source had less than asked for
        }

        if(ended) return produced | unchecked((int)0x80000000);  // BASS_STREAMPROC_END
        return produced;
    }

    // The boundary this pass through the loop will wrap at, which is not necessarily the loop
    // length currently configured. Resizing a loop while the playhead is already past the new end
    // must not cut the pass short: playback runs to the armed end, wraps, and only then adopts
    // the new length. This mirrors the BASS_SYNC_POS it replaced, which sat at a fixed byte
    // position until it was deliberately re-armed.
    private long armedEndFrame;

    // Arms the wrap boundary immediately. Called by the Player when a loop is started, or when a
    // resize should take effect at once rather than at the next wrap.
    public void ArmLoop(double endSeconds) {
        Volatile.Write(ref armedEndFrame, (long)(endSeconds * sampleRate));
    }

    // The frame the current pull must stop at, or 0 when the loop places no constraint.
    // Sitting at or past the armed end means the loop does not apply - matching the old sync,
    // which simply never fired if playback was already beyond the sync position. That is what
    // lets a seek out of a loop play on instead of being yanked back.
    private long LoopEndFrame() {
        Loop loop = Loop;
        if(loop == null || !loop.Enabled) return 0;

        long armed = Volatile.Read(ref armedEndFrame);
        if(armed <= 0) armed = (long)(loop.End * sampleRate);   // never armed: use as configured
        if(armed <= (long)(loop.Start * sampleRate)) return 0;

        return sourceFrame < armed ? armed : 0;
    }

    private void WrapToLoopStart() {
        SeekSource((long)(Loop.Start * sampleRate));

        // Adopt whatever length is configured now. A resize that arrived mid-pass takes effect
        // here, at the wrap, rather than truncating the pass it arrived during.
        Volatile.Write(ref armedEndFrame, (long)(Loop.End * sampleRate));

        PushSegment(outputFrames, sourceFrame);
    }

    private void ApplySeek() {
        long frame = Interlocked.Exchange(ref pendingSeekFrame, -1);
        if(frame < 0) {
            seekPending = false;
            return;
        }

        SeekSource(frame);
        ended = false;

        // The caller flushes the chain, which resets BASS's byte counter for this stream back to
        // zero, so the output-frame origin has to move with it.
        outputFrames = 0;
        Volatile.Write(ref segmentHead, 0);
        PushSegment(0, sourceFrame);

        // Cleared last: until the map has been rebuilt, readers are served the requested position
        // rather than a stale or half-built answer.
        seekPending = false;
    }

    private void SeekSource(long frame) {
        if(frame < 0) frame = 0;
        if(lengthFrames > 0 && frame > lengthFrames) frame = lengthFrames;

        Bass.BASS_ChannelSetPosition(source, frame * bytesPerFrame, BASSMode.BASS_POS_BYTE);
        sourceFrame = frame;
    }

    private void PushSegment(long outFrame, long srcFrame) {
        Volatile.Write(ref segmentVersion, Volatile.Read(ref segmentVersion) + 1);   // odd: writing
        long head = Volatile.Read(ref segmentHead);
        segments[head & SegmentMask] = new Segment { OutFrame = outFrame, SrcFrame = srcFrame };
        Volatile.Write(ref segmentHead, head + 1);
        Volatile.Write(ref segmentVersion, Volatile.Read(ref segmentVersion) + 1);   // even: stable
    }

    // ---------------------------------------------------------------- teardown

    public void Dispose() {
        int h = Interlocked.Exchange(ref stream, 0);
        if(h != 0) Bass.BASS_StreamFree(h);
    }
}
