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
// While the platter is held, velocity is not commanded - it is whatever it takes to close the gap
// between the playhead and where the hand says the record should be. See the servo fields below;
// that indirection is what stops the input's quantisation and burstiness reaching the audio.
//
// Two resampling paths, which is deliberate rather than leftover. At or below play speed there is
// nothing to band-limit, so Catmull-Rom interpolates: cheap, and at exactly 1.0 the playhead stays
// on integer frames and it returns the sample untouched, keeping ordinary playback bit-identical
// to the original chain. Above play speed the engine is decimating, so it switches to a sinc
// kernel stretched by the rate, which low-passes and interpolates in one pass. Using the sinc for
// both was tried: it costs twice as much at 1x and loses the bit-identity, because sin(pi*k) is
// not exactly zero in floating point.
//
// Why the engine owns position: a STREAMPROC stream reports position as bytes emitted, which only
// ever increases - it cannot represent a loop wrap, a seek, or playing backwards. BASS's idea of
// "where are we" is therefore not usable as track position. See SourceSecondsAtOutputFrame.
public sealed class ScratchEngine : IDisposable {
    // ~23.8s of source at 44.1kHz, 8.4 MB per stereo deck. Sized by the longest LOOP worth keeping
    // resident rather than by the longest scratch: a loop wrap jumps the playhead to the far end of
    // the loop, and the only way to make that free is to hold the whole loop. At 5.9s it did not
    // even cover 16 beats at 120 BPM.
    private const int RingFrames = 1 << 20;
    private const int RingMask = RingFrames - 1;
    private const int ChunkFrames = 4096;      // decoded per top-up
    private const int BackfillFrames = 16384;  // decoded when reaching back beyond the ring
    private const int InterpMargin = 2;        // frames Catmull-Rom needs either side
    private const int EdgeMargin = MaxKernelTaps;   // pre-roll kept behind a reposition, so the
                                                    // first frame after a seek has its window

    // The ring is filled by a producer thread; the STREAMPROC only ever reads it. Seeking and
    // decoding cost hundreds of microseconds on a local file and hundreds of MILLISECONDS over
    // dropbox://, and the thread that used to pay that is BASS's update thread, which serves every
    // deck on the device - so one stalled track silenced both. Plan section 7, risk 3.
    //
    // The two windows never add up to the whole ring, which is what makes the reader safe without
    // locking: the tail is only ever dragged up to HistoryFrames behind the playhead, so the
    // producer cannot overwrite what is being played even if it is working from a stale reading
    // of where the playhead is.
    // Frame counts, calibrated at 44.1kHz. Lookahead has to exceed what one callback can consume
    // at MaxScratchSpeed (ChunkFrames * 32 = 131072) or a single callback could outrun the ring.
    private const int LookaheadFrames = 176400;            // 4s, kept ahead in the direction of travel
    private const int HistoryFrames = 44100;               // 1s, kept behind it
    private const int ScratchWindow = 132300;              // 3s of both, once a hand is on it
    private const int MaxResident = RingFrames - ChunkFrames * 4;   // most the ring can safely hold
    private const int PrefillChunks = 2;                   // decoded inline when a track is loaded
    private const int ProducerWaitMs = 2;                  // one wait; the budget is a few of these
    private const int ProducerWaitTries = 64;              // ...but only while it is making progress
    private const int ProducerQuietWaits = 3;              // consecutive silent waits that mean stalled
    private const int ProducerIdleMs = 250;                // re-check even if nobody asked

    // Band-limiting resampler, used above play speed - see docs/scratch-audio-quality.md.
    //
    // The kernel spans SincTapsPerSide source frames either side at 1x and stretches with the rate,
    // so an unbounded kernel costs taps in proportion to speed. Cost is bounded by capping the TAP
    // COUNT rather than the stretch: capping the stretch would leave the cutoff behind the actual
    // rate, and the band that then escapes is bass, the one thing still audible in a fast scratch.
    // Capping taps instead keeps the cutoff exact and widens the transition band instead.
    //
    // MaxKernelTaps does not bind below 8x, so ordinary scratching is unaffected by it. Measured by
    // check 19 of tools/enginetest, on a desktop:
    //
    //     taps   1kHz at 30x     4x     8x    16x    32x
    //       64        -6.8dB    36x    35x    34x    33x   realtime
    //      128       -13.6dB    36x    18x    18x    17x
    //      256       -27.0dB    36x    18x     9x     9x   <- chosen
    //      512       -77.8dB    36x    18x     9x     5x
    //
    // Lower it first if a Pi ever struggles.
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
    private volatile bool sourceExhausted;
    private long sourceNextFrame;              // frame the source will decode next

