using System.Threading;
using Un4seen.Bass;

namespace Diyokee;

// Owns playback position and playback speed for a Player.
//
// The engine is a user (STREAMPROC) stream sitting where the BASS_FX reverse stream used to be:
// master decode stream -> ScratchEngine -> gain -> tempo -> FX -> splitters -> device mixers.
//
// It keeps a fractional playhead into the source and a ring buffer of decoded source frames
// around it, so it can render the track at any speed in either direction. Velocity is signed:
// negative simply reads the ring backwards, which is why the reverse FX is gone.
//
// At a velocity of exactly 1.0 the playhead stays on integer frames and Catmull-Rom returns the
// sample untouched, so ordinary playback is still bit-identical to the original chain.
//
// Why the engine owns position: a STREAMPROC stream reports position as bytes emitted, which only
// ever increases - it cannot represent a loop wrap, a seek, or playing backwards. BASS's idea of
// "where are we" is therefore not usable as track position. See SourceSecondsAtOutputFrame.
public sealed class ScratchEngine : IDisposable {
    public const int DefaultSampleRate = 44100;

    // ~5.9s of source at 44.1kHz. Big enough that a scratch stays inside the ring and needs no
    // re-seek; small enough to stay cheap (2.1 MB per stereo deck).
    private const int RingFrames = 1 << 18;
    private const int RingMask = RingFrames - 1;
    private const int ChunkFrames = 4096;      // decoded per top-up
    private const int BackfillFrames = 16384;  // decoded when reaching back beyond the ring
    private const int InterpMargin = 2;        // frames Catmull-Rom needs either side

    // Band-limiting resampler, used above play speed.
    //
    // The kernel spans SincTapsPerSide source frames either side at 1x and stretches with the rate,
    // so an unbounded kernel costs taps in proportion to speed: measured at 7x realtime at 32x,
    // which would underrun on hardware as slow as a Pi.
    //
    // Bounding the STRETCH is the wrong cure - it leaves the cutoff behind the actual rate, and the
    // band that then escapes is bass, the one thing still audible in a fast scratch. Bounding the
    // TAP COUNT is right: the cutoff still tracks the rate exactly, and what degrades instead is
    // the width of the transition band, up where the record is a whoosh either way.
    //
    // MaxKernelTaps is where cost is traded against rejection at extreme speeds. It does not bind
    // below 8x, so the 2-8x range that ordinary scratching lives in is unaffected by it. Measured
    // by check 19 of tools/enginetest, on a desktop:
    //
    //     taps   1kHz at 30x     4x     8x    16x    32x
    //       64        -6.8dB    36x    35x    34x    33x   realtime
    //      128       -13.6dB    36x    18x    18x    17x
    //      256       -27.0dB    36x    18x     9x     9x   <- chosen
    //      512       -77.8dB    36x    18x     9x     5x
    //
    // 256 keeps the common range fast and leaves the 9x worst case only on brief flicks, which
    // BASS's 500ms output buffer absorbs. Lower it first if a Pi ever struggles.
    private const int SincTapsPerSide = 8;     // full quality, used up to MaxKernelTaps/(2*rate)
    private const int MaxKernelTaps = 256;
    private const double MaxScratchSpeed = 32.0;
    private const int KernelSteps = 512;       // prototype table resolution, per unit tap

    // Below this speed the record counts as stopped. A stopped record makes no sound, and the
    // playhead is moving so little that rendering would just repeat one source sample - a DC
    // offset rather than audio. The threshold has to be high enough that the fade below finishes
    // before that repetition is audible: at 1e-4 the engine held a single sample for ~10000 frames
    // before silencing, which is the DC step this was supposed to avoid.
    private const double SilenceThreshold = 0.02;

    // Gain snaps to exactly 0 or 1 within this much of either end. The upper snap is what keeps
    // unity playback bit-identical, since it lets the multiply be skipped entirely; the lower one
    // stops the one-pole's inaudible tail from rendering for another 20ms. -80dB either way.
    private const double GainSnap = 1e-4;

