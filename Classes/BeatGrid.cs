namespace Diyokee;

// A track's beats as an ordered list of tempo anchors rather than one BPM and one downbeat, so a
// track that drifts - a live recording, a tape transfer, a set ripped from vinyl - can be gridded
// correctly end to end.
//
// This describes where the beats are. It does not move any audio: nothing here time-stretches
// anything and ScratchEngine is not involved. Whether playback should later be warped onto a
// uniform grid is a separate stage that would consume this, and is deliberately not decided yet.
//
// Segment i runs from anchors[i].Position until the next anchor (or the end of the track) at
// anchors[i].BPM. Before the first anchor the beats extrapolate backwards at the first anchor's
// tempo, which is what the old GenerateBeatMarkers did either side of DownbeatAt.
//
// A track with no anchors is treated as a single synthetic anchor at DownbeatAt, so the legacy
// BPM + DownbeatAt pair is not a second code path - it is the one-anchor case of this one. Check 1
// of tools/gridtest is the standing proof that it reproduces the old grid exactly.
public sealed class BeatGrid {
    public readonly record struct Anchor(double Position, double BPM, bool IsDownbeat);

    // X is the pixel offset the waveform draws at, carried alongside Seconds because every consumer
    // needed both and computing it twice invited them to disagree.
    public readonly record struct Beat(double Seconds, double X, int IndexInBar, bool IsDownbeat);

    public const int BeatsPerBar = 4;

    private readonly List<Anchor> anchors;
    private readonly Beat[] beats;

    // Tempo to fall back on when there are no anchors at all. A track can have a detected BPM and
    // no downbeat - analysis finds them separately - and that case still has to answer "how long is
    // a beat" for loops and jumps, while producing NO beat markers, because there is nothing to say
    // where they would fall. An anchorless grid used to be a no-op here, which silently gave those
    // tracks a zero-length loop.
    private readonly double nominalBPM;

    public IReadOnlyList<Beat> Beats => beats;
    public bool IsEmpty => beats.Length == 0;

    // Index of the first downbeat within Beats. The legacy VU colour alternates on this, and the
    // old code recomputed it with a FindIndex over the whole list on every read.
    public int DownbeatIndex { get; }

    public BeatGrid(IEnumerable<Anchor> anchors, double duration, double secondsToPosX, double nominalBPM = 0) {
        this.anchors = [.. anchors.OrderBy(a => a.Position)];
        this.nominalBPM = nominalBPM;
        beats = Generate(duration, secondsToPosX);
        DownbeatIndex = Math.Max(0, Array.FindIndex(beats, b => b.IsDownbeat));
    }

    // The one place that knows how a DFile becomes a grid. Falls back to the BPM + DownbeatAt pair
    // when the track has no anchors of its own, which is every track until one is edited.
    public static BeatGrid FromFile(DFile file, double secondsToPosX) {
        List<Anchor> anchors = [];

        if(file.DownbeatAt >= 0) anchors.Add(new Anchor(file.DownbeatAt, file.BPM, true));

        return new BeatGrid(anchors, file.Duration, secondsToPosX, file.BPM);
    }

    // Beats are accumulated by repeated addition rather than computed as start + k * step. That is
    // what GenerateBeatMarkers did, and repeated addition drifts in the last bits, so anything else
    // would produce a grid that is very slightly different from the one this replaces - and the
    // whole point of the first phase is that nothing moves.
    private Beat[] Generate(double duration, double secondsToPosX) {
        if(anchors.Count == 0) return [];

        List<double> before = [];
        List<double> forward = [];

        // Backwards from the first anchor, at its tempo, down to the start of the track.
        double back = SecondsPerBeat(0);
        if(back > 0 && !double.IsInfinity(back)) {
            for(double t = anchors[0].Position - back; t >= 0; t -= back) before.Add(t);
        }

        // Then each segment forwards, at its own tempo, up to wherever the next one takes over.
        for(int i = 0; i < anchors.Count; i++) {
            double step = SecondsPerBeat(i);
            double end = Math.Min(SegmentEnd(i), duration);

            if(!(step > 0) || double.IsInfinity(step)) {
                // A missing or nonsensical BPM. One beat at the anchor and move on, rather than
                // stepping by zero forever.
                if(anchors[i].Position < end) forward.Add(anchors[i].Position);
                continue;
            }

            for(double t = anchors[i].Position; t < end; t += step) forward.Add(t);
        }

        before.Reverse();

        Beat[] result = new Beat[before.Count + forward.Count];
        int bar = 0;
        int next = 0;   // the next anchor a forward beat might coincide with

        for(int i = 0; i < result.Length; i++) {
            double seconds = i < before.Count ? before[i] : forward[i - before.Count];

            // The bar counter runs continuously and restarts at every anchor marked as a downbeat.
            if(i >= before.Count && next < anchors.Count && seconds >= anchors[next].Position) {
                if(anchors[next].IsDownbeat) bar = 0;
                next++;
            }

            result[i] = new Beat(seconds, seconds * secondsToPosX, bar, bar == 0);
            bar = (bar + 1) % BeatsPerBar;
        }

        return result;
    }