    // Everything that touches the source handle or writes the ring runs under this: the producer
    // thread, and the control thread when it prefills a seek. The audio thread never takes it.
    private readonly object sourceLock = new();
    private readonly Thread producer;
    private readonly AutoResetEvent wake = new(false);
    private readonly ManualResetEventSlim filled = new(false);
    private volatile bool disposed;

    private long readTail, readHead;           // audio thread only: the window this frame may read

    // Counters for tools/enginetest. Source I/O now belongs to the producer thread, and
    // AudioThreadSourceOps is the standing proof of it: anything but zero means a seek or a decode
    // happened inside the STREAMPROC, which is what this whole arrangement exists to prevent.
    public long SourceSeeks { get; private set; }
    public long FramesDecoded { get; private set; }
    public long AudioThreadSourceOps { get; private set; }

    // Frames the STREAMPROC had to pad with silence because the ring had not reached them. Should
    // be zero: anything else means the producer is not keeping up.
    public long SilencePaddedFrames { get; private set; }

    private int renderingOn;                   // thread id currently inside StreamProc, 0 if none

    private void NoteSourceIo() {
        if(Volatile.Read(ref renderingOn) == Environment.CurrentManagedThreadId) AudioThreadSourceOps++;
    }

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

    // Position servo. The hand's POSITION is the input, not its speed: the engine steers the
    // playhead toward where the hand says the record should be, and whatever velocity that takes
    // is the velocity you hear.
    //
    // Differentiating the hand's position was the root of this whole class of artifact. One
    // waveform pixel is 12.5ms of source, the browser reports integer pixels, and events arrive
    // in bursts over the circuit, so any speed computed from them is quantised, noisy, and simply
    // wrong whenever delivery is uneven. A servo never differentiates: three events arriving at
    // once move the target three pixels, and the error term absorbs the timing.
    //
    // The price is that the record lags the hand by FollowTime, which is the same thing as the
    // smoothing. It is wall-clock lag and stays constant at any scratch speed.
    private double gestureOffsetSeconds;       // hand displacement since Touch(), control thread
    private double appliedOffsetFrames;        // how much of it the audio thread has folded in
    private double targetFrame;                // where the hand says the record should be
    private volatile bool servoActive;
    private volatile bool anchorPending;

    public Loop Loop { get; set; }

    public int StreamHandle => stream;
    public int BytesPerFrame => bytesPerFrame;
    public double LengthSeconds => lengthFrames / (double)sampleRate;

    // Speed the deck runs at when nothing is touching it. Not configurable: tempo and pitch are
    // handled downstream by the BASS_FX tempo stream, so the engine always plays at natural speed.
    private const double PlayVelocity = 1.0;

    public TouchModes TouchMode { get; set; } = TouchModes.Vinyl;
    public ReleaseModes ReleaseMode { get; set; } = ReleaseModes.Inertia;
    public double SpinUpTime { get; set; } = 0.25;
    public double BrakeTime { get; set; } = 0.12;
    // Smoothing on a speed that was handed to us rather than derived from a position. Only the
    // speed-commanded path uses it - while the servo is running the slew comes from FollowTime.
    public double SlewTime { get; set; } = 0.004;

    // How far behind the hand the record is allowed to sit while being scratched - the servo's
    // one tuning knob, and the only scratch-feel setting that now exists. Shorter tracks the hand
    // more tightly and passes more of the input's quantisation through as velocity ripple; longer
    // is smoother and further behind. It is wall-clock lag, the same at any scratch speed.
    public double FollowTime { get; set; } = 0.040;

    // Clamped at the point of use rather than trusted. The settings dialog exposes FollowTime as a
    // plain number, and zero is its one genuinely dangerous value: the gain becomes one whole
    // playback speed per frame of error and the derived slew becomes instant, which turns the loop
    // into a bang-bang controller slamming between the speed limits.
    private double Follow => Math.Clamp(FollowTime, 0.005, 0.5);

