using Diyokee;

// Correctness harness for Classes/BeatGrid.cs. Run by hand, not part of Diyokee.sln:
//
//     dotnet run --project tools/gridtest/gridtest.csproj
//
// It compiles the real Classes/BeatGrid.cs, so it cannot drift from what ships. No BASS, no audio
// and no I/O, so it finishes in well under a second anywhere.
//
// Check 1 is the important one and was written FIRST, against the old GenerateBeatMarkers, before
// any of it was replaced: a one-anchor grid has to reproduce the legacy BPM + DownbeatAt grid bit
// for bit. Everything phase 1 claims rests on that, because phase 1's whole promise is that the app
// behaves identically when it lands.

internal static class GridTest {
    static int failures;

    static void Main() {
        Console.WriteLine("BeatGrid harness\n");

        LegacyEquivalence();
        Extrapolation();
        MultiSegmentPositions();
        AdvanceWithinSegment();
        AdvanceAcrossSegments();
        AdvanceRoundTrip();
        Searches();
        TempoLookup();
        BarPhase();
        Degenerate();

        Console.WriteLine(failures == 0
            ? "\nAll checks passed."
            : $"\n{failures} CHECK(S) FAILED.");
        Environment.Exit(failures == 0 ? 0 : 1);
    }

    // ------------------------------------------------------------------ the legacy grid

    // Verbatim copy of Player.GenerateBeatMarkers as it stood before BeatGrid existed. Kept here
    // deliberately: the point of check 1 is to compare against the ORIGINAL, so this must not be
    // "tidied" to match the new code. If it is ever changed, check 1 stops proving anything.
    static List<(double X, double Seconds)> LegacyMarkers(double downbeatAt, double bpm, double duration, double secondsToPosX) {
        if(downbeatAt == -1) return [];

        double beatsPerSecond = bpm / 60.0;
        double secondsPerBeat = 1.0 / beatsPerSecond;

        List<(double X, double Seconds)> markers = new();
        double t1 = downbeatAt;
        double t2 = t1 - secondsPerBeat;
        while(t1 < duration || t2 >= 0) {
            if(t1 < duration) {
                markers.Add((t1 * secondsToPosX, t1));
                t1 += secondsPerBeat;
            }
            if(t2 >= 0) {
                markers.Insert(0, (t2 * secondsToPosX, t2));
                t2 -= secondsPerBeat;
            }
        }
        return markers;
    }

    static void LegacyEquivalence() {
        Console.WriteLine("[1] One-anchor grid reproduces the legacy grid bit for bit");

        (double downbeat, double bpm, double duration)[] cases = [
            (0.517,   128.0, 361.0),     // ordinary house track
            (0.0,     120.0, 240.0),     // downbeat exactly at zero
            (11.9317, 174.03, 407.55),   // awkward tempo, downbeat well into the track
            (3.25,     90.0,  12.0),     // very short track
            (0.04,    128.0, 361.0),     // downbeat before the first backward beat
        ];

        foreach(var c in cases) {
            const double toX = 37.4;
            var legacy = LegacyMarkers(c.downbeat, c.bpm, c.duration, toX);
            var grid = new BeatGrid([new BeatGrid.Anchor(c.downbeat, c.bpm, true)], c.duration, toX);

            bool ok = legacy.Count == grid.Beats.Count;
            if(ok) {
                for(int i = 0; i < legacy.Count; i++) {
                    // BitConverter, not a tolerance: "identical" has to mean identical, or the
                    // check quietly degrades into "close enough" and stops catching a drift.
                    if(BitConverter.DoubleToInt64Bits(legacy[i].Seconds) != BitConverter.DoubleToInt64Bits(grid.Beats[i].Seconds)
                       || BitConverter.DoubleToInt64Bits(legacy[i].X) != BitConverter.DoubleToInt64Bits(grid.Beats[i].X)) {
                        ok = false;
                        Console.WriteLine($"      first difference at beat {i}: legacy {legacy[i].Seconds:R} / grid {grid.Beats[i].Seconds:R}");
                        break;
                    }
                }
            }

            Report(ok, $"bpm {c.bpm}, downbeat {c.downbeat}, {legacy.Count} vs {grid.Beats.Count} beats");
        }

        // And the legacy "no downbeat" state, which produced no markers at all.
        var none = new BeatGrid([], 300, 1);
        Report(none.IsEmpty && none.Beats.Count == 0, "no anchors produces an empty grid");
    }

