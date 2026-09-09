namespace Diyokee;

// Where the transients are in a stream of samples. The signal processing half of onset detection,
// with no BASS in it, so tools/gridtest can drive it on a synthesised click track - which is the
// only way to test it repeatably, and the half where a wrong sign or a mis-scaled threshold would
// otherwise just look like "no beats detected".
//
// Classes/OnsetDetector.cs is the other half: it opens the track and hands the samples over.
//
// Method: three band-limited energy envelopes at a ~6 ms hop, log-compressed, half-wave-rectified
// first difference summed across the bands, then peaks picked against a local mean and deviation.
//
// Bands rather than an FFT because that is what the material calls for and what the cost allows.
// A kick, a snare and a hat land in different bands, so summing their fluxes catches all three,
// while an FFT at this hop over a five-minute track means ~50,000 transforms - and Classes/FFT.cs
// allocates a ComplexDouble per bin, so it would allocate tens of millions of objects to answer a
// question three biquads answer.
//
// log(1 + lambda * E) rather than log(E), because a quiet passage has to come out quiet. Plain log
// is scale-invariant, so it turns the noise floor of a breakdown into onsets as confident as a
// kick drum - and "no beats detected here" is a result this feature depends on being able to
// produce. log(1 + lambda * E) is linear near zero and compressive where the music is, so silence
// stays silent.
public static class OnsetEnvelope {
    // ~5.8 ms at 44.1 kHz. Onsets are placed to a fraction of this by interpolation, so the hop
    // only bounds how close two onsets can be, not how precisely one is timed.
    public const double HopSeconds = 0.0058;

    private const double LowCrossoverHz = 150.0;
    private const double HighCrossoverHz = 1500.0;

    // Maps a frame's mean square into the compressive part of the log. Music sits around 1e-3 to
    // 1e-1 mean square in float samples, so 1000 puts it at log(2) to log(101).
    private const double Lambda = 1000.0;

    // Half-width of the window a peak is judged against. Long enough to span a bar at any usable
    // tempo, so a single loud section cannot raise the bar for the whole track.
    private const double LocalWindowSeconds = 0.5;

    // How many local deviations above the local mean a peak has to stand to count.
    private const double PeakThreshold = 1.3;

    // A peak below this fraction of the track's loud transients is not a transient, whatever the
    // local statistics say. This is what keeps the noise floor of a breakdown from producing
    // onsets: in a real track the loud transients are the music, so the bar is set by the music.
    private const double AbsoluteFloorFraction = 0.08;

    // ...but a track with no music in it at all sets that bar at its own noise, so a fraction of
    // it is not a floor. This is the floor that does not move: below it the flux corresponds to an
    // energy step around -47 dB, which is not an onset in anything, whatever else the track holds.
    private const double MinimumFlux = 0.02;

    // Two onsets closer than this are one onset; the weaker is dropped.
    private const double MinSpacingSeconds = 0.030;

    // `read` fills the buffer it is handed and returns how many samples it wrote, or zero at the
    // end - mono, float. `startSeconds` is where those samples begin in the track; `fromSeconds`
    // is the first position an onset may be reported at, so a caller can decode a pre-roll for the
    // filters to settle in and still get nothing back from it.
    public static List<BeatAlign.Onset> Onsets(Func<float[], int> read, int rate,
                                               double startSeconds, double fromSeconds) {
        if(rate <= 0) return [];

        int hop = Math.Max(1, (int)Math.Round(rate * HopSeconds));
        double[] odf = Flux(read, rate, hop);

        return odf.Length < 5 ? [] : PickPeaks(odf, startSeconds, (double)hop / rate, fromSeconds);
    }

    // One onset detection function value per hop.
    private static double[] Flux(Func<float[], int> read, int rate, int hop) {
        Biquad low = Biquad.LowPass(LowCrossoverHz, rate);
        Biquad midHigh = Biquad.HighPass(LowCrossoverHz, rate);
        Biquad midLow = Biquad.LowPass(HighCrossoverHz, rate);
        Biquad high = Biquad.HighPass(HighCrossoverHz, rate);

        float[] buffer = new float[1 << 16];
        List<double> result = [];

        double lowSum = 0, midSum = 0, highSum = 0;
        int inFrame = 0;
        double lastLow = double.NaN, lastMid = 0, lastHigh = 0;

        while(true) {
            int samples = read(buffer);
            if(samples <= 0) break;

            for(int i = 0; i < samples; i++) {
                double x = buffer[i];

                double l = low.Process(x);
                double m = midLow.Process(midHigh.Process(x));
                double h = high.Process(x);

                lowSum += l * l;
                midSum += m * m;
                highSum += h * h;

                if(++inFrame < hop) continue;

                double a = Compress(lowSum / hop);
                double b = Compress(midSum / hop);
                double c = Compress(highSum / hop);

                // The first frame has nothing to difference against, so it is only a baseline.
                if(!double.IsNaN(lastLow)) {
                    result.Add(Math.Max(0, a - lastLow) + Math.Max(0, b - lastMid) + Math.Max(0, c - lastHigh));
                }

                lastLow = a;
                lastMid = b;
                lastHigh = c;
                lowSum = midSum = highSum = 0;
                inFrame = 0;
            }
        }

        return [.. result];
    }