    // How long the output takes to fade in or out as the record passes SilenceThreshold.
    public double SilenceFadeTime { get; set; } = 0.005;

    public bool IsTouched => state == State.Touched;
    public double Velocity => Volatile.Read(ref velocity);

    public ScratchEngine(int sourceHandle, int sampleRate, int channels, Loop loop) {
        source = sourceHandle;
        this.sampleRate = sampleRate <= 0 ? 44100 : sampleRate;
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
        ringTail = ringHead = sourceNextFrame = (long)playhead;

        PushRun(0, playhead, 1.0);

        proc = StreamProc;
        stream = Bass.BASS_StreamCreate(this.sampleRate, this.channels,
                                        BASSFlag.BASS_SAMPLE_FLOAT | BASSFlag.BASS_STREAM_DECODE,
                                        proc, IntPtr.Zero);

        // A couple of chunks inline so the very first callback has something to play; the producer
        // takes over from there.
        lock(sourceLock) {
            for(int i = 0; i < PrefillChunks && DecodeChunk(); i++) { }
        }

        producer = new Thread(ProducerLoop) { IsBackground = true, Name = "scratch-producer" };
        producer.Start();
    }

    // ---------------------------------------------------------------- control side

    // Asks for the playhead to move. The caller must then flush the chain (see
    // Player.SeekSeconds) so the new position is heard immediately rather than after the
    // buffered audio drains.
    public void RequestSeek(double seconds) {
        if(seconds < 0) seconds = 0;
        double max = LengthSeconds;
        if(max > 0 && seconds > max) seconds = max;

        // Deliberately does no I/O and takes no lock. This is called from the UI thread, from the
        // 30ms scrub loop, and from the 1ms loop in Player.Play that waits for the other deck's
        // beat before seeking and starting - so anything that can block here lands squarely on the
        // paths where latency shows.
        //
        // Repositioning the ring here was tried, on the reasoning that a seek should be audible at
        // once, and it was exactly that mistake: the producer re-acquires sourceLock once per
        // chunk, so a caller waiting on it can be starved for as long as a whole window takes to
        // decode. Nothing here needs to be synchronous - FillStep aims at pendingSeekFrame while a
        // seek is outstanding, so the producer retargets within one chunk, and the audio thread
        // waits for it rather than stalling.
        pendingSeekSeconds = seconds;
        Interlocked.Exchange(ref pendingSeekFrame, (long)(seconds * sampleRate));
        seekPending = true;
        wake.Set();
    }

    // The platter has been grabbed. From here the hand decides where the record is, through
    // SetGestureTarget - or, for a caller that knows a speed, through SetGestureSpeed.
    public void Touch() {
        Volatile.Write(ref gestureSpeed, 0);
        Volatile.Write(ref gestureOffsetSeconds, 0);
        servoActive = false;
        anchorPending = true;                  // the audio thread pins the target to the playhead
        brakeCompleted = false;
        state = State.Touched;
        wake.Set();                            // history is only worth fetching once we might reverse
    }

    // Speed the hand is asking for, as a multiple of normal playback: 1.0 is play speed, -2.0 is
    // twice as fast backwards, 0 is held still.
    public void SetGestureSpeed(double speed) {
        if(double.IsNaN(speed) || double.IsInfinity(speed)) speed = 0;

        // A sanity bound, not a cost bound - MaxKernelTaps handles cost. This only stops a wild
        // delta from asking for a speed no hand could produce. Clamped here rather than in the
        // render loop so the release state machine still sees a target it can reach.
        if(speed > MaxScratchSpeed) speed = MaxScratchSpeed;
        else if(speed < -MaxScratchSpeed) speed = -MaxScratchSpeed;

        Volatile.Write(ref gestureSpeed, speed);
    }

    // Where the hand has taken the record, as a signed displacement in source seconds since the
    // platter was grabbed. This is what the Player feeds; SetGestureSpeed above stays for callers
    // that genuinely know a speed, which is how the test harness drives exact rates.
    public void SetGestureTarget(double offsetSeconds) {
        if(double.IsNaN(offsetSeconds) || double.IsInfinity(offsetSeconds)) return;

        Volatile.Write(ref gestureOffsetSeconds, offsetSeconds);
        servoActive = true;
    }