    // Maps a stretch of what we emit onto where it came from in the source.
    private struct Run {
        public long OutFrame;     // output frame this run starts at
        public double SrcFrame;   // source frame at that point
        public double Slope;      // source frames per output frame across the run
    }

    private const int RunCount = 128;          // power of two
    private const int RunMask = RunCount - 1;

    private readonly int source;               // master decode stream, owned by the caller
    private readonly int channels;
    private readonly int bytesPerFrame;
    private readonly int sampleRate;
    private readonly long lengthFrames;

    private readonly STREAMPROC proc;          // kept alive for as long as the stream exists
    private int stream;

    private readonly float[] ring;
    private readonly float[] decodeBuf;
    private readonly float[] outBuf;
    private readonly double[] tapWeights = new double[MaxKernelTaps + 4];
    private long ringTail, ringHead;           // source frames held: [ringTail, ringHead)

    private readonly Run[] runs = new Run[RunCount];
    private long runHead;
    private long runVersion;                   // seqlock: odd while being written

    private long outputFrames;                 // frames emitted since the last output reset
    private double playhead;                   // fractional source frame - the authority
    private double velocity = 1.0;             // current speed, after slewing
    private bool ended;
    private bool sourceExhausted;

    // Output level, faded in and out around SilenceThreshold. Cutting straight to zero was a step
    // at whatever amplitude the waveform happened to be at - the click at the start and end of
    // every stroke, and why scratching a paused deck sounded worse than scratching a playing one:
    // paused, every stroke both begins and ends at zero velocity.
    private double silenceGain = 1.0;
    private volatile bool fadeInRequested;

    private long pendingSeekFrame = -1;
    private double pendingSeekSeconds;
    private volatile bool seekPending;

    // Stopped is distinct from Idle: after braking to a halt the deck must stay halted. Idle
    // targets play speed, so without it the platter would spin straight back up in the gap before
    // the Player notices the brake completed and pauses the deck.
    private enum State { Idle, Touched, Releasing, Stopped }
    private volatile State state = State.Idle;
    private double gestureSpeed;               // requested speed while the platter is held
    private volatile bool brakeCompleted;

    public Loop Loop { get; set; } = new();

    public int StreamHandle => stream;
    public int BytesPerFrame => bytesPerFrame;
    public double LengthSeconds => lengthFrames / (double)sampleRate;

    // Speed the deck runs at when nothing is touching it. 1.0 is normal playback.
    public double PlayVelocity { get; set; } = 1.0;

    public TouchModes TouchMode { get; set; } = TouchModes.Vinyl;
    public ReleaseModes ReleaseMode { get; set; } = ReleaseModes.Inertia;
    public double SpinUpTime { get; set; } = 0.25;
    public double BrakeTime { get; set; } = 0.12;
    public double SlewTime { get; set; } = 0.004;

    // How long the output takes to fade in or out as the record passes SilenceThreshold.
    public double SilenceFadeTime { get; set; } = 0.005;

    public bool IsTouched => state == State.Touched;
    public double Velocity => Volatile.Read(ref velocity);

