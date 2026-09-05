using Diyokee;
using Un4seen.Bass;

// Correctness tests for Classes/ScratchEngine.cs. Run from the repo root:
//
//     dotnet run --project tools/enginetest/enginetest.csproj
//
// Checks 1-17 use a generated 32-bit float WAV whose every sample equals its own frame index, so
// any output frame can be traced back to the exact source frame it came from. That makes
// pass-through, seeking, looping and the output->source position map all directly checkable
// without an audio device or listening to anything.
//
// Check 18 uses a sine instead, because the ramp cannot show what the engine does to a SIGNAL.
// Everything above verifies where samples come from; none of it looks at frequency content, which
// is why unfiltered decimation went unnoticed all the way to a listening test.
// See docs/scratch-audio-quality.md.

internal static class EngineTest {
    const int RATE = 44100;
    const int CHANS = 2;
    const int BYTES_PER_FRAME = CHANS * 4;
    const int SECONDS = 30;
    const long TOTAL_FRAMES = (long)RATE * SECONDS;

    static int failures;

    static void Check(string what, bool ok, string detail = "") {
        Console.WriteLine($"  [{(ok ? "PASS" : "FAIL")}] {what}{(detail == "" ? "" : "   " + detail)}");
        if(!ok) failures++;
    }

    static void Main() {
        Directory.SetCurrentDirectory(AppContext.BaseDirectory);
        string wav = Path.Combine(AppContext.BaseDirectory, "ramp.wav");
        WriteRampWav(wav);

        Bass.BASS_Init(0, RATE, BASSInit.BASS_DEVICE_DEFAULT, IntPtr.Zero);

        int source = Bass.BASS_StreamCreateFile(wav, 0, 0,
            BASSFlag.BASS_STREAM_DECODE | BASSFlag.BASS_SAMPLE_FLOAT | BASSFlag.BASS_STREAM_PRESCAN);
        if(source == 0) { Console.WriteLine($"could not open source: {Bass.BASS_ErrorGetCode()}"); return; }

        Loop loop = new();
        ScratchEngine engine = new(source, RATE, CHANS, loop);
        Console.WriteLine($"engine stream = {engine.StreamHandle}, length = {engine.LengthSeconds:F3}s (expected {SECONDS})\n");

        Check("reports the source length", Math.Abs(engine.LengthSeconds - SECONDS) < 0.01,
              $"{engine.LengthSeconds:F4}s");

        // ------------------------------------------------------------------ 1
        Console.WriteLine("\n[1] Pass-through is sample-exact from the start");
        float[] buf = new float[8192 * CHANS];
        long expected = 0;
        bool exact = true;
        for(int block = 0; block < 20 && exact; block++) {
            int got = Bass.BASS_ChannelGetData(engine.StreamHandle, buf, 8192 * BYTES_PER_FRAME);
            if(got <= 0) { exact = false; break; }
            int frames = got / BYTES_PER_FRAME;
            for(int f = 0; f < frames; f++) {
                if(buf[f * CHANS] != expected + f) {
                    exact = false;
                    Console.WriteLine($"      mismatch at out frame {expected + f}: got {buf[f * CHANS]}");
                    break;
                }
            }
            expected += frames;
        }
        Check("every output frame equals its source frame", exact, $"checked {expected} frames");

        // ------------------------------------------------------------------ 2
        Console.WriteLine("\n[2] Output -> source position map (no seeks yet, so 1:1)");
        Check("map at output frame 0", Math.Abs(engine.SourceSecondsAtOutputFrame(0) - 0) < 1e-9);
        Check("map at output frame 44100 == 1.000s",
              Math.Abs(engine.SourceSecondsAtOutputFrame(RATE) - 1.0) < 1e-9,
              $"{engine.SourceSecondsAtOutputFrame(RATE):F6}");

        // ------------------------------------------------------------------ 3
        Console.WriteLine("\n[3] Seek moves the playhead and rebases the map");
        engine.RequestSeek(10.0);
        Check("position reported immediately while the seek is pending",
              Math.Abs(engine.SourceSecondsAtOutputFrame(999999) - 10.0) < 1e-9);

        int g = Bass.BASS_ChannelGetData(engine.StreamHandle, buf, 4096 * BYTES_PER_FRAME);
        long firstFrameAfterSeek = (long)buf[0];
        Check("first frame after seek is 10s in", firstFrameAfterSeek == 10L * RATE,
              $"frame {firstFrameAfterSeek}, expected {10L * RATE}");
        Check("map rebased: output frame 0 -> 10.000s",
              Math.Abs(engine.SourceSecondsAtOutputFrame(0) - 10.0) < 1e-9,
              $"{engine.SourceSecondsAtOutputFrame(0):F6}");
        Check("map: output frame 4410 -> 10.100s",
              Math.Abs(engine.SourceSecondsAtOutputFrame(4410) - 10.1) < 1e-9,
              $"{engine.SourceSecondsAtOutputFrame(4410):F6}");

        // ------------------------------------------------------------------ 4
        Console.WriteLine("\n[4] Loop wraps exactly on the boundary");
        engine.RequestSeek(5.0);
        Bass.BASS_ChannelGetData(engine.StreamHandle, buf, 64 * BYTES_PER_FRAME);   // apply the seek

        loop.Start = 5.0;
        loop.End = 5.1;                       // 4410 frames long
        loop.Enabled = true;
        engine.ArmLoop(loop.End);             // enabling a loop always arms, as SetLoopSize does
        long loopStartFrame = 5L * RATE;
        long loopEndFrame = (long)(5.1 * RATE);

        // Pull well past the loop end and confirm no frame ever lands outside the loop, and that
        // the wrap happens at the boundary rather than a block edge.
        bool inBounds = true, sawWrap = false;
        long prev = -1;
        int wrapAtCorrectFrame = 0, wrapAtWrongFrame = 0;
        for(int block = 0; block < 40; block++) {
            int got2 = Bass.BASS_ChannelGetData(engine.StreamHandle, buf, 2048 * BYTES_PER_FRAME);
            if(got2 <= 0) break;
            int frames = got2 / BYTES_PER_FRAME;
            for(int f = 0; f < frames; f++) {
                long v = (long)buf[f * CHANS];
                if(v < loopStartFrame || v >= loopEndFrame) { inBounds = false; }
                if(prev >= 0 && v != prev + 1) {
                    sawWrap = true;
                    if(prev == loopEndFrame - 1 && v == loopStartFrame) wrapAtCorrectFrame++;
                    else wrapAtWrongFrame++;
                }
                prev = v;
            }
        }
        Check("playback never leaves the loop region", inBounds);
        Check("the loop actually wrapped", sawWrap);
        Check("every wrap was exactly end-1 -> start", wrapAtWrongFrame == 0,
              $"{wrapAtCorrectFrame} correct, {wrapAtWrongFrame} wrong");

        // ------------------------------------------------------------------ 5
        Console.WriteLine("\n[5] Position map stays correct across loop wraps");
        loop.Enabled = false;
        engine.RequestSeek(20.0);
        Bass.BASS_ChannelGetData(engine.StreamHandle, buf, 64 * BYTES_PER_FRAME);
        loop.Start = 20.0;
        loop.End = 20.05;                     // 2205 frames
        loop.Enabled = true;
        engine.ArmLoop(loop.End);

        long outFrames = 64;                  // already pulled above
        long seen = 0;
        bool mapOk = true;
        int wrapsSeen = 0;
        long prevSrc = -1;
        for(int block = 0; block < 10; block++) {
            int got3 = Bass.BASS_ChannelGetData(engine.StreamHandle, buf, 1024 * BYTES_PER_FRAME);
            if(got3 <= 0) break;
            int frames = got3 / BYTES_PER_FRAME;
            for(int f = 0; f < frames; f++) {
                long trueSrc = (long)buf[f * CHANS];
                if(prevSrc >= 0 && trueSrc != prevSrc + 1) wrapsSeen++;
                prevSrc = trueSrc;
                double mapped = engine.SourceSecondsAtOutputFrame(outFrames + f);
                if(Math.Abs(mapped * RATE - trueSrc) > 0.5) {
                    if(mapOk) Console.WriteLine($"      out {outFrames + f}: map says {mapped * RATE:F1}, actual {trueSrc}");
                    mapOk = false;
                }
                seen++;
            }
            outFrames += frames;
        }
        Check("the loop under test actually wrapped", wrapsSeen > 0, $"{wrapsSeen} wraps");
        Check("map matches the actual source frame at every output frame", mapOk, $"checked {seen} frames");

        // ------------------------------------------------------------------ 6
        Console.WriteLine("\n[6] End of track reports END exactly once, and seek revives it");
        loop.Enabled = false;
        engine.RequestSeek(SECONDS - 0.05);
        int pulls = 0, lastGot = 0;
        for(int i = 0; i < 200; i++) {
            lastGot = Bass.BASS_ChannelGetData(engine.StreamHandle, buf, 4096 * BYTES_PER_FRAME);
            pulls++;
            if(lastGot <= 0) break;
        }
        Check("stream reports the end", Bass.BASS_ChannelIsActive(engine.StreamHandle) == BASSActive.BASS_ACTIVE_STOPPED,
              $"after {pulls} pulls, state={Bass.BASS_ChannelIsActive(engine.StreamHandle)}");
        engine.RequestSeek(1.0);
        Bass.BASS_ChannelSetPosition(engine.StreamHandle, 0L, BASSMode.BASS_POS_BYTE);   // what SeekSeconds' flush does
        int revived = Bass.BASS_ChannelGetData(engine.StreamHandle, buf, 4096 * BYTES_PER_FRAME);
        Check("seeking after the end resumes playback", revived > 0 && (long)buf[0] == RATE,
              $"got {revived} bytes, first frame {(revived > 0 ? (long)buf[0] : -1)}, expected {RATE}");

        // ------------------------------------------------------------------ 7
        Console.WriteLine("\n[7] Seeking outside a loop plays on (matches the old sync behaviour)");
        loop.Start = 2.0; loop.End = 3.0; loop.Enabled = true;
        engine.RequestSeek(10.0);                       // past the loop end
        Bass.BASS_ChannelGetData(engine.StreamHandle, buf, 2048 * BYTES_PER_FRAME);
        long v0 = (long)buf[0];
        Bass.BASS_ChannelGetData(engine.StreamHandle, buf, 2048 * BYTES_PER_FRAME);
        long v1 = (long)buf[0];
        Check("does not get yanked back into the loop", v0 >= 10L * RATE && v1 > v0,
              $"frames {v0} then {v1}");

        // ------------------------------------------------------------------ 8
        // The reported scenario: a 16-beat loop, resized to 4 beats at beat 13. Playback must run
        // out the remaining 3 beats to the ORIGINAL end, then wrap and loop the new 4-beat length.
        Console.WriteLine("\n[8] Resizing a loop mid-pass defers to the next wrap");
        const double BEAT = 0.5;                      // 120 BPM, half a second per beat
        loop.Enabled = false;
        engine.RequestSeek(1.0);
        Bass.BASS_ChannelGetData(engine.StreamHandle, buf, 64 * BYTES_PER_FRAME);

        loop.Start = 1.0;
        loop.End = 1.0 + 16 * BEAT;                   // 16 beats = 8.0s, ends at 9.0s
        loop.Enabled = true;
        engine.ArmLoop(loop.End);                     // starting a loop arms immediately

        // Play up to beat 13 (1.0 + 6.5s = 7.5s)
        long beat13 = (long)(7.5 * RATE);
        while(true) {
            int n = Bass.BASS_ChannelGetData(engine.StreamHandle, buf, 512 * BYTES_PER_FRAME);
            if(n <= 0) break;
            long last = (long)buf[(n / BYTES_PER_FRAME - 1) * CHANS];
            if(last >= beat13) break;
        }

        // Shrink to 4 beats. Per SetLoopSize's rule the playhead (beat 13) is past the new end
        // (beat 4), so this must NOT arm - the old end stays armed.
        loop.End = 1.0 + 4 * BEAT;                    // 4 beats = 2.0s, ends at 3.0s
        // (no ArmLoop call - this is the deferred case)

        long oldEndFrame = (long)(9.0 * RATE);
        long newStartFrame = (long)(1.0 * RATE);
        long newEndFrame = (long)(3.0 * RATE);

        bool reachedOldEnd = false, wrappedCorrectly = false, stayedInNewLoop = true;
        long prevF = -1;
        int wraps = 0;
        for(int block = 0; block < 400; block++) {
            int n = Bass.BASS_ChannelGetData(engine.StreamHandle, buf, 512 * BYTES_PER_FRAME);
            if(n <= 0) break;
            int fr = n / BYTES_PER_FRAME;
            for(int f = 0; f < fr; f++) {
                long v = (long)buf[f * CHANS];
                if(v >= oldEndFrame - 2 && v < oldEndFrame) reachedOldEnd = true;
                if(prevF >= 0 && v != prevF + 1) {
                    wraps++;
                    if(wraps == 1 && prevF == oldEndFrame - 1 && v == newStartFrame) wrappedCorrectly = true;
                }
                if(wraps >= 1 && (v < newStartFrame || v >= newEndFrame)) stayedInNewLoop = false;
                prevF = v;
            }
            if(wraps >= 3) break;
        }

        Check("plays out to the ORIGINAL 16-beat end", reachedOldEnd);
        Check("first wrap is old-end -> loop start", wrappedCorrectly);
        Check("afterwards it loops the NEW 4-beat length", stayedInNewLoop && wraps >= 2,
              $"{wraps} wraps observed");

        // ------------------------------------------------------------------ 9
        // The immediate case: lengthening while still before the new end arms at once.
        Console.WriteLine("\n[9] Resizing while before the new end takes effect immediately");
        loop.Enabled = false;
        engine.RequestSeek(1.0);
        Bass.BASS_ChannelGetData(engine.StreamHandle, buf, 64 * BYTES_PER_FRAME);
        loop.Start = 1.0; loop.End = 1.0 + 4 * BEAT; loop.Enabled = true;
        engine.ArmLoop(loop.End);

        loop.End = 1.0 + 16 * BEAT;                   // grow to 16 beats, playhead still near start
        engine.ArmLoop(loop.End);                     // position <= new end, so SetLoopSize arms now

        bool reachedBeyondOldShortEnd = false;
        for(int block = 0; block < 400; block++) {
            int n = Bass.BASS_ChannelGetData(engine.StreamHandle, buf, 512 * BYTES_PER_FRAME);
            if(n <= 0) break;
            int fr = n / BYTES_PER_FRAME;
            for(int f = 0; f < fr; f++) {
                long v = (long)buf[f * CHANS];
                if(v > (long)(3.0 * RATE)) reachedBeyondOldShortEnd = true;
                if(v >= (long)(9.0 * RATE)) stayedInNewLoop = false;
            }
            if(reachedBeyondOldShortEnd) break;
        }
        Check("grown loop takes effect at once", reachedBeyondOldShortEnd);

        // ================================================================== PHASE 2
        // Every sample equals its own frame index, so the source is a straight line. Cubic
        // interpolation of a straight line is exact, which means at ANY speed the output value
        // must equal the fractional playhead - making speed directly measurable.
        loop.Enabled = false;
        engine.SlewTime = 0;                  // no smoothing, so speed changes are instant
        // The sample value IS the position here, so any gain stage destroys that identity. The
        // silence fade is therefore switched off for the position tests and exercised on its own
        // in [12] and [17], which are about the fade rather than about position.
        engine.SilenceFadeTime = 0;

        // ------------------------------------------------------------------ 10
        Console.WriteLine("\n[10] Reverse playback");
        engine.RequestSeek(15.0);
        Bass.BASS_ChannelGetData(engine.StreamHandle, buf, 64 * BYTES_PER_FRAME);
        engine.Touch();
        engine.SetGestureSpeed(-1.0);

        int n10 = Bass.BASS_ChannelGetData(engine.StreamHandle, buf, 2048 * BYTES_PER_FRAME);
        int f10 = n10 / BYTES_PER_FRAME;
        bool descending = f10 > 1;
        for(int f = 1; f < f10; f++) {
            if(Math.Abs((buf[f * CHANS] - buf[(f - 1) * CHANS]) + 1.0f) > 0.01f) { descending = false; break; }
        }
        Check("plays backwards exactly one frame per frame", descending,
              $"{buf[0]} -> {buf[(f10 - 1) * CHANS]} over {f10} frames");

        // ------------------------------------------------------------------ 11
        Console.WriteLine("\n[11] Fractional speeds interpolate correctly");
        foreach(double speed in new[] { 0.5, 0.25, 2.0, -0.5, 1.5 }) {
            engine.RequestSeek(12.0);
            Bass.BASS_ChannelGetData(engine.StreamHandle, buf, 64 * BYTES_PER_FRAME);
            engine.Touch();
            engine.SetGestureSpeed(speed);

            int n = Bass.BASS_ChannelGetData(engine.StreamHandle, buf, 1024 * BYTES_PER_FRAME);
            int fr = n / BYTES_PER_FRAME;
            double worst = 0;
            for(int f = 1; f < fr; f++) {
                double step = buf[f * CHANS] - buf[(f - 1) * CHANS];
                worst = Math.Max(worst, Math.Abs(step - speed));
            }
            Check($"speed {speed,5} advances the playhead by exactly {speed}", worst < 0.01,
                  $"worst step error {worst:E2}");
        }

        // ------------------------------------------------------------------ 12
        // This used to assert the output was silent from the very first frame. That WAS the bug:
        // cutting to zero in one frame is a full-amplitude step, and it is what clicked at the
        // start and end of every stroke. A held platter must still end up silent - but it has to
        // get there by fading.
        engine.SilenceFadeTime = 0.005;                    // this test is about the fade
        Console.WriteLine("\n[12] Held still fades to silence rather than stepping to it");
        engine.RequestSeek(12.0);
        Bass.BASS_ChannelGetData(engine.StreamHandle, buf, 64 * BYTES_PER_FRAME);
        engine.Touch();
        engine.SetGestureSpeed(0.0);
        int n12 = Bass.BASS_ChannelGetData(engine.StreamHandle, buf, 4096 * BYTES_PER_FRAME);
        int fr12 = n12 / BYTES_PER_FRAME;

        float level12 = Math.Abs(buf[0]);
        bool tailSilent = fr12 > 1024;
        for(int f = fr12 - 512; f < fr12; f++) if(buf[f * CHANS] != 0f) { tailSilent = false; break; }
        Check("a held platter ends up silent", tailSilent, $"over {fr12} frames");

        float biggestStep12 = 0;
        for(int f = 1; f < fr12; f++)
            biggestStep12 = Math.Max(biggestStep12, Math.Abs(buf[f * CHANS] - buf[(f - 1) * CHANS]));
        // The old hard gate gave a step of the full signal level, so this ratio was 1.0.
        Check("gets there without a step", biggestStep12 < level12 * 0.02f,
              $"biggest step {biggestStep12:F1} = {100.0 * biggestStep12 / level12:F2}% of level {level12:F0}");
        engine.SilenceFadeTime = 0;                        // back to exact values for [13]

        // ------------------------------------------------------------------ 13
        Console.WriteLine("\n[13] Position map is correct at non-unity speed");
        engine.RequestSeek(12.0);
        Bass.BASS_ChannelGetData(engine.StreamHandle, buf, 64 * BYTES_PER_FRAME);
        engine.Touch();
        engine.SetGestureSpeed(0.5);
        long outAt = 64;
        bool mapOk2 = true;
        for(int block = 0; block < 6; block++) {
            int n = Bass.BASS_ChannelGetData(engine.StreamHandle, buf, 1024 * BYTES_PER_FRAME);
            if(n <= 0) break;
            int fr = n / BYTES_PER_FRAME;
            for(int f = 0; f < fr; f += 64) {
                double trueSrc = buf[f * CHANS];
                double mapped = engine.SourceSecondsAtOutputFrame(outAt + f) * RATE;
                if(Math.Abs(mapped - trueSrc) > 2.0) {
                    if(mapOk2) Console.WriteLine($"      out {outAt + f}: map {mapped:F1} vs actual {trueSrc:F1}");
                    mapOk2 = false;
                }
            }
            outAt += fr;
        }
        Check("map follows the playhead at half speed", mapOk2);

        // ------------------------------------------------------------------ 14
        Console.WriteLine("\n[14] Release ramps back to play speed (Inertia)");
        engine.SlewTime = 0.004;
        engine.SpinUpTime = 0.05;
        engine.ReleaseMode = ReleaseModes.Inertia;
        engine.RequestSeek(12.0);
        Bass.BASS_ChannelGetData(engine.StreamHandle, buf, 64 * BYTES_PER_FRAME);
        engine.Touch();
        engine.SetGestureSpeed(-2.0);
        Bass.BASS_ChannelGetData(engine.StreamHandle, buf, 1024 * BYTES_PER_FRAME);
        Check("scratching backwards before release", engine.Velocity < -0.5, $"v={engine.Velocity:F3}");

        engine.Release();
        for(int i = 0; i < 60; i++) Bass.BASS_ChannelGetData(engine.StreamHandle, buf, 1024 * BYTES_PER_FRAME);
        Check("settles back at play speed", Math.Abs(engine.Velocity - 1.0) < 0.01, $"v={engine.Velocity:F4}");
        Check("no brake signal in Inertia mode", !engine.ConsumeBrakeCompleted());

        // ------------------------------------------------------------------ 15
        Console.WriteLine("\n[15] Release brakes to a standstill (Stop) and signals it");
        engine.ReleaseMode = ReleaseModes.Stop;
        engine.BrakeTime = 0.05;
        engine.RequestSeek(12.0);
        Bass.BASS_ChannelGetData(engine.StreamHandle, buf, 64 * BYTES_PER_FRAME);
        engine.Touch();
        engine.SetGestureSpeed(1.0);
        Bass.BASS_ChannelGetData(engine.StreamHandle, buf, 1024 * BYTES_PER_FRAME);
        engine.Release();
        for(int i = 0; i < 60; i++) Bass.BASS_ChannelGetData(engine.StreamHandle, buf, 1024 * BYTES_PER_FRAME);
        Check("comes to a stop", Math.Abs(engine.Velocity) < 0.01, $"v={engine.Velocity:F4}");
        Check("signals that it braked to a halt", engine.ConsumeBrakeCompleted());
        Check("the signal is consumed once", !engine.ConsumeBrakeCompleted());
        for(int i = 0; i < 20; i++) Bass.BASS_ChannelGetData(engine.StreamHandle, buf, 1024 * BYTES_PER_FRAME);
        Check("stays stopped instead of spinning back up", Math.Abs(engine.Velocity) < 0.01, $"v={engine.Velocity:F4}");
        engine.Resume();
        for(int i = 0; i < 40; i++) Bass.BASS_ChannelGetData(engine.StreamHandle, buf, 1024 * BYTES_PER_FRAME);
        Check("Resume() lifts the halt", Math.Abs(engine.Velocity - 1.0) < 0.01, $"v={engine.Velocity:F4}");

        // ------------------------------------------------------------------ 16
        Console.WriteLine("\n[16] Scratching backwards out of a loop wraps to the end");
        engine.ReleaseMode = ReleaseModes.Inertia;
        engine.SlewTime = 0;
        engine.RequestSeek(8.0);
        Bass.BASS_ChannelGetData(engine.StreamHandle, buf, 64 * BYTES_PER_FRAME);
        loop.Start = 8.0; loop.End = 8.5; loop.Enabled = true;
        engine.ArmLoop(loop.End);
        engine.Touch();
        engine.SetGestureSpeed(-1.0);

        long lStart = (long)(8.0 * RATE), lEnd = (long)(8.5 * RATE);
        bool stayedIn = true, wrappedBack = false;
        for(int block = 0; block < 20; block++) {
            int n = Bass.BASS_ChannelGetData(engine.StreamHandle, buf, 1024 * BYTES_PER_FRAME);
            if(n <= 0) break;
            int fr = n / BYTES_PER_FRAME;
            for(int f = 0; f < fr; f++) {
                double v = buf[f * CHANS];
                if(v < lStart - 2 || v > lEnd + 2) stayedIn = false;
                if(f > 0 && buf[f * CHANS] - buf[(f - 1) * CHANS] > 1.0) wrappedBack = true;
            }
        }
        Check("stays within the loop while scratching backwards", stayedIn);
        Check("wraps backwards from loop start to loop end", wrappedBack);

        // ------------------------------------------------------------------ 17
        // A paused deck's mixer channels carry BASS_MIXER_CHAN_NORAMPIN, so un-pausing them for a
        // scratch steps from silence straight to whatever the deck was sitting on. The engine has
        // to do the ramp itself.
        Console.WriteLine("\n[17] FadeInFromSilence ramps up instead of starting at full level");
        loop.Enabled = false;
        engine.SilenceFadeTime = 0.005;
        engine.RequestSeek(20.0);
        Bass.BASS_ChannelGetData(engine.StreamHandle, buf, 64 * BYTES_PER_FRAME);
        engine.Touch();
        engine.SetGestureSpeed(1.0);
        Bass.BASS_ChannelGetData(engine.StreamHandle, buf, 1024 * BYTES_PER_FRAME);
        float level17 = Math.Abs(buf[0]);                  // running at full level here

        engine.FadeInFromSilence();
        int n17 = Bass.BASS_ChannelGetData(engine.StreamHandle, buf, 2048 * BYTES_PER_FRAME);
        int fr17 = n17 / BYTES_PER_FRAME;
        float first17 = Math.Abs(buf[0]);
        float last17 = Math.Abs(buf[(fr17 - 1) * CHANS]);
        Check("starts from near silence", first17 < level17 * 0.05f,
              $"first sample {first17:F0} vs level {level17:F0}");
        Check("climbs back to full level", last17 > level17 * 0.5f,
              $"last sample {last17:F0}");

        // ------------------------------------------------------------------ 20
        // Seeking the source and decoding both happen on the audio thread, so how often they
        // happen matters. Backfilling used to discard everything decoded ahead of the playhead,
        // leaving only BackfillFrames of history and nothing in front - so a scratch oscillating
        // across that boundary paid a file seek plus a chunk of decoding on every single pass.
        Console.WriteLine("\n[20] Scratching across the backfill boundary does not re-seek every pass");
        loop.Enabled = false;
        engine.SlewTime = 0;
        engine.SilenceFadeTime = 0;
        engine.RequestSeek(12.0);
        Bass.BASS_ChannelGetData(engine.StreamHandle, buf, 64 * BYTES_PER_FRAME);
        engine.Touch();

        long seeksBefore = engine.SourceSeeks;
        long decodedBefore = engine.FramesDecoded;
        bool sweepExact = true;
        for(int sweep = 0; sweep < 6; sweep++) {
            engine.SetGestureSpeed(sweep % 2 == 0 ? -8.0 : 8.0);
            for(int block = 0; block < 6; block++) {
                int n = Bass.BASS_ChannelGetData(engine.StreamHandle, buf, 1024 * BYTES_PER_FRAME);
                if(n <= 0) break;
                int fr = n / BYTES_PER_FRAME;
                for(int f = 1; f < fr; f++) {
                    double step = buf[f * CHANS] - buf[(f - 1) * CHANS];
                    if(Math.Abs(Math.Abs(step) - 8.0) > 0.01) sweepExact = false;
                }
            }
        }
        long sweepSeeks = engine.SourceSeeks - seeksBefore;
        long sweepDecoded = engine.FramesDecoded - decodedBefore;
        Console.WriteLine($"      6 sweeps at 8x: {sweepSeeks} source seeks, {sweepDecoded} frames decoded");
        Check("audio stays sample-exact across the boundary", sweepExact);
        Check("the boundary is crossed without re-seeking each pass", sweepSeeks <= 6,
              $"{sweepSeeks} seeks over 6 sweeps");

        // ------------------------------------------------------------------ 21
        // The servo. The hand's POSITION is the input and the engine works out the speed, which
        // is what stops the input's quantisation and bursty delivery reaching the audio.
        // See docs/scratch-audio-quality.md.
        Console.WriteLine("\n[21] Position servo follows the hand rather than being told a speed");
        const double FOLLOW = 0.040;
        const double PIXEL = 0.05 / 4.0;                   // Files.TimeSlice / WaveformBarWidth
        loop.Enabled = false;
        engine.SilenceFadeTime = 0;
        engine.TouchMode = TouchModes.Vinyl;
        engine.FollowTime = FOLLOW;

        // A hand moving steadily at 2x, sampled every 16ms as the browser does.
        var steady = Drag(engine, buf, 12.0, 2.0);
        Console.WriteLine($"      2x steady:   {steady.Lag * 1000,5:F1}ms behind, mean {steady.Speed:F3}x,"
                        + $" worst excursion {steady.Ripple:F3}x");
        Check("settles at the hand's speed", Math.Abs(steady.Speed - 2.0) < 0.02, $"{steady.Speed:F4}x");
        Check("sits FollowTime's worth of travel behind the hand",
              Math.Abs(steady.Lag - 2.0 * FOLLOW) < 0.020, $"{steady.Lag * 1000:F1}ms of source");

        // The same hand, sampled at irregular intervals with gaps - which is what bursty delivery
        // looks like to the engine. Dividing a distance by an interval makes this the dominant
        // error term; a servo divides by nothing, so uneven sampling can only cost what it
        // genuinely takes away, which is knowing where the hand was in the gaps.
        var uneven = Drag(engine, buf, 12.0, 2.0, jitter: true);
        Console.WriteLine($"      2x jittered: {uneven.Lag * 1000,5:F1}ms behind, mean {uneven.Speed:F3}x,"
                        + $" worst excursion {uneven.Ripple:F3}x");
        Check("irregular sampling does not change the speed it settles at",
              Math.Abs(uneven.Speed - 2.0) < 0.02, $"{uneven.Speed:F4}x");
        // The extra distance is the age of the newest sample, not a servo error: sampled every
        // 6-56ms instead of every 16ms, the hand's reported position is simply older.
        Check("and costs only the staleness of the samples themselves",
              uneven.Lag - steady.Lag < 2.0 * 0.056, $"{(uneven.Lag - steady.Lag) * 1000:F1}ms further back");

        // Whole-pixel reporting, which is the quantisation that used to become playback rate.
        // 1x is the worst case: the slowest pixel-crossing rate the loop has to smooth.
        //
        // An ISOLATED target step of A seconds drives the critically damped loop to a peak
        // velocity excursion of 2A/(e*FollowTime), so the ripple is set by the input's quantum
        // against FollowTime and the only way to shrink it is to sit further behind the hand.
        // That closed form is an upper bound here: the loop peaks 20ms after a step and pixels
        // arrive every 12.5ms at 1x, so consecutive responses average each other down.
        var pixels = Drag(engine, buf, 12.0, 1.0, quantum: PIXEL);
        double predicted = 2 * PIXEL / (Math.E * FOLLOW);
        Console.WriteLine($"      1x whole-pixel: worst excursion {pixels.Ripple:F3}x"
                        + $" (predicted {predicted:F3}x for a {PIXEL / 0.016:F2}x quantum)");
        Check("whole-pixel input stays under the isolated-step bound",
              pixels.Ripple < predicted, $"{pixels.Ripple:F3}x vs {predicted:F3}x");
        Check("and it still averages out to the hand's speed",
              Math.Abs(pixels.Speed - 1.0) < 0.02, $"{pixels.Speed:F4}x");

        // A hand that stops moving stops the record, with no timeout and no decay constant: the
        // target simply stops moving and the error runs out.
        for(int i = 0; i < 40; i++) Bass.BASS_ChannelGetData(engine.StreamHandle, buf, 1024 * BYTES_PER_FRAME);
        Check("a hand that stops moving brings the record to rest", Math.Abs(engine.Velocity) < 0.01,
              $"v={engine.Velocity:F4}");

        engine.Release();
        engine.Dispose();
        Bass.BASS_StreamFree(source);

        // ------------------------------------------------------------------ 18
        // Everything above traces positions. This asks what happens to a SIGNAL when the platter
        // runs faster than 1x.
        //
        // An 8kHz tone played at 4x would ideally land at 32kHz - above the 22.05kHz Nyquist, so a
        // band-limited resampler has nothing to emit and should be near silent. Catmull-Rom is an
        // interpolator with no rate-dependent lowpass, so instead the tone folds back to
        // |44100 - 32000| = 12100Hz and is heard as an inharmonic whistle.
        //
        // 1x and 2x are controls: at 2x the tone lands at 16kHz, still under Nyquist, so nothing
        // should fold and the 12100Hz bin must stay empty.
        Console.WriteLine("\n[18] Aliasing above 1x  (see docs/scratch-audio-quality.md)");
        float[] tone = new float[16384 * CHANS];

        // Runs a pure tone through its own engine at one rate and reports where the energy landed:
        // at the pitch-shifted tone if that is still under Nyquist, and at the frequency the tone
        // folds back to if it is not.
        (double ideal, double fold, double atIdeal, double atFold) Probe(int src, double toneHz, double rate) {
            ScratchEngine e = new(src, RATE, CHANS, new Loop()) { SlewTime = 0, SilenceFadeTime = 0 };
            e.RequestSeek(2.0);
            Bass.BASS_ChannelGetData(e.StreamHandle, tone, 64 * BYTES_PER_FRAME);
            e.Touch();
            e.SetGestureSpeed(rate);
            Bass.BASS_ChannelGetData(e.StreamHandle, tone, 4096 * BYTES_PER_FRAME);       // settle
            int frames = Bass.BASS_ChannelGetData(e.StreamHandle, tone, 16384 * BYTES_PER_FRAME) / BYTES_PER_FRAME;
            e.Dispose();

            double ideal = toneHz * rate;
            double fold = ideal % RATE;
            if(fold > RATE / 2.0) fold = RATE - fold;
            return (ideal, fold,
                    ideal < RATE / 2.0 ? ToneAmplitude(tone, frames, ideal) : 0,
                    ToneAmplitude(tone, frames, fold));
        }

        int MakeTone(string name, double freq) {
            string path = Path.Combine(AppContext.BaseDirectory, name);
            WriteSineWav(path, freq, 30.0, 0.5);
            return Bass.BASS_StreamCreateFile(path, 0, 0,
                BASSFlag.BASS_STREAM_DECODE | BASSFlag.BASS_SAMPLE_FLOAT | BASSFlag.BASS_STREAM_PRESCAN);
        }

        const double TONE = 8000.0;
        int sineSource = MakeTone("sine8k.wav", TONE);
        double reference = Probe(sineSource, TONE, 1.0).atIdeal;

        foreach(double rate in new[] { 1.0, 2.0, 4.0, 8.0, 16.0, 30.0 }) {
            var p = Probe(sineSource, TONE, rate);
            if(p.ideal < RATE / 2.0) {
                Console.WriteLine($"      {rate,4}x: tone lands at {p.ideal,6:F0}Hz {Db(p.atIdeal, reference),6:F1}dB (legitimate)");
            } else {
                double rejection = Db(p.atFold, reference);
                Console.WriteLine($"      {rate,4}x: {p.ideal,7:F0}Hz is over Nyquist, so it must be removed"
                                  + $" - {p.fold,5:F0}Hz bin {rejection,7:F1}dB");
                Check($"{rate}x rejects content above Nyquist by at least 40dB", rejection < -40,
                      $"{rejection:F1}dB");
            }
        }
        Bass.BASS_StreamFree(sineSource);

        // What the kernel-stretch cap costs. The cap holds the cutoff at Nyquist/16 however fast
        // the platter goes, so above 16x a band between Nyquist/rate and Nyquist/16 is no longer
        // filtered out and does fold back. A 1kHz tone at 30x sits inside that band deliberately.
        // Reported rather than asserted: this is the bounded-cost trade, not a defect.
        int lowSource = MakeTone("sine1k.wav", 1000.0);
        double lowReference = Probe(lowSource, 1000.0, 1.0).atIdeal;
        var capped = Probe(lowSource, 1000.0, 30.0);
        Console.WriteLine($"      cost of the stretch cap: 1000Hz at 30x folds to {capped.fold:F0}Hz"
                          + $" at {Db(capped.atFold, lowReference):F1}dB");

        // The hardest case for any windowed sinc is content just above the cutoff, in the
        // transition band - real music has a continuous spectrum, so this is what actually
        // determines how clean a scratch sounds.
        int edgeSource = MakeTone("sine6k6.wav", 6600.0);
        double edgeReference = Probe(edgeSource, 6600.0, 1.0).atIdeal;
        var edge = Probe(edgeSource, 6600.0, 4.0);        // cutoff at 4x is 5512Hz, so 1.2x over it
        Console.WriteLine($"      transition band: 6600Hz at 4x (cutoff 5512Hz) folds to {edge.fold:F0}Hz"
                          + $" at {Db(edge.atFold, edgeReference):F1}dB");
        Bass.BASS_StreamFree(edgeSource);
        Bass.BASS_StreamFree(lowSource);

        // ------------------------------------------------------------------ 19
        // The kernel costs more taps the faster the platter goes, and this runs on the audio
        // thread on hardware as slow as a Raspberry Pi. Measure it rather than estimating.
        Console.WriteLine("\n[19] Resampler throughput (this machine)");
        int benchSource = MakeTone("bench.wav", 1000.0);
        const int BENCH_FRAMES = 22050;                   // half a second of output

        // Warm up the JIT on the band-limited path first, or whichever rate is measured first
        // pays for compiling it and reads several times slower than it really is.
        foreach(double warmRate in new[] { 4.0, 32.0 }) {
            ScratchEngine w = new(benchSource, RATE, CHANS, new Loop()) { SlewTime = 0, SilenceFadeTime = 0 };
            w.RequestSeek(0.5);
            Bass.BASS_ChannelGetData(w.StreamHandle, tone, 64 * BYTES_PER_FRAME);
            w.Touch();
            w.SetGestureSpeed(warmRate);
            for(int i = 0; i < 8; i++) Bass.BASS_ChannelGetData(w.StreamHandle, tone, 8192 * BYTES_PER_FRAME);
            w.Dispose();
        }

        foreach(double rate in new[] { 1.0, 2.0, 4.0, 8.0, 16.0, 32.0 }) {
            ScratchEngine b = new(benchSource, RATE, CHANS, new Loop()) { SlewTime = 0, SilenceFadeTime = 0 };
            b.RequestSeek(0.5);
            Bass.BASS_ChannelGetData(b.StreamHandle, tone, 64 * BYTES_PER_FRAME);
            b.Touch();
            b.SetGestureSpeed(rate);
            Bass.BASS_ChannelGetData(b.StreamHandle, tone, 4096 * BYTES_PER_FRAME);

            var sw = System.Diagnostics.Stopwatch.StartNew();
            int pulled = 0;
            while(pulled < BENCH_FRAMES) {
                int n = Bass.BASS_ChannelGetData(b.StreamHandle, tone, 8192 * BYTES_PER_FRAME);
                if(n <= 0) break;
                pulled += n / BYTES_PER_FRAME;
            }
            sw.Stop();
            b.Dispose();

            double realtime = (pulled / (double)RATE) / sw.Elapsed.TotalSeconds;
            Console.WriteLine($"      {rate,4}x: {realtime,7:F0}x realtime   ({pulled} frames in {sw.Elapsed.TotalMilliseconds:F1}ms)");
            // Desktop figures. A Pi is roughly 5-10x slower, so 8x here is around realtime
            // there - acceptable only because fast rates happen in brief flicks that BASS's
            // 500ms output buffer absorbs. Sustained scratching sits at 2-8x.
            Check($"{rate}x renders faster than realtime with margin", realtime > 8, $"{realtime:F0}x");
        }
        Bass.BASS_StreamFree(benchSource);

        Bass.BASS_Free();

        Console.WriteLine($"\n{(failures == 0 ? "ALL CHECKS PASSED" : $"{failures} CHECK(S) FAILED")}");
        Environment.Exit(failures == 0 ? 0 : 1);
    }