    // The platter has been let go. What happens next is up to ReleaseMode.
    public void Release() {
        if(state == State.Touched) state = State.Releasing;
    }

    // Lifts the halt left behind by a braked release, so the deck runs again. Called when
    // playback is (re)started.
    //
    // It also CANCELS a brake still in progress, and drops any completion the Player has not
    // collected yet. Both matter because the brake finishes on the audio thread while the Player
    // notices it up to 30ms later, in MonitorPlayback - so pressing play in that window used to
    // start the deck and then have the late completion pause it again a moment afterwards. The
    // deck crept forward and stopped, seemingly on its own.
    public void Resume() {
        if(state == State.Stopped || (state == State.Releasing && ReleaseMode == ReleaseModes.Stop)) {
            state = State.Idle;
        }
        brakeCompleted = false;
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
        Volatile.Write(ref renderingOn, Environment.CurrentManagedThreadId);
        try {
            return RenderBlock(buffer, length);
        } finally {
            Volatile.Write(ref renderingOn, 0);
        }
    }

    private int RenderBlock(IntPtr buffer, int length) {
        if(seekPending) ApplySeek();
        if(fadeInRequested) {
            fadeInRequested = false;
            silenceGain = 0.0;
        }

        int framesWanted = length / bytesPerFrame;
        if(framesWanted > outBuf.Length / channels) framesWanted = outBuf.Length / channels;
        if(framesWanted <= 0) return 0;

        double blockTarget = TargetVelocity();
        double alpha = SlewAlpha();
        double fadeAlpha = Alpha(SilenceFadeTime);

        // Servo input. Pinning the target happens here rather than in Touch() so it lands on the
        // playhead the audio thread actually has, and the block's worth of hand movement is
        // spread across the frames instead of stepping once per callback.
        if(anchorPending) {
            anchorPending = false;
            targetFrame = playhead;
            appliedOffsetFrames = 0;
        }

        bool servo = state == State.Touched && servoActive;
        double offsetStep = servo
            ? (Volatile.Read(ref gestureOffsetSeconds) * sampleRate - appliedOffsetFrames) / framesWanted
            : 0;
        double followGain = 1.0 / (Follow * sampleRate);

        long outCursor = outputFrames;
        long runOutStart = outCursor;
        double runSrcStart = playhead;

        bool haveLoop = LoopBounds(out double loopStart, out double loopEnd);
        double loopLength = loopEnd - loopStart;

        int produced = 0;
        while(produced < framesWanted) {
            double target = blockTarget;
            if(servo) {
                appliedOffsetFrames += offsetStep;
                targetFrame += offsetStep;

                // Bend mode: the record keeps turning under the hand, so the target advances on
                // its own and the hand only displaces it.
                if(TouchMode == TouchModes.Bend) targetFrame += PlayVelocity;

                if(targetFrame < 0) targetFrame = 0;
                else if(lengthFrames > 0 && targetFrame > lengthFrames) targetFrame = lengthFrames;

                target = (targetFrame - playhead) * followGain;
                if(target > MaxScratchSpeed) target = MaxScratchSpeed;
                else if(target < -MaxScratchSpeed) target = -MaxScratchSpeed;
            }

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
            } else if(!RenderOne(produced)) {
                // Only a genuinely exhausted source ends the stream. Anything else is the ring not
                // being filled this far yet, and returning less than asked for is allowed.
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
                    // The servo target has to wrap with the playhead, or the error term would
                    // immediately try to unwind the whole loop.
                    targetFrame += playhead - unwrapped;

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

        // Never hand back less than was asked for. A mixer source that gets a short read reports
        // BASS_ACTIVE_STALLED, and MonitorPlayback treats anything but PLAYING as the end of the
        // track: it stops the deck, rebuilds the chain and jumps to the end of the file. It also
        // stops MonitorBeats dead, which is what drives InBeat and therefore sync playback. A few
        // milliseconds of silence is an enormously smaller thing than any of that.
        //
        // The playhead stays where it is, so the padding is a zero-slope run and the position map
        // stays truthful across it.
        if(!ended && produced < framesWanted) {
            int pad = framesWanted - produced;
            Array.Clear(outBuf, produced * channels, pad * channels);
            PushRun(outCursor, playhead, 0);

            SilencePaddedFrames += pad;
            outCursor += pad;
            produced = framesWanted;
        }

        outputFrames = outCursor;

        if(state == State.Releasing && Math.Abs(velocity - blockTarget) < 1e-3) {
            velocity = blockTarget;
            if(ReleaseMode == ReleaseModes.Stop) {
                brakeCompleted = true;
                state = State.Stopped;
            } else {
                state = State.Idle;
            }
        }

        // Keep the producer ahead of us without poking it on every callback.
        long headroom = velocity >= 0 ? Volatile.Read(ref ringHead) - (long)playhead
                                      : (long)playhead - Volatile.Read(ref ringTail);
        if(headroom < LookaheadFrames / 2) wake.Set();

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

    // One-pole coefficient reaching ~63% of a step per tau seconds. Zero tau means no smoothing.
    private double Alpha(double tau) => tau <= 0 ? 1.0 : 1.0 - Math.Exp(-1.0 / (tau * sampleRate));

    // While the servo is driving, the slew is the second pole of the loop rather than an
    // independent smoother, so it is derived instead of configured. The loop is
    //
    //     v' = (e/FollowTime - v) / slew,   e' = hand - v
    //
    // which is a second-order system with damping ratio 0.5 * sqrt(FollowTime / slew). At a
    // quarter of FollowTime that is exactly 1: the fastest response that cannot overshoot, and
    // overshoot here would be the record audibly rocking past where the hand is.
    private double SlewAlpha() => Alpha(state switch {
        State.Releasing => ReleaseMode == ReleaseModes.Stop ? BrakeTime : SpinUpTime,
        State.Touched when servoActive => Follow / 4.0,
        _ => SlewTime
    });

    // One output frame, in descending order of preference: the frame as it should be; the frame
    // rendered from whatever part of its window the ring holds, which costs a little filtering;
    // or nothing, which costs a gap. The waiting in between is what usually avoids both.
    private bool RenderOne(int outIndex) {
        int quiet = 0;
        for(int attempt = 0; attempt < ProducerWaitTries; attempt++) {
            if(RenderFrame(outIndex, degrade: false)) return true;
            if(sourceExhausted) break;

            // A ring that has not moved is not proof of a stall - right after a seek the producer
            // may simply not have been scheduled yet. Only several quiet waits in a row mean it is
            // stuck on something, and only then is spending the rest of the budget pointless.
            if(WaitForData()) quiet = 0;
            else if(++quiet >= ProducerQuietWaits) break;
        }
        return RenderFrame(outIndex, degrade: true);
    }

    // The only place the audio thread waits at all - and it waits on a thread, not on a file. A
    // dropbox:// range request that used to stall BASS's update thread, and with it every deck on
    // the device, for hundreds of milliseconds now costs a few milliseconds and a slightly worse
    // frame.
    //
    // Returns whether it is worth looking again: the producer publishes every chunk as it lands,
    // so a ring that has not moved at all means it is stuck rather than merely behind, and the
    // rest of the budget would be spent for nothing.
    private bool WaitForData() {
        long head = Volatile.Read(ref ringHead), tail = Volatile.Read(ref ringTail);

        filled.Reset();
        wake.Set();
        filled.Wait(ProducerWaitMs);

        return Volatile.Read(ref ringHead) != head || Volatile.Read(ref ringTail) != tail;
    }

    // Writes one output frame. Returns false if the ring does not hold what it needs.
    //
    // Below play speed the playhead moves less than one source frame per output frame, so there is
    // nothing to band-limit and plain interpolation is both correct and cheap. Above it the engine
    // is decimating, and point interpolation folds everything past Nyquist/rate back into the
    // audible band - see RenderBandLimited.
    private bool RenderFrame(int outIndex, bool degrade) {
        // Whatever the producer has published, taken once so the whole frame is rendered from a
        // consistent view of the ring.
        readTail = Volatile.Read(ref ringTail);
        readHead = Volatile.Read(ref ringHead);

        // The playhead's own frame is the one thing no amount of degrading can substitute for.
        long i = (long)Math.Floor(playhead);
        if(i < readTail || i >= readHead) return false;

        double rate = Math.Abs(velocity);

        // No cap on the stretch: the cutoff must follow the rate however fast it gets. The tap
        // budget inside RenderBandLimited is what bounds the cost.
        if(rate > 1.0) return RenderBandLimited(outIndex, rate, degrade);

        double t = playhead - i;

        long i0 = i - 1, i2 = i + 1, i3 = i + InterpMargin;
        if(degrade) {
            // Out of patience: hold the ring's edge rather than emit nothing at all.
            i0 = Math.Clamp(i0, readTail, readHead - 1);
            i2 = Math.Clamp(i2, readTail, readHead - 1);
            i3 = Math.Clamp(i3, readTail, readHead - 1);
        } else if(Math.Max(i0, 0) < readTail || i3 >= readHead) {
            // The whole interpolation window has to be there, because holding an edge sample would
            // be a quietly wrong output frame where waiting costs nothing. Frame 0 is the
            // exception: nothing precedes the start of the track, and Sample() folds the negative
            // index onto it exactly as it always did.
            return false;
        }

        int at = outIndex * channels;
        for(int c = 0; c < channels; c++) {
            float p0 = Sample(i0, c);
            float p1 = Sample(i, c);
            float p2 = Sample(i2, c);
            float p3 = Sample(i3, c);

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
    private bool RenderBandLimited(int outIndex, double stretch, bool degrade) {
        // Cutoff always follows the rate; only the window narrows once the tap budget is reached.
        double half = Math.Min(SincTapsPerSide * stretch, MaxKernelTaps / 2.0);
        double invWindow = stretch / half;             // maps a tap offset onto the window's [-1, 1]

        long wantFirst = (long)Math.Ceiling(playhead - half);
        long wantLast = (long)Math.Floor(playhead + half);
        long first = Math.Max(wantFirst, readTail);
        long last = Math.Min(wantLast, readHead - 1);

        // A kernel clipped by the RING is a filter nobody asked for, so wait for the producer
        // rather than quietly applying it. Clipped by the start or the end of the TRACK is a
        // different thing: there is nothing to wait for, and the normalisation below holds it at
        // unity gain anyway. That case has always been allowed.
        if(!degrade && ((first > wantFirst && first > 0) ||
                        (last < wantLast && (lengthFrames <= 0 || last < lengthFrames - 1)))) return false;

        int taps = (int)(last - first + 1);
        if(taps <= 0 || taps > tapWeights.Length) return false;

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

    // Callers are responsible for staying inside [readTail, readHead) - both render paths clamp
    // their own window before they get here, which keeps the clamp out of the inner tap loop.
    private float Sample(long frame, int channel) {
        if(frame < 0) frame = 0;
        return ring[((frame & RingMask) * channels) + channel];
    }

    // ---------------------------------------------------------------- producer thread

    // Everything below runs off the audio thread, under sourceLock.
    private void ProducerLoop() {
        while(!disposed) {
            wake.WaitOne(ProducerIdleMs);
            // Yield between steps. The lock is released and re-taken per chunk, and .NET's monitor
            // is not fair: without this a producer working through a long window can keep winning
            // the re-acquire and starve Dispose behind it.
            while(!disposed && FillStep()) Thread.Yield();
        }
    }

    // One lock-hold's worth of filling; returns true if there is more to do.
    //
    // Taking the lock per STEP rather than around the whole window is deliberate. A seek prefills
    // on the caller's thread, and the beat-sync loop in Player.Play polls every 1ms and seeks the
    // instant the other deck hits a beat - so queueing that seek behind three seconds of decoding
    // put a multi-millisecond stall exactly where alignment is decided. One chunk is a few hundred
    // microseconds, and the whole window still fills in the same number of decodes.
    private bool FillStep() {
        lock(sourceLock) {
            if(disposed) return false;

            // While a seek is pending the playhead is still at the old position, so aim at where
            // the audio thread is about to jump to instead - otherwise this would undo the prefill
            // that RequestSeek just did.
            long pending = Volatile.Read(ref pendingSeekFrame);
            long here = seekPending && pending >= 0 ? pending : (long)Volatile.Read(ref playhead);

            // A window biased along the direction of travel is right for playback and wrong for a
            // scratch, where the direction reverses several times a second: each reversal would
            // throw away one side and re-fetch the other. While the platter is held it is
            // symmetric, so a stroke crossing back and forth costs nothing after the first pass.
            bool scratching = state != State.Idle;
            bool forward = Volatile.Read(ref velocity) >= 0;
            long ahead = scratching ? ScratchWindow : forward ? LookaheadFrames : HistoryFrames;
            long behind = scratching ? ScratchWindow : forward ? HistoryFrames : LookaheadFrames;

            long wantTail = Math.Max(0, here - behind);
            long wantHead = here + ahead;

            // A loop wrap teleports the playhead to the far end of the loop - which a window that
            // follows the playhead has, by definition, just let go of. So while a loop is running,
            // pin the window to the LOOP rather than to the playhead: every wrap is then free, in
            // both directions, because both ends are already resident. A loop too long for the
            // ring falls back to the moving window and pays a refill once per pass.
            if(LoopBounds(out double loopStart, out double loopEnd)) {
                long ls = Math.Max(0, (long)loopStart - EdgeMargin);
                long le = (long)loopEnd + EdgeMargin;
                if(le - ls <= MaxResident && here >= ls && here <= le) {
                    wantTail = ls;
                    wantHead = le;
                }
            }

            if(lengthFrames > 0 && wantHead > lengthFrames) wantHead = lengthFrames;

            // Only reposition when decoding cannot reach the playhead: a big seek forward, or so
            // far back that the history will not fit alongside what is already decoded ahead.
            // Anything closer is covered below without discarding the ring.
            //
            // This used to test "outside [ringTail, ringHead]", which looks equivalent and is not:
            // RepositionSource starts the ring EdgeMargin BEHIND the target, so the playhead was
            // still past the head afterwards and the next pass repositioned again - a livelock
            // that decoded nothing at all. Every loop wrap fell into it.
            if(here > ringHead + ahead || ringHead - here > MaxResident) {
                RepositionSource(here);
                return true;
            }

            if(ringHead < wantHead && DecodeChunk()) return true;

            // Below the tail the playhead itself is missing, so this is not optional history and
            // runs whatever the state. Above it, history is only worth fetching while the platter
            // is held, since only a scratch running backwards ever reads it - pulling it in after
            // every seek would decode a second of audio per scrub tick that nothing looks at.
            if(here < ringTail) return Backfill(Math.Min(wantTail, here - EdgeMargin));

            if(wantTail > ringTail) {
                Volatile.Write(ref ringTail, wantTail);    // let go of history we have run past
                return false;
            }
            return scratching && here - ringTail < behind && Backfill(wantTail);
        }
    }

    // Pulls history in behind the tail, stopping at the old tail so that everything already
    // decoded AHEAD of the playhead survives. Keeping the forward half is the point: discarding it
    // left only BackfillFrames of history and nothing in front, so a scratch oscillating across
    // this boundary re-seeked and re-decoded on every pass.
    //
    // It never repositions on failure either. The forward half of the ring is being played right
    // now, and throwing that away to recover history nothing is even reading would turn a missing
    // feature into a dropout.
    private bool Backfill(long wantTail) {
        // One BackfillFrames step at a time, so the new history becomes visible as it lands rather
        // than only when the whole request is satisfied. A reader waiting on the tail to move gets
        // to carry on within a fraction of a millisecond instead of after the lot.
        long windowStart = Math.Max(wantTail, ringTail - BackfillFrames);
        if(windowStart < 0) windowStart = 0;
        if(ringHead - windowStart > RingFrames) windowStart = ringHead - RingFrames;
        if(windowStart >= ringTail) return false;

        long fillTo = ringTail;
        SeekSource(windowStart);

        while(sourceNextFrame < fillTo) {
            int want = (int)Math.Min(ChunkFrames, fillTo - sourceNextFrame);
            // A hole anywhere in the step leaves everything below the tail unusable, so publish
            // nothing and let the next pass try again.
            if(disposed || DecodeInto(sourceNextFrame, want) <= 0) return false;
        }

        Volatile.Write(ref ringTail, windowStart);
        filled.Set();
        return true;
    }

    private void SeekSource(long frame) {
        NoteSourceIo();
        Bass.BASS_ChannelSetPosition(source, frame * bytesPerFrame, BASSMode.BASS_POS_BYTE);
        sourceNextFrame = frame;
        sourceExhausted = false;
        SourceSeeks++;
    }

    // Starts the ring a little BEHIND the requested frame. Interpolation reads one frame back, so
    // a ring that begins exactly at the playhead cannot render its own first frame - which is the
    // first frame after every seek.
    private void RepositionSource(long frame) {
        long at = Math.Max(0, frame - EdgeMargin);
        SeekSource(at);
        Volatile.Write(ref ringTail, at);
        Volatile.Write(ref ringHead, at);
    }

    // Decodes at most maxFrames into the ring at an absolute frame position, leaving ringTail and
    // ringHead alone. Returns the number of frames written.
    private int DecodeInto(long frame, int maxFrames) {
        if(sourceExhausted) return 0;

        int wantFrames = Math.Min(maxFrames, decodeBuf.Length / channels);
        if(wantFrames <= 0) return 0;

        NoteSourceIo();
        int bytes = Bass.BASS_ChannelGetData(source, decodeBuf, wantFrames * bytesPerFrame);
        if(bytes <= 0) {
            // Only a genuine BASS_ERROR_ENDED means the track is over. The master stream is opened
            // with BASS_ASYNCFILE, so a short read just means the file buffer has not caught up.
            if(bytes < 0 && Bass.BASS_ErrorGetCode() == BASSError.BASS_ERROR_ENDED) sourceExhausted = true;
            return 0;
        }

        int got = bytes / bytesPerFrame;
        int at = (int)(frame & RingMask);
        int firstPart = Math.Min(got, RingFrames - at);

        Array.Copy(decodeBuf, 0, ring, at * channels, firstPart * channels);
        if(got > firstPart) Array.Copy(decodeBuf, firstPart * channels, ring, 0, (got - firstPart) * channels);

        sourceNextFrame = frame + got;
        FramesDecoded += got;
        return got;
    }

    // Extends the ring forwards by one chunk. The frames land beyond ringHead, where nothing is
    // reading, and only become visible once the new head is published.
    private bool DecodeChunk() {
        // A backfill leaves the source parked at the old tail, so the forward path has to put it
        // back before reading on. Tracking the position explicitly is what makes that safe.
        if(sourceNextFrame != ringHead) SeekSource(ringHead);

        int got = DecodeInto(ringHead, ChunkFrames);
        if(got <= 0) return false;

        Volatile.Write(ref ringHead, ringHead + got);
        filled.Set();                          // every chunk, so a waiting reader moves on at once
        return true;
    }

    private void ApplySeek() {
        long frame = Volatile.Read(ref pendingSeekFrame);
        if(frame < 0) {
            seekPending = false;
            return;
        }

        if(lengthFrames > 0 && frame > lengthFrames) frame = lengthFrames;
        if(frame < 0) frame = 0;

        // The ring was repositioned by RequestSeek, on the caller's thread. All that is left here
        // is to adopt the new position - the audio thread does no source I/O at all.
        playhead = frame;
        velocity = TargetVelocity();
        ended = false;

        // A seek re-defines where the record is, so the servo re-anchors there and drops whatever
        // hand movement had accumulated but not yet been applied.
        targetFrame = frame;
        appliedOffsetFrames = Volatile.Read(ref gestureOffsetSeconds) * sampleRate;

        // The caller flushes the chain, which resets BASS's byte counter for this stream back to
        // zero, so the output-frame origin moves with it.
        outputFrames = 0;
        Volatile.Write(ref runHead, 0);
        PushRun(0, playhead, velocity);

        // Retire only the request that was actually applied. A seek arriving while this ran - the
        // jump and snap buttons can fire two in quick succession - would otherwise be wiped by our
        // own cleanup and simply never happen. If the value has moved on, leave the flag set so
        // the next callback picks the new one up.
        //
        // Cleared last either way: until the map has been rebuilt, readers are served the
        // requested position and the producer keeps aiming at it rather than at the old playhead.
        if(Interlocked.CompareExchange(ref pendingSeekFrame, -1, frame) == frame) seekPending = false;
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
        if(disposed) return;                   // Player nulls the field after this, but be safe
        disposed = true;
        wake.Set();

        // Free our own stream first so no further callback can run, then make sure the producer is
        // not inside BASS: the caller frees the source handle as soon as this returns.
        int h = Interlocked.Exchange(ref stream, 0);
        if(h != 0) Bass.BASS_StreamFree(h);

        lock(sourceLock) { }
        if(producer != null && producer.Join(2000)) {
            wake.Dispose();
            filled.Dispose();
        }
    }
}