    private double SecondsPerBeat(int segment)
        => 60.0 / (anchors.Count == 0 ? nominalBPM : anchors[segment].BPM);

    private double SegmentEnd(int segment)
        => segment + 1 < anchors.Count ? anchors[segment + 1].Position : double.PositiveInfinity;

    private double SegmentStart(int segment)
        => segment > 0 ? anchors[segment].Position : double.NegativeInfinity;

    // Index of the segment containing `seconds`. Everything before the first anchor belongs to
    // segment 0, whose tempo is what the backwards extrapolation uses.
    private int SegmentAt(double seconds) {
        int lo = 0, hi = anchors.Count - 1, result = 0;
        while(lo <= hi) {
            int mid = lo + (hi - lo) / 2;
            if(anchors[mid].Position <= seconds) {
                result = mid;
                lo = mid + 1;
            } else {
                hi = mid - 1;
            }
        }
        return result;
    }

    public double TempoAt(double seconds) => anchors.Count == 0 ? nominalBPM : anchors[SegmentAt(seconds)].BPM;

    // Moves `beats` beats from `seconds`, signed, crossing tempo changes correctly. This is what
    // makes an 8-beat loop that straddles a tempo change land on a beat instead of drifting -
    // `start + beats / beatsPerSecond` cannot, because there is no single beatsPerSecond.
    public double Advance(double seconds, double beats) {
        if(beats == 0) return seconds;

        // No anchors: there is one tempo and it applies everywhere, so there are no segments to walk.
        if(anchors.Count == 0) {
            double flat = 60.0 / nominalBPM;
            return flat > 0 && !double.IsInfinity(flat) ? seconds + beats * flat : seconds;
        }

        int direction = beats < 0 ? -1 : 1;
        double remaining = Math.Abs(beats);
        double t = seconds;
        int segment = SegmentAt(t);

        while(remaining > 0) {
            double step = SecondsPerBeat(segment);
            if(!(step > 0)) return t;                        // no usable tempo; refuse to move

            double boundary = direction > 0 ? SegmentEnd(segment) : SegmentStart(segment);
            double available = Math.Abs(boundary - t) / step;

            // Deliberately `!(available < remaining)`, so an infinite or NaN gap - the open ends of
            // the first and last segments - finishes here rather than walking off the list.
            if(!(available < remaining)) return t + direction * remaining * step;

            t = boundary;
            remaining -= available;
            segment += direction;

            if(segment < 0 || segment >= anchors.Count) return t + direction * remaining * step;
        }

        return t;
    }

    // Index of the last beat at or before `seconds`, or -1 if the playhead is before the first.
    public int IndexAtOrBefore(double seconds) {
        int lo = 0, hi = beats.Length - 1, result = -1;
        while(lo <= hi) {
            int mid = lo + (hi - lo) / 2;
            if(beats[mid].Seconds <= seconds) {
                result = mid;
                lo = mid + 1;
            } else {
                hi = mid - 1;
            }
        }
        return result;
    }

    // The nearest beat is always either the one at or before the playhead or the one after it, so
    // this costs a binary search and one comparison - where the code it replaces scanned every
    // marker of the track.
    public int NearestBeatIndex(double seconds) {
        if(beats.Length == 0) return -1;

        int at = IndexAtOrBefore(seconds);
        if(at < 0) return 0;
        if(at + 1 >= beats.Length) return at;

        return seconds - beats[at].Seconds <= beats[at + 1].Seconds - seconds ? at : at + 1;
    }

    public double NearestBeat(double seconds) {
        int i = NearestBeatIndex(seconds);
        return i < 0 ? seconds : beats[i].Seconds;
    }
}