    static void Extrapolation() {
        Console.WriteLine("\n[2] Beats extrapolate backwards from the first anchor, down to zero but not past it");

        var grid = new BeatGrid([new BeatGrid.Anchor(10.0, 120.0, true)], 30.0, 1);
        Report(grid.Beats[0].Seconds >= 0, $"first beat {grid.Beats[0].Seconds:F3} is not negative");
        Report(grid.Beats[0].Seconds < 0.5, $"first beat {grid.Beats[0].Seconds:F3} is within one beat of zero");

        int atAnchor = grid.IndexAtOrBefore(10.0);
        Report(Near(grid.Beats[atAnchor].Seconds, 10.0), "a beat lands exactly on the anchor");
        Report(grid.Beats[^1].Seconds < 30.0, $"last beat {grid.Beats[^1].Seconds:F3} is inside the track");
    }

    static void MultiSegmentPositions() {
        Console.WriteLine("\n[3] Each segment steps at its own tempo");

        // 120 BPM from 0, then 90 BPM from 10s. 0.5s per beat, then 0.666..s per beat.
        var grid = new BeatGrid([
            new BeatGrid.Anchor(0.0, 120.0, true),
            new BeatGrid.Anchor(10.0, 90.0, true),
        ], 20.0, 1);

        Report(Near(grid.Beats[1].Seconds, 0.5), "second beat at 0.5s (120 BPM)");
        Report(Near(grid.Beats[20].Seconds, 10.0), "beat 20 lands on the anchor at 10s");
        Report(Near(grid.Beats[21].Seconds, 10.0 + 2.0 / 3.0), "the beat after it steps at 90 BPM");

        // The last beat of a segment may fall short of the next anchor. That short beat at the join
        // is what a tempo reset MEANS, and is correct - it is the bend operation that avoids it.
        var uneven = new BeatGrid([
            new BeatGrid.Anchor(0.0, 120.0, true),
            new BeatGrid.Anchor(1.2, 120.0, true),
        ], 3.0, 1);
        var seconds = uneven.Beats.Select(b => b.Seconds).ToArray();
        Report(seconds.Contains(1.0) && seconds.Contains(1.2), "a short beat at the join is kept, not swallowed");
        Report(seconds.All(s => s < 3.0), "no beat past the end of the track");
    }

    static void AdvanceWithinSegment() {
        Console.WriteLine("\n[4] Advance inside one segment");

        var grid = new BeatGrid([new BeatGrid.Anchor(0.0, 120.0, true)], 600.0, 1);

        foreach(int n in new[] { 1, 4, 8, 32 }) {
            Report(Near(grid.Advance(10.0, n), 10.0 + n * 0.5), $"+{n} beats");
            Report(Near(grid.Advance(10.0, -n), 10.0 - n * 0.5), $"-{n} beats");
        }
        Report(Near(grid.Advance(10.0, 0), 10.0), "zero beats is a no-op");
    }

    static void AdvanceAcrossSegments() {
        Console.WriteLine("\n[5] Advance across tempo changes");

        // 120 BPM (0.5s/beat) to 10s, then 60 BPM (1.0s/beat).
        var grid = new BeatGrid([
            new BeatGrid.Anchor(0.0, 120.0, true),
            new BeatGrid.Anchor(10.0, 60.0, true),
        ], 600.0, 1);

        // From 9s: 2 beats reaches 10s, the remaining 2 cost 1.0s each.
        Report(Near(grid.Advance(9.0, 4), 12.0), "forwards over one boundary");

        // Backwards from 12s: 2 beats back to 10s, then 2 more at 0.5s each.
        Report(Near(grid.Advance(12.0, -4), 9.0), "backwards over one boundary");

        var three = new BeatGrid([
            new BeatGrid.Anchor(0.0, 120.0, true),
            new BeatGrid.Anchor(10.0, 60.0, true),
            new BeatGrid.Anchor(14.0, 240.0, true),
        ], 600.0, 1);

        // From 9s: 2 beats to 10s, 4 beats to 14s, then 2 at 0.25s.
        Report(Near(three.Advance(9.0, 8), 14.5), "forwards over two boundaries");
        Report(Near(three.Advance(14.5, -8), 9.0), "backwards over two boundaries");

        // Past the open ends, the outermost tempo just keeps going.
        Report(Near(three.Advance(1.0, -8), -3.0), "off the front extrapolates at the first tempo");
    }

