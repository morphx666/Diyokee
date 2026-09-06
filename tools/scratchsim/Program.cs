using Diyokee;
using Un4seen.Bass;

// Renders a scratch gesture to WAV files, offline, so the INPUT path can be judged by ear without
// running the app. Usage:
//
//     dotnet run --project tools/scratchsim/scratchsim.csproj -- <audio file> [output directory]
//
// Why this exists. Scratching sounded wrong and three attempts at the audio path failed to change
// it: anti-aliasing, click removal, and the FX chain were all measured, fixed or ruled out while
// it still sounded the same. Rendering the same gesture through different layers is what finally
// located it - the audio path was fine and the GESTURE was the problem.
//
// So this models the real mouse path rather than an idealised one:
//   - the hand moves smoothly, but the browser reports INTEGER pixel positions
//   - event timestamps have ~1ms resolution
//   - events arrive in bursts over the SignalR circuit, with occasional gaps
//
// One waveform pixel is Files.TimeSlice / WaveformBarWidth = 12.5ms of audio at default zoom, so a
// single-pixel delta over one ~16ms event is already 0.78x. Differentiating one pair of samples
// turns that quantisation straight into playback rate, and a 4ms slew against ~16ms events made
// the rate a staircase. Both were audible; neither was visible to any measurement.
internal static class ScratchSim {
    const int RATE = 44100, CHANS = 2, BPF = CHANS * 4;
    const double SECONDS_PER_PIXEL = 0.05 / 4.0;      // TimeSlice / WaveformBarWidth

    // FollowMs > 0 renders through the position servo instead of a fitted speed.
    record Variant(string Name, double SlewMs, int FitEvents, double FollowMs = 0);

    static void Main(string[] args) {
        Directory.SetCurrentDirectory(AppContext.BaseDirectory);
        if(args.Length < 1) {
            Console.WriteLine("usage: scratchsim <audio file> [output directory]");
            return;
        }
        string src = args[0];
        string outDir = args.Length > 1 ? args[1] : AppContext.BaseDirectory;

        Bass.BASS_Init(0, RATE, BASSInit.BASS_DEVICE_DEFAULT, IntPtr.Zero);
        // How it sounded, how commanding a fitted speed sounds, and how following the hand's
        // position sounds at three different amounts of lag.
        Render(src, new Variant("1 - as it was, 4ms slew and a two-sample speed", 4, 1), outDir);
        Render(src, new Variant("2 - fitted speed over 5 samples", 20, 5), outDir);
        Render(src, new Variant("3 - servo, 25ms behind the hand", 0, 0, 25), outDir);
        Render(src, new Variant("4 - servo, 40ms behind the hand", 0, 0, 40), outDir);
        Render(src, new Variant("5 - servo, 60ms behind the hand", 0, 0, 60), outDir);
        Bass.BASS_Free();
        Console.WriteLine($"\ndone - wrote 5 files to {outDir}");
    }

    // Hand position in pixels. Peak speed ~2x: 2 / SECONDS_PER_PIXEL = 160 px/s.
    static double HandPixels(double t) => (160.0 / (2 * Math.PI * 0.4)) * Math.Sin(2 * Math.PI * 0.4 * t);