    private static double Compress(double meanSquare) => Math.Log(1.0 + Lambda * meanSquare);

    // Local peaks that stand clear of their surroundings. The threshold is a moving mean plus a
    // multiple of the moving deviation, both from prefix sums, so it costs one pass rather than a
    // sort per frame.
    private static List<BeatAlign.Onset> PickPeaks(double[] odf, double start, double hopSeconds, double fromSeconds) {
        int n = odf.Length;

        double[] sum = new double[n + 1];
        double[] sumSquares = new double[n + 1];

        for(int i = 0; i < n; i++) {
            sum[i + 1] = sum[i] + odf[i];
            sumSquares[i + 1] = sumSquares[i] + odf[i] * odf[i];
        }

        // The track's own idea of a loud transient, used as an absolute floor so that the local
        // statistics of a silent passage cannot promote its noise to an onset.
        double[] ranked = [.. odf];
        Array.Sort(ranked);
        double floor = Math.Max(MinimumFlux, ranked[(int)(n * 0.95)] * AbsoluteFloorFraction);

        int half = Math.Max(4, (int)Math.Round(LocalWindowSeconds / hopSeconds));
        List<BeatAlign.Onset> onsets = [];

        for(int i = 2; i < n - 2; i++) {
            double here = odf[i];
            if(here < floor) continue;

            // A local maximum, and strictly greater on the rising side so a plateau yields one
            // onset rather than several.
            if(here <= odf[i - 1] || here <= odf[i - 2] || here < odf[i + 1] || here < odf[i + 2]) continue;

            int lo = Math.Max(0, i - half);
            int hi = Math.Min(n, i + half + 1);
            int count = hi - lo;

            double mean = (sum[hi] - sum[lo]) / count;
            double variance = (sumSquares[hi] - sumSquares[lo]) / count - mean * mean;
            double deviation = Math.Sqrt(Math.Max(0, variance));

            if(here < mean + PeakThreshold * deviation) continue;

            // Parabolic interpolation through the three samples around the peak, which places the
            // onset to a fraction of a hop instead of snapping it to the frame grid.
            double denominator = odf[i - 1] - 2 * here + odf[i + 1];
            double offset = denominator != 0 ? 0.5 * (odf[i - 1] - odf[i + 1]) / denominator : 0;
            if(Math.Abs(offset) > 0.5) offset = 0;

            // The flux at frame i is the rise from frame i-1 into frame i, so the attack is inside
            // frame i. Its centre is the best single guess, and the half-hop that is left is a bias
            // shared by every onset - which cancels in the tempo BeatAlign fits, and cannot move
            // the downbeat, because the downbeat is not measured here.
            double seconds = start + (i + 0.5 + offset) * hopSeconds;
            if(seconds < fromSeconds) continue;

            double strength = deviation > 1e-12 ? (here - mean) / deviation : PeakThreshold;

            // Merge with the previous onset if they are too close to be separate hits.
            if(onsets.Count > 0 && seconds - onsets[^1].Seconds < MinSpacingSeconds) {
                if(strength > onsets[^1].Strength) onsets[^1] = new BeatAlign.Onset(seconds, strength);
                continue;
            }

            onsets.Add(new BeatAlign.Onset(seconds, strength));
        }

        return onsets;
    }

    // Direct form I. Q is 1/sqrt(2), so a pair of these makes a band with no ripple at the
    // crossover.
    private struct Biquad {
        private double b0, b1, b2, a1, a2;
        private double x1, x2, y1, y2;

        public static Biquad LowPass(double frequency, double rate) {
            Prepare(frequency, rate, out double cosine, out double alpha, out double a0);

            return new Biquad {
                b0 = (1 - cosine) / 2 / a0,
                b1 = (1 - cosine) / a0,
                b2 = (1 - cosine) / 2 / a0,
                a1 = -2 * cosine / a0,
                a2 = (1 - alpha) / a0
            };
        }

        public static Biquad HighPass(double frequency, double rate) {
            Prepare(frequency, rate, out double cosine, out double alpha, out double a0);

            return new Biquad {
                b0 = (1 + cosine) / 2 / a0,
                b1 = -(1 + cosine) / a0,
                b2 = (1 + cosine) / 2 / a0,
                a1 = -2 * cosine / a0,
                a2 = (1 - alpha) / a0
            };
        }

        private static void Prepare(double frequency, double rate, out double cosine, out double alpha, out double a0) {
            double w0 = 2 * Math.PI * Math.Clamp(frequency, 10, rate * 0.45) / rate;
            cosine = Math.Cos(w0);
            alpha = Math.Sin(w0) / Math.Sqrt(2.0);
            a0 = 1 + alpha;
        }

        public double Process(double x) {
            double y = b0 * x + b1 * x1 + b2 * x2 - a1 * y1 - a2 * y2;

            x2 = x1;
            x1 = x;
            y2 = y1;
            y1 = y;

            return y;
        }
    }
}