    static void AdvanceRoundTrip() {
        Console.WriteLine("\n[6] Advance round-trips");

        var grid = new BeatGrid([
            new BeatGrid.Anchor(0.31, 128.0, true),
            new BeatGrid.Anchor(37.9, 131.4, false),
            new BeatGrid.Anchor(122.05, 127.2, true),
        ], 300.0, 1);

        foreach(double from in new[] { 5.0, 37.9, 60.0, 122.05, 200.0 }) {
            foreach(int n in new[] { 1, 4, 8, 16, 64 }) {
                double there = grid.Advance(from, n);
                double back = grid.Advance(there, -n);
                Report(Near(back, from, 1e-9), $"from {from}s, {n} beats out and back");
            }
        }
    }

    static void Searches() {
        Console.WriteLine("\n[7] Binary searches agree with a linear scan");

        var grid = new BeatGrid([
            new BeatGrid.Anchor(0.517, 128.0, true),
            new BeatGrid.Anchor(90.2, 132.0, false),
        ], 361.0, 1);

        var all = grid.Beats;
        var rnd = new Random(1234);
        bool atOk = true, nearOk = true;

        for(int i = 0; i < 20000; i++) {
            double t = rnd.NextDouble() * 365.0 - 2.0;

            int expectedAt = -1;
            for(int j = 0; j < all.Count; j++) if(all[j].Seconds <= t) expectedAt = j; else break;
            if(grid.IndexAtOrBefore(t) != expectedAt) { atOk = false; break; }

            int expectedNear = -1;
            double best = double.MaxValue;
            for(int j = 0; j < all.Count; j++) {
                double d = Math.Abs(all[j].Seconds - t);
                if(d < best) { best = d; expectedNear = j; }
            }
            if(grid.NearestBeatIndex(t) != expectedNear) { nearOk = false; break; }
        }

        Report(atOk, "IndexAtOrBefore over 20000 random positions");
        Report(nearOk, "NearestBeatIndex over 20000 random positions");
        Report(grid.IndexAtOrBefore(-1.0) == -1, "before the first beat reports -1");
        Report(grid.NearestBeatIndex(-1.0) == 0, "nearest to a position before the grid is the first beat");
    }

    static void TempoLookup() {
        Console.WriteLine("\n[8] TempoAt reports the segment's tempo");

        var grid = new BeatGrid([
            new BeatGrid.Anchor(0.0, 120.0, true),
            new BeatGrid.Anchor(10.0, 90.0, true),
            new BeatGrid.Anchor(20.0, 150.0, true),
        ], 30.0, 1);

        Report(Near(grid.TempoAt(-5.0), 120.0), "before the first anchor uses the first tempo");
        Report(Near(grid.TempoAt(5.0), 120.0), "inside the first segment");
        Report(Near(grid.TempoAt(10.0), 90.0), "exactly on an anchor takes the new tempo");
        Report(Near(grid.TempoAt(25.0), 150.0), "inside the last segment");
    }