    // 32-bit float WAV where sample value == frame index, so output can be traced to its source.
    // Drags the platter at a constant speed for two seconds and reports what the servo did with
    // it, measured over the second half once the loop has settled: the mean distance the record
    // sits behind the hand, its mean speed, and the worst velocity excursion.
    //
    // The hand moves continuously and is SAMPLED, which is what a browser does. "jitter" makes
    // that sampling irregular with occasional gaps, as the real one is. "quantum" rounds the
    // reported position, as whole-pixel reporting does.
    static (double Lag, double Speed, double Ripple) Drag(ScratchEngine engine, float[] buf,
                                                          double from, double speed,
                                                          bool jitter = false, double quantum = 0) {
        const int BLOCK = 256;                             // ~5.8ms, fine enough to see the ripple
        Random rng = new(11);

        engine.RequestSeek(from);
        Bass.BASS_ChannelGetData(engine.StreamHandle, buf, 64 * BYTES_PER_FRAME);
        engine.Touch();

        double t = 0, nextSample = 0, lagSum = 0, speedSum = 0, ripple = 0;
        int taken = 0;
        while(t < 2.0) {
            if(t >= nextSample) {
                double at = t * speed;
                engine.SetGestureTarget(quantum > 0 ? Math.Round(at / quantum) * quantum : at);
                nextSample = t + (jitter ? 0.006 + rng.NextDouble() * 0.05 : 0.016);
            }

            Bass.BASS_ChannelGetData(engine.StreamHandle, buf, BLOCK * BYTES_PER_FRAME);
            t += BLOCK / (double)RATE;

            if(t <= 1.0) continue;
            lagSum += (from + t * speed) - engine.DecodeSeconds;
            speedSum += engine.Velocity;
            ripple = Math.Max(ripple, Math.Abs(engine.Velocity - speed));
            taken++;
        }
        return (lagSum / taken, speedSum / taken, ripple);
    }