    public ScratchEngine(int sourceHandle, int sampleRate, int channels, Loop loop) {
        source = sourceHandle;
        this.sampleRate = sampleRate <= 0 ? DefaultSampleRate : sampleRate;
        this.channels = Math.Max(1, channels);
        bytesPerFrame = this.channels * sizeof(float);
        Loop = loop;

        ring = new float[RingFrames * this.channels];
        decodeBuf = new float[ChunkFrames * this.channels];
        outBuf = new float[ChunkFrames * this.channels];

        long lengthBytes = Bass.BASS_ChannelGetLength(sourceHandle, BASSMode.BASS_POS_BYTE);
        lengthFrames = lengthBytes > 0 ? lengthBytes / bytesPerFrame : 0;

        long pos = Bass.BASS_ChannelGetPosition(sourceHandle, BASSMode.BASS_POS_BYTE);
        playhead = pos > 0 ? pos / bytesPerFrame : 0;
        ringTail = ringHead = (long)playhead;

        PushRun(0, playhead, 1.0);

        proc = StreamProc;
        stream = Bass.BASS_StreamCreate(this.sampleRate, this.channels,
                                        BASSFlag.BASS_SAMPLE_FLOAT | BASSFlag.BASS_STREAM_DECODE,
                                        proc, IntPtr.Zero);
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

    // The platter has been grabbed. Playback speed is handed over to SetGestureSpeed.
    public void Touch() {
        Volatile.Write(ref gestureSpeed, 0);
        brakeCompleted = false;
        state = State.Touched;
    }

    // Speed the hand is asking for, as a multiple of normal playback: 1.0 is play speed, -2.0 is
    // twice as fast backwards, 0 is held still.
    public void SetGestureSpeed(double speed) {
        if(double.IsNaN(speed) || double.IsInfinity(speed)) speed = 0;

        // Clamped here rather than in the render loop so the release state machine still sees a
        // target it can actually reach. Beyond this the record is a whoosh regardless, and the
        // limit is what keeps the resampler's per-frame cost finite.
        if(speed > MaxScratchSpeed) speed = MaxScratchSpeed;
        else if(speed < -MaxScratchSpeed) speed = -MaxScratchSpeed;

        Volatile.Write(ref gestureSpeed, speed);
    }

    // The platter has been let go. What happens next is up to ReleaseMode.
    public void Release() {
        if(state == State.Touched) state = State.Releasing;
    }

    // Lifts the halt left behind by a braked release, so the deck runs again. Called when
    // playback is (re)started.
    public void Resume() {
        if(state == State.Stopped) state = State.Idle;
    }

    // Asks for the next audio to fade up from silence rather than starting at full level. Used
    // when a paused deck is un-paused to be scratched: its mixer channels carry
    // BASS_MIXER_CHAN_NORAMPIN, so nothing downstream will ramp it in for us and the first sample
    // would step straight from silence to wherever the waveform happened to be.
    public void FadeInFromSilence() {
        fadeInRequested = true;
    }

    // True once after a braked release has actually reached a standstill, so the Player can stop
    // the deck. Reading it clears it.
    public bool ConsumeBrakeCompleted() {
        if(!brakeCompleted) return false;
        brakeCompleted = false;
        return true;
    }

    // Track position that is currently being heard, given the mixer's report of how far into our
    // emitted stream playback has reached. BASS_Mixer_ChannelGetPosition already compensates for
    // downstream buffering, so this stays honest without the engine modelling latency itself.
    public double SourceSecondsAtOutputFrame(long outFrame) {
        if(seekPending) return pendingSeekSeconds;

        for(int attempt = 0; attempt < 8; attempt++) {
            long before = Volatile.Read(ref runVersion);
            if((before & 1) != 0) continue;                  // a write is in progress

            long head = Volatile.Read(ref runHead);
            double best = -1;
            long start = Math.Max(0, head - RunCount);
            for(long i = head - 1; i >= start; i--) {
                Run r = runs[i & RunMask];
                if(r.OutFrame <= outFrame) {
                    best = r.SrcFrame + (outFrame - r.OutFrame) * r.Slope;
                    break;
                }
            }

            if(Volatile.Read(ref runVersion) == before) {
                if(best < 0) return 0;
                if(lengthFrames > 0 && best > lengthFrames) best = lengthFrames;
                return best / sampleRate;
            }
        }

        return Volatile.Read(ref playhead) / sampleRate;
    }

    // The playhead as the engine sees it - ahead of what is being heard by the pipeline's
    // buffering. Used only where the decode position is genuinely what is wanted.
    public double DecodeSeconds => Volatile.Read(ref playhead) / sampleRate;

    // ---------------------------------------------------------------- loop

    // The boundary this pass through the loop will wrap at, which is not necessarily the loop
    // length currently configured. Resizing a loop while the playhead is already past the new end
    // must not cut the pass short: playback runs to the armed end, wraps, and only then adopts the
    // new length. This mirrors the BASS_SYNC_POS it replaced, which sat at a fixed byte position
    // until it was deliberately re-armed.
    private long armedEndFrame;

    // Arms the wrap boundary immediately. Called by the Player when a loop is started, or when a
    // resize should take effect at once rather than at the next wrap.
    public void ArmLoop(double endSeconds) {
        Volatile.Write(ref armedEndFrame, (long)(endSeconds * sampleRate));
    }

    private bool LoopBounds(out double startFrame, out double endFrame) {
        startFrame = 0;
        endFrame = 0;

        Loop loop = Loop;
        if(loop == null || !loop.Enabled) return false;

        long armed = Volatile.Read(ref armedEndFrame);
        if(armed <= 0) armed = (long)(loop.End * sampleRate);

        startFrame = loop.Start * sampleRate;
        endFrame = armed;
        return endFrame > startFrame;
    }

    // ---------------------------------------------------------------- audio thread

    private int StreamProc(int handle, IntPtr buffer, int length, IntPtr user) {
        if(seekPending) ApplySeek();
        if(fadeInRequested) {
            fadeInRequested = false;
            silenceGain = 0.0;
        }

        int framesWanted = length / bytesPerFrame;
        if(framesWanted > outBuf.Length / channels) framesWanted = outBuf.Length / channels;
        if(framesWanted <= 0) return 0;

        double target = TargetVelocity();
        double alpha = SlewAlpha();
        double fadeAlpha = FadeAlpha();

        long outCursor = outputFrames;
        long runOutStart = outCursor;
        double runSrcStart = playhead;

        bool haveLoop = LoopBounds(out double loopStart, out double loopEnd);
        double loopLength = loopEnd - loopStart;

        int produced = 0;
        while(produced < framesWanted) {
            velocity += (target - velocity) * alpha;

            // Fade across SilenceThreshold rather than gating on it: the jump in and out of
            // silence was the click, not the silence itself.
            double gainTarget = Math.Abs(velocity) < SilenceThreshold ? 0.0 : 1.0;
            silenceGain += (gainTarget - silenceGain) * fadeAlpha;
            if(silenceGain > 1.0 - GainSnap) silenceGain = 1.0;
            else if(silenceGain < GainSnap) silenceGain = 0.0;

            double previous = playhead;

            if(silenceGain == 0.0) {
                int at = produced * channels;
                for(int c = 0; c < channels; c++) outBuf[at + c] = 0f;
            } else if(!RenderFrame(produced)) {
                // Only a genuinely exhausted source ends the stream. Anything else is a short read
                // that has not caught up yet, and returning less than asked for is allowed.
                if(sourceExhausted) ended = true;
                break;
            } else if(silenceGain != 1.0) {
                // Skipped entirely at unity, which is what keeps normal playback bit-identical.
                int at = produced * channels;
                for(int c = 0; c < channels; c++) outBuf[at + c] = (float)(outBuf[at + c] * silenceGain);
            }

            produced++;
            outCursor++;
            playhead += velocity;
            double unwrapped = playhead;

            // Direction-aware wrap, triggered by the crossing rather than by the position, so that
            // seeking outside an active loop plays on instead of being yanked back into it.
            if(haveLoop) {
                bool wrapped = false;
                if(velocity > 0 && previous < loopEnd && playhead >= loopEnd) {
                    playhead -= loopLength;
                    wrapped = true;
                } else if(velocity < 0 && previous >= loopStart && playhead < loopStart) {
                    playhead += loopLength;
                    wrapped = true;
                }

                if(wrapped) {
                    long span = outCursor - runOutStart;
                    if(span > 0) PushRun(runOutStart, runSrcStart, (unwrapped - runSrcStart) / span);
                    runOutStart = outCursor;
                    runSrcStart = playhead;

                    // Adopt whatever length is configured now: a resize that arrived mid-pass
                    // takes effect here, at the wrap, rather than truncating the pass.
                    Volatile.Write(ref armedEndFrame, (long)(Loop.End * sampleRate));
                    haveLoop = LoopBounds(out loopStart, out loopEnd);
                    loopLength = loopEnd - loopStart;
                }
            }

            if(playhead < 0) {
                playhead = 0;
                velocity = 0;
            }
            if(lengthFrames > 0 && playhead >= lengthFrames) {
                playhead = lengthFrames;
                if(velocity > 0) {
                    ended = true;
                    break;
                }
            }
        }

        long finalSpan = outCursor - runOutStart;
        if(finalSpan > 0) PushRun(runOutStart, runSrcStart, (playhead - runSrcStart) / finalSpan);
        outputFrames = outCursor;

        if(state == State.Releasing && Math.Abs(velocity - target) < 1e-3) {
            velocity = target;
            if(ReleaseMode == ReleaseModes.Stop) {
                brakeCompleted = true;
                state = State.Stopped;
            } else {
                state = State.Idle;
            }
        }

        if(produced > 0) System.Runtime.InteropServices.Marshal.Copy(outBuf, 0, buffer, produced * channels);

        int bytes = produced * bytesPerFrame;
        if(ended) return bytes | unchecked((int)0x80000000);   // BASS_STREAMPROC_END
        return bytes;
    }

    private double TargetVelocity() {
        switch(state) {
            case State.Touched:
                double gesture = Volatile.Read(ref gestureSpeed);
                return TouchMode == TouchModes.Vinyl ? gesture : PlayVelocity + gesture;
            case State.Releasing:
                return ReleaseMode == ReleaseModes.Stop ? 0.0 : PlayVelocity;
            case State.Stopped:
                return 0.0;
            default:
                return PlayVelocity;
        }
    }

    private double SlewAlpha() {
        double tau = state switch {
            State.Releasing => ReleaseMode == ReleaseModes.Stop ? BrakeTime : SpinUpTime,
            _ => SlewTime
        };
        if(tau <= 0) return 1.0;                              // no smoothing at all
        return 1.0 - Math.Exp(-1.0 / (tau * sampleRate));
    }

    private double FadeAlpha() {
        if(SilenceFadeTime <= 0) return 1.0;                  // cut instead of fade
        return 1.0 - Math.Exp(-1.0 / (SilenceFadeTime * sampleRate));
    }

    // Writes one output frame. Returns false if the source could not supply the data.
    //
    // Below play speed the playhead moves less than one source frame per output frame, so there is
    // nothing to band-limit and plain interpolation is both correct and cheap. Above it the engine
    // is decimating, and point interpolation folds everything past Nyquist/rate back into the
    // audible band - see RenderBandLimited.
    private bool RenderFrame(int outIndex) {
        double rate = Math.Abs(velocity);

        // No cap on the stretch: the cutoff must follow the rate however fast it gets. The tap
        // budget inside RenderBandLimited is what bounds the cost.
        if(rate > 1.0) return RenderBandLimited(outIndex, rate);

        long i = (long)Math.Floor(playhead);
        double t = playhead - i;

        if(!Cover(i - 1, i + InterpMargin)) return false;

        int at = outIndex * channels;
        for(int c = 0; c < channels; c++) {
            float p0 = Sample(i - 1, c);
            float p1 = Sample(i, c);
            float p2 = Sample(i + 1, c);
            float p3 = Sample(i + 2, c);

            // Catmull-Rom. At t == 0 this collapses to exactly p1, which is what keeps ordinary
            // playback bit-identical to reading the source directly.
            double a = p1;
            double b = 0.5 * (p2 - p0);
            double cc = p0 - 2.5 * p1 + 2.0 * p2 - 0.5 * p3;
            double d = -0.5 * p0 + 1.5 * p1 - 1.5 * p2 + 0.5 * p3;

            outBuf[at + c] = (float)(((d * t + cc) * t + b) * t + a);
        }
        return true;
    }

    // Resamples with a sinc kernel stretched by the playback rate, which low-passes and
    // interpolates in one pass: stretching the kernel by "rate" moves its cutoff down to
    // Nyquist/rate, which is exactly the content that would otherwise fold back.
    //
    // Cost is proportional to rate, which is why the stretch is capped - a 30x flick is a whoosh
    // either way, and the cap keeps the worst case bounded on weak hardware. Weights depend only
    // on the tap offsets, so they are computed once and reused across channels.
    private bool RenderBandLimited(int outIndex, double stretch) {
        // Cutoff always follows the rate; only the window narrows once the tap budget is reached.
        double half = Math.Min(SincTapsPerSide * stretch, MaxKernelTaps / 2.0);
        double invWindow = stretch / half;             // maps a tap offset onto the window's [-1, 1]

        long first = (long)Math.Ceiling(playhead - half);
        long last = (long)Math.Floor(playhead + half);
        if(first < 0) first = 0;

        int taps = (int)(last - first + 1);
        if(taps <= 0 || taps > tapWeights.Length) return false;
        if(!Cover(first, last)) return false;

        double weightSum = 0;
        for(int k = 0; k < taps; k++) {
            double w = KernelAt((first + k - playhead) / stretch, invWindow);
            tapWeights[k] = w;
            weightSum += w;
        }
        if(Math.Abs(weightSum) < 1e-12) return false;

        // Normalising by the actual sum rather than by "stretch" keeps the gain at exactly unity
        // even where the kernel is truncated at the start of the track.
        double norm = 1.0 / weightSum;
        int at = outIndex * channels;
        for(int c = 0; c < channels; c++) {
            double sum = 0;
            for(int k = 0; k < taps; k++) sum += tapWeights[k] * Sample(first + k, c);
            outBuf[at + c] = (float)(sum * norm);
        }
        return true;
    }

    // Sinc and window are tabulated separately because the tap budget makes the window narrower
    // than the sinc at high rates - a single combined prototype would tie the two together and
    // force the cutoff to move with the budget, which is the mistake this design avoids.
    // Both are symmetric, so only the positive half is stored. Built once, shared by every deck.
    private static readonly float[] sincTable = BuildSincTable();
    private static readonly float[] windowTable = BuildWindowTable();

    private static float[] BuildSincTable() {
        float[] table = new float[SincTapsPerSide * KernelSteps + 2];
        for(int i = 0; i < table.Length; i++) {
            double u = i / (double)KernelSteps;
            table[i] = (float)(u < 1e-9 ? 1.0 : Math.Sin(Math.PI * u) / (Math.PI * u));
        }
        return table;
    }

    private static float[] BuildWindowTable() {
        float[] table = new float[KernelSteps + 2];
        for(int i = 0; i < table.Length; i++) {
            double v = Math.Min(1.0, i / (double)KernelSteps);    // 0 at the centre, 1 at the edge
            table[i] = (float)(0.42 + 0.5 * Math.Cos(Math.PI * v) + 0.08 * Math.Cos(2.0 * Math.PI * v));
        }
        return table;
    }

    // u is the tap's distance from the playhead in stretched units, so sinc(u) sets the cutoff.
    // invWindow scales that same distance onto the window's half width.
    private static double KernelAt(double u, double invWindow) {
        u = Math.Abs(u);
        double v = u * invWindow;
        if(v >= 1.0 || u >= SincTapsPerSide) return 0;

        double x = u * KernelSteps;
        int i = (int)x;
        double sinc = sincTable[i] + (sincTable[i + 1] - sincTable[i]) * (x - i);

        double y = v * KernelSteps;
        int j = (int)y;
        double window = windowTable[j] + (windowTable[j + 1] - windowTable[j]) * (y - j);

        return sinc * window;
    }

    private float Sample(long frame, int channel) {
        if(frame < 0) frame = 0;
        return ring[((frame & RingMask) * channels) + channel];
    }

    // Makes sure [first, last] is present in the ring, decoding or re-seeking as needed.
    private bool Cover(long first, long last) {
        if(first < 0) first = 0;
        if(first >= ringTail && last < ringHead) return true;

        if(first < ringTail || first > ringHead) {
            // Discontinuous. Reaching backwards past what the ring holds is the expensive case,
            // so pull back a block's worth of history in one go rather than re-seeking per frame.
            long windowStart = first < ringTail ? first - BackfillFrames : first;
            if(windowStart < 0) windowStart = 0;
            RepositionSource(windowStart);
        }

        while(ringHead <= last) {
            if(!DecodeChunk()) return false;
        }
        return true;
    }

    private void RepositionSource(long frame) {
        Bass.BASS_ChannelSetPosition(source, frame * bytesPerFrame, BASSMode.BASS_POS_BYTE);
        ringTail = ringHead = frame;
        sourceExhausted = false;
    }

    private bool DecodeChunk() {
        if(sourceExhausted) return false;

        int wantFrames = Math.Min(ChunkFrames, decodeBuf.Length / channels);
        int bytes = Bass.BASS_ChannelGetData(source, decodeBuf, wantFrames * bytesPerFrame);
        if(bytes <= 0) {
            // Only a genuine BASS_ERROR_ENDED means the track is over. The master stream is opened
            // with BASS_ASYNCFILE, so a short read just means the file buffer has not caught up.
            if(bytes < 0 && Bass.BASS_ErrorGetCode() == BASSError.BASS_ERROR_ENDED) sourceExhausted = true;
            return false;
        }

        int got = bytes / bytesPerFrame;
        int at = (int)(ringHead & RingMask);
        int firstPart = Math.Min(got, RingFrames - at);

        Array.Copy(decodeBuf, 0, ring, at * channels, firstPart * channels);
        if(got > firstPart) Array.Copy(decodeBuf, firstPart * channels, ring, 0, (got - firstPart) * channels);

        ringHead += got;
        if(ringHead - ringTail > RingFrames) ringTail = ringHead - RingFrames;
        return true;
    }

    private void ApplySeek() {
        long frame = Interlocked.Exchange(ref pendingSeekFrame, -1);
        if(frame < 0) {
            seekPending = false;
            return;
        }

        if(lengthFrames > 0 && frame > lengthFrames) frame = lengthFrames;
        if(frame < 0) frame = 0;

        RepositionSource(frame);
        playhead = frame;
        velocity = TargetVelocity();
        ended = false;

        // The caller flushes the chain, which resets BASS's byte counter for this stream back to
        // zero, so the output-frame origin moves with it.
        outputFrames = 0;
        Volatile.Write(ref runHead, 0);
        PushRun(0, playhead, velocity);

        // Cleared last: until the map has been rebuilt, readers are served the requested position
        // rather than a stale or half-built answer.
        seekPending = false;
    }

    private void PushRun(long outFrame, double srcFrame, double slope) {
        Volatile.Write(ref runVersion, Volatile.Read(ref runVersion) + 1);   // odd: writing
        long head = Volatile.Read(ref runHead);
        runs[head & RunMask] = new Run { OutFrame = outFrame, SrcFrame = srcFrame, Slope = slope };
        Volatile.Write(ref runHead, head + 1);
        Volatile.Write(ref runVersion, Volatile.Read(ref runVersion) + 1);   // even: stable
    }

    // ---------------------------------------------------------------- teardown

    public void Dispose() {
        int h = Interlocked.Exchange(ref stream, 0);
        if(h != 0) Bass.BASS_StreamFree(h);
    }
}