    static void BarPhase() {
        Console.WriteLine("\n[9] The bar counter runs continuously and restarts at a downbeat anchor");

        var grid = new BeatGrid([
            new BeatGrid.Anchor(0.0, 120.0, true),
            new BeatGrid.Anchor(10.5, 120.0, true),      // downbeat: restarts the bar
            new BeatGrid.Anchor(20.5, 120.0, false),     // not a downbeat: bar carries on
        ], 30.0, 1);

        int atReset = grid.IndexAtOrBefore(10.5);
        Report(grid.Beats[atReset].IndexInBar == 0 && grid.Beats[atReset].IsDownbeat,
               "a downbeat anchor restarts the bar");

        int atCarry = grid.IndexAtOrBefore(20.5);
        int before = grid.Beats[atCarry - 1].IndexInBar;
        Report(grid.Beats[atCarry].IndexInBar == (before + 1) % BeatGrid.BeatsPerBar,
               "a non-downbeat anchor does not");

        Report(grid.Beats.Count(b => b.IsDownbeat) > 1, "downbeats are marked throughout");
        Report(grid.Beats.All(b => b.IndexInBar >= 0 && b.IndexInBar < BeatGrid.BeatsPerBar),
               "every beat has a valid bar position");
    }

    static void Degenerate() {
        Console.WriteLine("\n[10] Degenerate input does not hang or throw");

        Report(new BeatGrid([], 300, 1).IsEmpty, "no anchors");
        Report(new BeatGrid([new BeatGrid.Anchor(0, 120, true)], 0, 1).IsEmpty, "zero-length track");

        // A zero BPM makes the step infinite. The old code emitted one marker and stopped; anything
        // that steps by zero instead would spin forever building an infinite list.
        var zero = new BeatGrid([new BeatGrid.Anchor(5.0, 0.0, true)], 30.0, 1);
        Report(zero.Beats.Count == 1, $"zero BPM yields one beat, got {zero.Beats.Count}");

        var infinite = new BeatGrid([new BeatGrid.Anchor(5.0, double.PositiveInfinity, true)], 30.0, 1);
        Report(infinite.Beats.Count == 1, $"infinite BPM yields one beat, got {infinite.Beats.Count}");

        // Anchors are sorted on the way in, so an out-of-order list is not a special case.
        var unsorted = new BeatGrid([
            new BeatGrid.Anchor(20.0, 90.0, true),
            new BeatGrid.Anchor(0.0, 120.0, true),
        ], 30.0, 1);
        var s = unsorted.Beats.Select(b => b.Seconds).ToArray();
        Report(s.SequenceEqual(s.OrderBy(x => x)), "unsorted anchors still produce an ascending grid");

        var pastEnd = new BeatGrid([
            new BeatGrid.Anchor(0.0, 120.0, true),
            new BeatGrid.Anchor(500.0, 90.0, true),
        ], 30.0, 1);
        Report(pastEnd.Beats.All(b => b.Seconds < 30.0), "an anchor past the end contributes no beats");

        var tight = new BeatGrid([
            new BeatGrid.Anchor(0.0, 120.0, true),
            new BeatGrid.Anchor(0.1, 120.0, true),
        ], 5.0, 1);
        Report(tight.Beats.Count > 0, "anchors closer together than one beat");

        var single = new BeatGrid([new BeatGrid.Anchor(0.0, 120.0, true)], 5.0, 1);
        Report(Near(single.Advance(1.0, 3), 2.5), "Advance with one anchor");
        Report(Near(new BeatGrid([], 5, 1).Advance(1.0, 3), 1.0), "Advance with no anchors and no tempo is a no-op");

        // A track analysed to a BPM but no downbeat. It has to have no beat markers - nothing says
        // where they fall - while loops and jumps still work off the nominal tempo.
        var noDownbeat = BeatGrid.FromFile(new DFile { BPM = 120, DownbeatAt = -1, Duration = 300 }, 1);
        Report(noDownbeat.IsEmpty, "BPM without a downbeat produces no beat markers");
        Report(Near(noDownbeat.Advance(10.0, 4), 12.0), "...but Advance still uses the nominal tempo");
        Report(Near(noDownbeat.TempoAt(10.0), 120.0), "...and TempoAt reports it");
        Report(Near(noDownbeat.NearestBeat(10.0), 10.0), "...and NearestBeat leaves the position alone");
    }

    // ------------------------------------------------------------------

    static bool Near(double a, double b, double tolerance = 1e-9) => Math.Abs(a - b) < tolerance;

    static void Report(bool ok, string what) {
        Console.WriteLine($"    {(ok ? "ok  " : "FAIL")}  {what}");
        if(!ok) failures++;
    }
}