    static void Render(string src, Variant v, string outDir) {
        int source = Bass.BASS_StreamCreateFile(src, 0, 0,
            BASSFlag.BASS_STREAM_DECODE | BASSFlag.BASS_SAMPLE_FLOAT | BASSFlag.BASS_STREAM_PRESCAN);
        ScratchEngine engine = new(source, RATE, CHANS, new Loop()) { SlewTime = v.SlewMs / 1000.0 };
        if(v.FollowMs > 0) engine.FollowTime = v.FollowMs / 1000.0;

        engine.RequestSeek(11.0);
        float[] buf = new float[4096 * CHANS];
        Bass.BASS_ChannelGetData(engine.StreamHandle, buf, 1024 * BPF);
        engine.Touch();

        List<float> outp = new();
        Random rng = new(7);
        double t = 0, nextEvent = 0, quietUntil = 0;
        double peakSpeed = 0, speedSum = 0; int speedCount = 0;
        long startPx = (long)Math.Round(HandPixels(0));
        long lastPx = startPx, lastMs = 0;
        Queue<(double ms, long px)> history = new();

        while(t < 8.0) {
            if(t >= quietUntil && t >= nextEvent) {
                long px = (long)Math.Round(HandPixels(t));      // integer pixels, as the browser reports
                long ms = (long)Math.Round(t * 1000.0);         // ~1ms timestamp resolution
                if(ms > lastMs && v.FollowMs > 0) {
                    // The servo is told WHERE the hand is. No timestamps, no division, so the
                    // uneven arrival modelled above cannot turn into a wrong speed.
                    engine.SetGestureTarget((px - startPx) * SECONDS_PER_PIXEL);
                    lastPx = px; lastMs = ms;
                } else if(ms > lastMs) {
                    history.Enqueue((ms, px));
                    while(history.Count > v.FitEvents + 1) history.Dequeue();

                    double speed;
                    if(v.FitEvents <= 1) {
                        speed = (px - lastPx) * SECONDS_PER_PIXEL / ((ms - lastMs) / 1000.0);
                    } else {
                        // Least-squares slope over the recent samples instead of differentiating
                        // one pair: averages the pixel quantisation out instead of amplifying it.
                        var h = history.ToArray();
                        double mt = h.Average(e => e.ms), mp = h.Average(e => e.px);
                        double num = h.Sum(e => (e.ms - mt) * (e.px - mp));
                        double den = h.Sum(e => (e.ms - mt) * (e.ms - mt));
                        speed = den > 0 ? num / den * 1000.0 * SECONDS_PER_PIXEL : 0;
                    }
                    engine.SetGestureSpeed(speed);
                    lastPx = px; lastMs = ms;
                }
                nextEvent = t + 0.016 + rng.NextDouble() * 0.05;
                if(rng.NextDouble() < 0.12) quietUntil = t + 0.12 + rng.NextDouble() * 0.25;
            }

            int n = Bass.BASS_ChannelGetData(engine.StreamHandle, buf, 1024 * BPF);
            if(n <= 0) break;
            double sp = Math.Abs(engine.Velocity);
            if(sp > peakSpeed) peakSpeed = sp;
            speedSum += sp; speedCount++;
            int frames = n / BPF;
            for(int i = 0; i < frames * CHANS; i++) outp.Add(buf[i]);
            t += frames / (double)RATE;
        }

        // Sanitised, because a ':' in a name does not fail on Windows - it silently writes an NTFS
        // alternate data stream instead, leaving a 0-byte file behind and no error at all. One
        // variant went missing that way for weeks.
        string safe = v.Name;
        foreach(char bad in Path.GetInvalidFileNameChars()) safe = safe.Replace(bad, '-');
        string path = Path.Combine(outDir, safe + ".wav");
        using(FileStream fs = new(path, FileMode.Create, FileAccess.Write))
        using(BinaryWriter w = new(fs)) {
            int db = outp.Count * 4;
            w.Write("RIFF"u8.ToArray()); w.Write(36 + db); w.Write("WAVE"u8.ToArray());
            w.Write("fmt "u8.ToArray()); w.Write(16); w.Write((short)3); w.Write((short)CHANS);
            w.Write(RATE); w.Write(RATE * BPF); w.Write((short)BPF); w.Write((short)32);
            w.Write("data"u8.ToArray()); w.Write(db);
            foreach(float s in outp) w.Write(s);
        }
        Console.WriteLine($"  {v.Name,-46}  peak {peakSpeed,5:F1}x   mean {speedSum / speedCount:F2}x");
        engine.Dispose();
        Bass.BASS_StreamFree(source);
    }
}