    static void WriteRampWav(string path) {
        using FileStream fs = new(path, FileMode.Create, FileAccess.Write);
        using BinaryWriter w = new(fs);
        WriteWavHeader(w, TOTAL_FRAMES);

        for(long f = 0; f < TOTAL_FRAMES; f++) {
            float v = f;
            w.Write(v);
            w.Write(v);
        }
    }

    // A pure tone, for asking what the engine does to a signal rather than to a position.
    static void WriteSineWav(string path, double freq, double seconds, double amplitude) {
        long frames = (long)(RATE * seconds);
        using FileStream fs = new(path, FileMode.Create, FileAccess.Write);
        using BinaryWriter w = new(fs);
        WriteWavHeader(w, frames);

        for(long f = 0; f < frames; f++) {
            float v = (float)(amplitude * Math.Sin(2.0 * Math.PI * freq * f / RATE));
            w.Write(v);
            w.Write(v);
        }
    }

    static void WriteWavHeader(BinaryWriter w, long frames) {
        int dataBytes = (int)(frames * BYTES_PER_FRAME);

        w.Write("RIFF"u8.ToArray());
        w.Write(36 + dataBytes);
        w.Write("WAVE"u8.ToArray());
        w.Write("fmt "u8.ToArray());
        w.Write(16);
        w.Write((short)3);                       // IEEE float
        w.Write((short)CHANS);
        w.Write(RATE);
        w.Write(RATE * BYTES_PER_FRAME);
        w.Write((short)BYTES_PER_FRAME);
        w.Write((short)32);
        w.Write("data"u8.ToArray());
        w.Write(dataBytes);
    }

    // Amplitude of one frequency in a block of interleaved frames (Goertzel, Hann-windowed). Only
    // ever used to compare one tone against another, so the window's scaling factor cancels.
    static double ToneAmplitude(float[] frames, int count, double freq) {
        double w = 2.0 * Math.PI * freq / RATE;
        double coeff = 2.0 * Math.Cos(w);
        double s1 = 0, s2 = 0;
        for(int i = 0; i < count; i++) {
            double hann = 0.5 - 0.5 * Math.Cos(2.0 * Math.PI * i / (count - 1));
            double s0 = coeff * s1 - s2 + frames[i * CHANS] * hann;
            s2 = s1;
            s1 = s0;
        }
        double re = s1 - s2 * Math.Cos(w);
        double im = s2 * Math.Sin(w);
        return 2.0 * Math.Sqrt(re * re + im * im) / count;
    }

    static double Db(double amp, double reference) => 20.0 * Math.Log10((amp + 1e-12) / (reference + 1e-12));
}
