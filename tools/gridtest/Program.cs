using Diyokee;

// Correctness harness for Classes/BeatGrid.cs and Classes/BeatAlign.cs. Run by hand, not part of
// Diyokee.sln:
//
//     dotnet run --project tools/gridtest/gridtest.csproj
//
// It compiles the real sources, so it cannot drift from what ships. No BASS, no audio and no I/O,
// so it finishes in well under a second anywhere.
//
// Group 18 drives automatic gridding on synthesised onsets - a drifting track, a tempo change, a
// breakdown with no drums in it, stray onsets, a mis-detected BPM - which is the only way to test
// it repeatably: the same run over a real file depends on the file.
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
        Persistence();
        Warp();
        Editing();
        BeatDragging();
        Pinning();
        ReferenceIsImmovable();
        CuePointsSurvive();
        OnsetDetection();
        AutomaticGridding();

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

        // The beats extrapolated BEFORE the downbeat have to be phased backwards from it, or the
        // bar lines land on the wrong beat and a stray downbeat appears a few beats early. The
        // count of backward beats is what used to decide it, so try every remainder.
        foreach(int backwardBeats in new[] { 1, 2, 3, 4, 5, 6, 7, 8 }) {
            double spb = 0.5;                               // 120 BPM
            double downbeat = backwardBeats * spb + 0.01;   // just past a whole number of beats back
            var g = new BeatGrid([new BeatGrid.Anchor(downbeat, 120.0, true)], 60.0, 1);

            int at = g.IndexAtOrBefore(downbeat);
            bool ok = g.Beats[at].IsDownbeat && at == backwardBeats;

            // Every downbeat must be a whole number of bars from the real one, in both directions.
            for(int i = 0; i < g.Beats.Count && ok; i++) {
                if(g.Beats[i].IsDownbeat && Math.Abs(i - at) % BeatGrid.BeatsPerBar != 0) ok = false;
            }

            Report(ok, $"{backwardBeats} beat(s) before the downbeat: bar lines stay in phase with it");
        }
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

    static void Persistence() {
        Console.WriteLine("\n[11] The traps in persisting anchors (plan section 9)");

        // FromFile prefers a track's own anchors; the BPM + DownbeatAt pair is only the fallback.
        var gridded = new DFile { BPM = 120, DownbeatAt = 0.0, Duration = 60 };
        gridded.BeatGridMarkers.Add(new DFile.BeatGridMarker { Id = 1, Position = 0.0, BPM = 60, IsDownbeat = true });
        var g = BeatGrid.FromFile(gridded, 1);
        Report(Near(g.TempoAt(10.0), 60.0), "anchors win over BPM/DownbeatAt when present");

        var ungridded = new DFile { BPM = 120, DownbeatAt = 0.0, Duration = 60 };
        Report(Near(BeatGrid.FromFile(ungridded, 1).TempoAt(10.0), 120.0), "and the pair is used when they are not");

        // Clone must deep-copy, or Cancel in the dialog does not cancel.
        var original = new DFile { BPM = 120, DownbeatAt = 0.0, Duration = 60 };
        original.BeatGridMarkers.Add(new DFile.BeatGridMarker { Id = 7, Position = 5.0, BPM = 128, IsDownbeat = true });
        var copy = (DFile)original.Clone();
        copy.BeatGridMarkers[0].Position = 99.0;
        copy.BeatGridMarkers.Add(new DFile.BeatGridMarker { Position = 20.0, BPM = 100 });
        Report(Near(original.BeatGridMarkers[0].Position, 5.0), "Clone deep-copies anchors: editing the clone leaves the original alone");
        Report(original.BeatGridMarkers.Count == 1, "...including additions");

        // ApplyEditsFrom diffs by Id: an edited anchor keeps its row, a new one is added, a
        // dropped one is removed. Replacing the list instead would churn every id on every save.
        var tracked = new DFile { Duration = 60 };
        tracked.BeatGridMarkers.Add(new DFile.BeatGridMarker { Id = 1, Position = 1.0, BPM = 120, IsDownbeat = true });
        tracked.BeatGridMarkers.Add(new DFile.BeatGridMarker { Id = 2, Position = 2.0, BPM = 120, IsDownbeat = false });

        var edited = new DFile { Duration = 60 };
        edited.BeatGridMarkers.Add(new DFile.BeatGridMarker { Id = 1, Position = 1.5, BPM = 130, IsDownbeat = true });
        edited.BeatGridMarkers.Add(new DFile.BeatGridMarker { Id = 0, Position = 9.0, BPM = 140, IsDownbeat = false });

        var kept = tracked.BeatGridMarkers[0];
        tracked.ApplyEditsFrom(edited);

        Report(tracked.BeatGridMarkers.Count == 2, $"anchor 2 was dropped, got {tracked.BeatGridMarkers.Count} anchors");
        Report(ReferenceEquals(tracked.BeatGridMarkers.FirstOrDefault(m => m.Id == 1), kept),
               "an edited anchor is the same object, so EF keeps the row");
        Report(Near(kept.Position, 1.5) && Near(kept.BPM, 130.0), "...with its values updated");
        Report(tracked.BeatGridMarkers.Any(m => m.Id == 0 && Near(m.Position, 9.0)), "a new anchor is added");
        Report(!tracked.BeatGridMarkers.Any(m => m.Id == 2), "a removed anchor is gone");

        // The failure mode the Include exists to prevent: diffing against an empty collection,
        // which is what EF hands back when the anchors were not loaded.
        var notIncluded = new DFile { Duration = 60 };
        notIncluded.ApplyEditsFrom(edited);
        Report(notIncluded.BeatGridMarkers.Count == 2, "diffing into an empty list adds rather than throws");
    }

    static void Warp() {
        Console.WriteLine("\n[12] The warp map - source time to a quantised grid");

        // An unedited track: one anchor at the nominal tempo. The warp MUST be the identity, or
        // every existing track would change speed the moment this shipped.
        var plain = BeatGrid.FromFile(new DFile { BPM = 128, DownbeatAt = 0.5, Duration = 300 }, 1);
        Report(!plain.IsWarped, "one anchor at the nominal tempo is not warped");
        Report(Near(plain.PlaybackRateAt(100.0), 1.0), "...its playback rate is exactly 1");
        Report(Near(plain.ToGridTime(100.0), 100.0), "...and grid time equals source time");

        // A track that drifts: 128 nominal, but the second half was actually recorded at 127.
        var drifting = new BeatGrid([
            new BeatGrid.Anchor(0.0, 128.0, true),
            new BeatGrid.Anchor(60.0, 127.0, false),
        ], 300.0, 1, 128.0);

        Report(drifting.IsWarped, "a segment off the nominal tempo is warped");
        Report(Near(drifting.PlaybackRateAt(10.0), 1.0), "the on-tempo segment plays at 1x");
        Report(Near(drifting.PlaybackRateAt(100.0), 128.0 / 127.0), "the slow segment plays at 128/127");

        // The first anchor is pinned, so correcting drift does not shift the whole track.
        Report(Near(drifting.ToGridTime(0.0), 0.0), "the first anchor is pinned");
        Report(Near(drifting.ToGridTime(30.0), 30.0), "...and the on-tempo segment is unmoved");

        // 40s of a 127 BPM segment is 40 * 127/128 of grid time - it takes LESS grid time,
        // because it is played slightly faster to bring it up to tempo.
        Report(Near(drifting.ToGridTime(100.0), 60.0 + 40.0 * 127.0 / 128.0), "the slow segment is compressed onto the grid");
        Report(drifting.ToGridTime(100.0) < 100.0, "...so grid time runs behind source time there");

        // Round trip, which is what guarantees the warped VIEW and the eventual audio warp agree.
        var rnd = new Random(99);
        bool ok = true;
        var messy = new BeatGrid([
            new BeatGrid.Anchor(0.31, 128.0, true),
            new BeatGrid.Anchor(37.9, 131.4, false),
            new BeatGrid.Anchor(122.05, 124.2, true),
            new BeatGrid.Anchor(200.0, 128.0, false),
        ], 300.0, 1, 128.0);

        for(int i = 0; i < 20000; i++) {
            double t = rnd.NextDouble() * 320.0 - 10.0;
            if(!Near(messy.FromGridTime(messy.ToGridTime(t)), t, 1e-8)) { ok = false; break; }
        }
        Report(ok, "ToGridTime and FromGridTime round-trip over 20000 positions");

        // Monotonic: the map must never fold back on itself or the waveform would draw inside out.
        bool monotonic = true;
        double previous = double.NegativeInfinity;
        for(double t = -5; t < 300; t += 0.05) {
            double g = messy.ToGridTime(t);
            if(g <= previous) { monotonic = false; break; }
            previous = g;
        }
        Report(monotonic, "the map is strictly increasing across every segment");

        // A grid with beats but no usable nominal tempo must not divide by zero.
        var noTarget = new BeatGrid([new BeatGrid.Anchor(0.0, 120.0, true)], 60.0, 1, 0);
        Report(Near(noTarget.ToGridTime(10.0), 10.0), "no target tempo leaves time alone");
        Report(Near(noTarget.PlaybackRateAt(10.0), 1.0), "...and the rate stays 1");
    }

    static List<DFile.BeatGridMarker> Anchors(params (double pos, double bpm)[] a)
        => [.. a.Select((x, i) => new DFile.BeatGridMarker { Id = i + 1, Position = x.pos, BPM = x.bpm, IsDownbeat = i == 0 })];

    static void Editing() {
        Console.WriteLine("\n[13] Editing operations");

        // Bend: both beat counts are preserved, the neighbours do not move, and the tempo moves
        // the right way. 120 BPM either side of an anchor at 10s, 20 beats each way.
        var a = Anchors((0.0, 120.0), (10.0, 120.0), (20.0, 120.0));
        var bend = BeatGrid.BeginBend(a, 1);
        Report(Near(bend.PrevBeats, 20) && Near(bend.NextBeats, 20), "bend freezes the beat counts either side");

        BeatGrid.ApplyBend(a, bend, 10.5);
        Report(Near(a[0].Position, 0.0) && Near(a[2].Position, 20.0), "the neighbours do not move");
        Report(Near(a[1].Position, 10.5), "the dragged anchor moves");
        Report(Near(a[0].BPM, 60.0 * 20 / 10.5), "dragging right SLOWS the preceding segment");
        Report(a[0].BPM < 120.0, $"...to {a[0].BPM:F2}, below the original 120");
        Report(Near(a[1].BPM, 60.0 * 20 / 9.5), "and speeds up the following one");
        Report(a[1].BPM > 120.0, $"...to {a[1].BPM:F2}, above the original 120");

        // The beat counts really are preserved - that is what makes a bend safe.
        var g = new BeatGrid([.. a.Select(m => new BeatGrid.Anchor(m.Position, m.BPM, m.IsDownbeat))], 30.0, 1, 120);
        Report(Near(g.Advance(0.0, 20), 10.5, 1e-6), "20 beats from the start still lands on the bent anchor");
        Report(Near(g.Advance(10.5, 20), 20.0, 1e-6), "and 20 more still lands on the next one");

        // Dragging left does the opposite.
        var b = Anchors((0.0, 120.0), (10.0, 120.0), (20.0, 120.0));
        BeatGrid.ApplyBend(b, BeatGrid.BeginBend(b, 1), 9.5);
        Report(b[0].BPM > 120.0, "dragging left speeds the preceding segment up");

        // Clamped: an anchor can never cross or touch a neighbour.
        var c = Anchors((0.0, 120.0), (10.0, 120.0), (20.0, 120.0));
        var cb = BeatGrid.BeginBend(c, 1);
        BeatGrid.ApplyBend(c, cb, 500.0);
        Report(c[1].Position < 20.0 && c[1].Position > 0.0, $"a drag past the next anchor is clamped to {c[1].Position:F2}");
        BeatGrid.ApplyBend(c, cb, -500.0);
        Report(c[1].Position > 0.0, $"and past the previous one to {c[1].Position:F2}");

        // First and last anchors have only one side to stretch.
        var d = Anchors((0.0, 120.0), (10.0, 120.0));
        BeatGrid.ApplyBend(d, BeatGrid.BeginBend(d, 0), 0.5);
        Report(Near(d[1].Position, 10.0), "bending the first anchor leaves the next one alone");
        Report(Near(d[0].BPM, 60.0 * 20 / 9.5), "...and re-times only the segment after it");

        var e = Anchors((0.0, 120.0), (10.0, 120.0));
        double tailBpm = e[1].BPM;
        BeatGrid.ApplyBend(e, BeatGrid.BeginBend(e, 1), 10.5);
        Report(Near(e[1].BPM, tailBpm), "bending the last anchor leaves its own tempo alone");
        Report(Near(e[0].BPM, 60.0 * 20 / 10.5), "...and re-times only the segment before it");

        // Round trip.
        var f = Anchors((0.0, 120.0), (10.0, 120.0), (20.0, 120.0));
        var fb = BeatGrid.BeginBend(f, 1);
        BeatGrid.ApplyBend(f, fb, 10.7);
        BeatGrid.ApplyBend(f, fb, 10.0);
        Report(Near(f[0].BPM, 120.0, 1e-9) && Near(f[1].BPM, 120.0, 1e-9), "bending out and back restores the tempos");

        // Offsets.
        var sa = Anchors((1.0, 120.0), (10.0, 130.0));
        BeatGrid.ShiftAll(sa, 0.25);
        Report(Near(sa[0].Position, 1.25) && Near(sa[1].Position, 10.25), "ShiftAll moves every anchor");
        Report(Near(sa[0].BPM, 120.0) && Near(sa[1].BPM, 130.0), "...and leaves the tempos alone");

        var st = Anchors((1.0, 120.0), (10.0, 130.0), (20.0, 140.0));
        BeatGrid.ShiftTail(st, 1, 0.5);
        Report(Near(st[0].Position, 1.0), "ShiftTail leaves earlier anchors alone");
        Report(Near(st[1].Position, 10.5) && Near(st[2].Position, 20.5), "...and moves this one and every later one");

        // Insert keeps order and defaults to the tempo already in force, so dropping an anchor
        // changes nothing until it is edited.
        var ins = Anchors((0.0, 120.0), (20.0, 90.0));
        int at = BeatGrid.Insert(ins, 10.0, 120.0, false);
        Report(at == 1 && Near(ins[1].Position, 10.0), "Insert places the anchor in order");
        var before = new BeatGrid([new BeatGrid.Anchor(0, 120, true), new BeatGrid.Anchor(20, 90, true)], 40, 1, 120);
        var after = new BeatGrid([.. ins.Select(m => new BeatGrid.Anchor(m.Position, m.BPM, m.IsDownbeat))], 40, 1, 120);
        Report(before.Beats.Count == after.Beats.Count, "...and inserting at the running tempo does not move any beat");

        BeatGrid.Delete(ins, 1);
        Report(ins.Count == 2 && Near(ins[1].Position, 20.0), "Delete removes the anchor and the previous segment extends");

        BeatGrid.Delete(ins, 99);
        Report(ins.Count == 2, "deleting out of range is ignored");
    }

    // Dragging a beat line directly - the only gesture the grid editor has.
    static void BeatDragging() {
        Console.WriteLine("\n[15] Dragging a beat line");

        static (BeatGrid grid, List<DFile.BeatGridMarker> anchors) Build(params (double pos, double bpm)[] a) {
            var anchors = Anchors(a);
            var grid = new BeatGrid([.. anchors.Select(m => new BeatGrid.Anchor(m.Position, m.BPM, m.IsDownbeat))], 600.0, 1, 120.0);
            return (grid, anchors);
        }

        static BeatGrid Rebuild(List<DFile.BeatGridMarker> anchors)
            => new([.. anchors.Select(m => new BeatGrid.Anchor(m.Position, m.BPM, m.IsDownbeat))], 600.0, 1, 120.0);

        // Grabbing an ordinary beat pins it: an anchor appears there so the drag is LOCAL.
        var (grid, anchors) = Build((0.0, 120.0));
        var drag = grid.BeginBeatDrag(anchors, 10.0);
        Report(drag.IsValid, "an ordinary beat can be grabbed");
        Report(anchors.Count == 2, $"an anchor is created where it was grabbed, got {anchors.Count}");
        Report(Near(anchors[1].Position, 10.0), "...at that beat");

        BeatGrid.ApplyBeatDrag(anchors, drag, 10.5);
        Report(Near(anchors[0].Position, 0.0), "the previous anchor is pinned");
        Report(Near(anchors[0].BPM, 60.0 * 20 / 10.5), "the stretch back to it is re-timed to land the beat where it was dropped");
        Report(Near(Rebuild(anchors).Advance(0.0, 20), 10.5, 1e-9), "...and it really does land there");

        // THE point of pinning: a later drag must not disturb what is already aligned.
        var (g2, a2) = Build((0.0, 120.0));
        BeatGrid.ApplyBeatDrag(a2, g2.BeginBeatDrag(a2, 10.0), 10.5);   // fix beat 20
        double firstFix = a2[0].BPM;

        var g2b = Rebuild(a2);
        double laterBeat = g2b.Advance(10.5, 40);
        BeatGrid.ApplyBeatDrag(a2, g2b.BeginBeatDrag(a2, laterBeat), laterBeat + 0.4);

        Report(Near(a2[0].BPM, firstFix, 1e-9), "correcting a later beat leaves the earlier section's tempo alone");
        Report(Near(Rebuild(a2).Advance(0.0, 20), 10.5, 1e-6), "...so the beat fixed first is still where it was put");

        // Dragging a beat that is already an anchor moves that anchor and creates nothing.
        var (g3, a3) = Build((0.0, 120.0), (10.0, 120.0), (20.0, 120.0));
        int before = a3.Count;
        var anchorDrag = g3.BeginBeatDrag(a3, 10.0);
        Report(a3.Count == before, "grabbing an existing anchor does not create another");
        BeatGrid.ApplyBeatDrag(a3, anchorDrag, 10.5);
        Report(Near(a3[0].Position, 0.0) && Near(a3[2].Position, 20.0), "...the neighbours stay put");
        Report(a3[0].BPM < 120.0 && a3[1].BPM > 120.0, "...and both adjacent segments are re-timed");

        // Clamped so an anchor can never reach or cross a neighbour.
        var (g4, a4) = Build((0.0, 120.0), (10.0, 120.0), (20.0, 120.0));
        var d4 = g4.BeginBeatDrag(a4, 10.0);
        BeatGrid.ApplyBeatDrag(a4, d4, 5000.0);
        Report(a4[1].Position < 20.0 && a4[1].Position > 0.0, $"a drag past the next anchor is clamped to {a4[1].Position:F2}");

        // Fidgeting must leave no trace: drag a beat out and back and the anchor it created is
        // pruned, because an anchor whose tempo matches the one before it says nothing.
        var (g5, a5) = Build((0.0, 120.0));
        var d5 = g5.BeginBeatDrag(a5, 10.0);
        BeatGrid.ApplyBeatDrag(a5, d5, 10.9);
        BeatGrid.ApplyBeatDrag(a5, d5, 10.0);
        BeatGrid.PruneDrag(a5, d5);
        Report(a5.Count == 1, $"dragging out and back leaves no anchor behind, got {a5.Count}");
        Report(Near(a5[0].BPM, 120.0, 1e-9), "...and the tempo is exactly as it was");

        // A real correction is NOT pruned.
        var (g6, a6) = Build((0.0, 120.0));
        var d6b = g6.BeginBeatDrag(a6, 10.0);
        BeatGrid.ApplyBeatDrag(a6, d6b, 10.4);
        BeatGrid.PruneDrag(a6, d6b);
        Report(a6.Count == 2, "a genuine correction keeps its anchor");

        var (g8, a8) = Build((0.0, 120.0));
        Report(!g8.BeginBeatDrag([], 10.0).IsValid, "grabbing with no anchors at all is refused");
    }

    // Pinning: the answer to "everything up to here is already correct, do not touch it".
    static void Pinning() {
        Console.WriteLine("\n[16] Pins protect what is already aligned");

        static (BeatGrid grid, List<DFile.BeatGridMarker> anchors) Build(params (double pos, double bpm)[] a) {
            var anchors = Anchors(a);
            var grid = new BeatGrid([.. anchors.Select(m => new BeatGrid.Anchor(m.Position, m.BPM, m.IsDownbeat))], 600.0, 1, 120.0);
            return (grid, anchors);
        }

        static BeatGrid Rebuild(List<DFile.BeatGridMarker> anchors)
            => new([.. anchors.Select(m => new BeatGrid.Anchor(m.Position, m.BPM, m.IsDownbeat))], 600.0, 1, 120.0);

        // A pin changes nothing you can see or hear - it takes the tempo already in force.
        var (grid, anchors) = Build((0.0, 120.0));
        var beatsBefore = grid.Beats.Select(b => b.Seconds).ToArray();
        Report(grid.TogglePin(anchors, 16.0), "placing a reference adds an anchor");
        Report(anchors.Count == 2, "...as an anchor");

        var pinned = Rebuild(anchors);
        Report(pinned.Beats.Count == beatsBefore.Length, "a pin does not change the number of beats");
        Report(pinned.Beats.Select(b => b.Seconds).Zip(beatsBefore).All(p => Near(p.First, p.Second, 1e-9)),
               "...and does not move a single one");

        // THE point: drag a later beat and nothing before the pin may move.
        var beforeDrag = pinned.Beats.Where(b => b.Seconds <= 16.0).Select(b => b.Seconds).ToArray();
        double later = pinned.Advance(16.0, 32);
        BeatGrid.ApplyBeatDrag(anchors, pinned.BeginBeatDrag(anchors, later), later + 0.35);

        var after = Rebuild(anchors);
        var afterBeforePin = after.Beats.Where(b => b.Seconds <= 16.0 + 1e-9).Select(b => b.Seconds).ToArray();
        Report(afterBeforePin.Length == beforeDrag.Length, "the same beats still sit before the pin");
        Report(afterBeforePin.Zip(beforeDrag).All(p => Near(p.First, p.Second, 1e-9)),
               "not one beat before the pin moved");
        Report(!Near(after.Advance(16.0, 32), later, 1e-6), "...while the beat that was dragged did");

        // Without a pin, the same drag reaches all the way back - which is what the pin is for.
        var (g2, a2) = Build((0.0, 120.0));
        double later2 = g2.Advance(16.0, 32);
        BeatGrid.ApplyBeatDrag(a2, g2.BeginBeatDrag(a2, later2), later2 + 0.35);
        var after2 = Rebuild(a2);
        Report(!Near(after2.Beats[8].Seconds, g2.Beats[8].Seconds, 1e-9), "with no pin, an early beat does move");

        // A pin survives a later drag's prune. It looks exactly like a redundant anchor - same
        // tempo as the segment before it - which is why the prune has to be targeted.
        var (g3, a3) = Build((0.0, 120.0));
        g3.TogglePin(a3, 16.0);
        var g3b = Rebuild(a3);
        double later3 = g3b.Advance(16.0, 32);
        var drag3 = g3b.BeginBeatDrag(a3, later3);
        BeatGrid.ApplyBeatDrag(a3, drag3, later3 + 0.2);
        BeatGrid.PruneDrag(a3, drag3);
        Report(a3.Any(m => Near(m.Position, 16.0)), "the pin survives a later drag and its prune");

        // Double-clicking a pin removes it.
        var (g4, a4) = Build((0.0, 120.0));
        g4.TogglePin(a4, 16.0);
        Report(!Rebuild(a4).TogglePin(a4, 16.0), "removing a reference reports it");
        Report(a4.Count == 1, "...and it is gone");

        // The downbeat anchor is what the grid hangs from and cannot be unpinned away.
        var (g5, a5) = Build((0.0, 120.0));
        g5.TogglePin(a5, 0.0);
        Report(a5.Count == 1, "the first anchor cannot be removed");

        // A reference is flagged as one, which is what tells it apart from an anchor a drag left
        // behind - the two are drawn differently and only one of them is auto-prunable.
        var (g6, a6) = Build((0.0, 120.0));
        g6.TogglePin(a6, 16.0);
        var placed = a6.Single(m => Math.Abs(m.Position - 16.0) < 1e-9);
        Report(placed.IsReference, "a placed reference is flagged as one");

        var g6b = Rebuild(a6);
        double dragged = g6b.Advance(16.0, 8);
        var d6 = g6b.BeginBeatDrag(a6, dragged);
        Report(!a6.Single(m => Math.Abs(m.Position - dragged) < 1e-9).IsReference,
               "an anchor a drag leaves behind is not");

        // ...and a reference is never pruned, whatever its tempo happens to say.
        BeatGrid.PruneDrag(a6, d6);
        Report(a6.Any(m => Math.Abs(m.Position - 16.0) < 1e-9), "a reference survives a prune");

        // A reference is still DRAGGABLE. Immovable describes what it protects, not itself: moving
        // it re-times the two segments either side, and the anchor before it is the guard rail.
        var (g7, a7) = Build((0.0, 120.0));
        g7.TogglePin(a7, 16.0);
        var g7b = Rebuild(a7);
        var refDrag = g7b.BeginBeatDrag(a7, 16.0);
        Report(refDrag.IsValid, "a reference can be grabbed");

        BeatGrid.ApplyBeatDrag(a7, refDrag, 16.4);
        Report(Near(a7.Single(m => m.IsReference && Math.Abs(m.Position - 16.4) < 1e-9).Position, 16.4),
               "...and moved");
        Report(Near(a7[0].Position, 0.0), "...with the anchor before it left where it was");
    }

    // Reproduces the reported sequence exactly: set a reference, then drag a beat AFTER it, and
    // demand that NOTHING at or before the reference moved. A reference is meant to be an immovable
    // beat, so this is the property the whole feature rests on.
    static void ReferenceIsImmovable() {
        Console.WriteLine("\n[17] A reference is immovable");

        // A real track: analysis gives a BPM and a downbeat, and no anchors of its own.
        var file = new DFile { BPM = 120, DownbeatAt = 0.517, Duration = 300 };

        BeatGrid Grid() => BeatGrid.FromFile(file, 1);

        // 1. The user clicks + on a beat to make it a reference. This is what TogglePinAt does,
        //    including seeding the downbeat anchor the fallback grid was using.
        var anchors = file.BeatGridMarkers;
        double reference = Grid().Advance(0.517, 32);
        BeatGrid.Insert(anchors, file.DownbeatAt, file.BPM, true);
        Grid().TogglePin(anchors, reference);

        double[] beforeReference = [.. Grid().Beats.Where(b => b.Seconds <= reference + 1e-9).Select(b => b.Seconds)];
        Console.WriteLine($"      reference at {reference:F4}s, {beforeReference.Length} beats at or before it");

        // 2. The user drags a beat well after the reference.
        double target = Grid().Advance(reference, 32);
        var drag = Grid().BeginBeatDrag(anchors, target);
        BeatGrid.ApplyBeatDrag(anchors, drag, target + 0.30);
        BeatGrid.PruneDrag(anchors, drag);

        double[] afterEdit = [.. Grid().Beats.Where(b => b.Seconds <= reference + 1e-9).Select(b => b.Seconds)];

        bool sameCount = afterEdit.Length == beforeReference.Length;
        Report(sameCount, $"the same number of beats sits at or before the reference ({beforeReference.Length} vs {afterEdit.Length})");

        int moved = 0;
        double worst = 0;
        if(sameCount) {
            for(int i = 0; i < afterEdit.Length; i++) {
                double d = Math.Abs(afterEdit[i] - beforeReference[i]);
                if(d > 1e-9) {
                    moved++;
                    if(d > worst) worst = d;
                }
            }
        }
        Report(moved == 0, $"not one beat at or before the reference moved ({moved} moved, worst {worst * 1000:F3} ms)");

        // The intro. Beats extrapolated BACK from the downbeat are before every anchor, so nothing
        // may move them either - the first anchor is a guard rail like any other. This is the exact
        // shape of the reported "mess before the reference": a track whose downbeat is 8 s in has
        // ~17 beats behind it, and they all re-spaced.
        var intro = new DFile { BPM = 124, DownbeatAt = 8.183, Duration = 300 };
        var introAnchors = intro.BeatGridMarkers;
        BeatGrid.Insert(introAnchors, intro.DownbeatAt, intro.BPM, true);

        BeatGrid IntroGrid() => BeatGrid.FromFile(intro, 1);

        double[] introBefore = [.. IntroGrid().Beats.Where(b => b.Seconds < intro.DownbeatAt).Select(b => b.Seconds)];
        Console.WriteLine($"      {introBefore.Length} beats extrapolated before the downbeat at {intro.DownbeatAt}s");

        double introTarget = IntroGrid().Advance(intro.DownbeatAt, 32);
        var introDrag = IntroGrid().BeginBeatDrag(introAnchors, introTarget);
        BeatGrid.ApplyBeatDrag(introAnchors, introDrag, introTarget + 0.4);

        double[] introAfter = [.. IntroGrid().Beats.Where(b => b.Seconds < intro.DownbeatAt).Select(b => b.Seconds)];

        bool introCount = introAfter.Length == introBefore.Length;
        Report(introCount, $"the intro still has the same number of beats ({introBefore.Length} vs {introAfter.Length})");

        int introMoved = 0;
        double introWorst = 0;
        if(introCount) {
            for(int i = 0; i < introAfter.Length; i++) {
                double d = Math.Abs(introAfter[i] - introBefore[i]);
                if(d > 1e-9) { introMoved++; if(d > introWorst) introWorst = d; }
            }
        }
        Report(introMoved == 0, $"not one beat before the downbeat moved ({introMoved} moved, worst {introWorst * 1000:F1} ms)");

        // ...and it must not be PLAYED any differently either. Drawing the intro at the nominal
        // tempo while warping it at segment 0's rate is a picture and a sound disagreeing about
        // the same audio - visually correct, audibly wrong, which is exactly how it was reported.
        var introGrid = IntroGrid();
        Report(Near(introGrid.PlaybackRateAt(intro.DownbeatAt / 2), 1.0, 1e-12),
               $"the intro plays at 1.0x, got {introGrid.PlaybackRateAt(intro.DownbeatAt / 2):F6}");
        Report(Near(introGrid.PlaybackRateAt(0.0), 1.0, 1e-12), "...including the very start of the track");
        Report(Near(introGrid.ToGridTime(4.0), 4.0, 1e-12), "...and its grid time equals its source time");
        Report(Near(introGrid.FromGridTime(4.0), 4.0, 1e-12), "...both ways");

        // The correction itself is still applied, from the first anchor onward - the point is that
        // it starts THERE and not before.
        double insideCorrected = intro.DownbeatAt + 1.0;
        Report(!Near(introGrid.PlaybackRateAt(insideCorrected), 1.0, 1e-9),
               $"the corrected segment does play at {introGrid.PlaybackRateAt(insideCorrected):F4}x");

        // And the reference itself is exactly where it was put.
        Report(anchors.Any(a => Math.Abs(a.Position - reference) < 1e-9), "the reference itself has not moved");

        // 3. A second edit, further along, must not disturb the first correction either.
        double corrected = Grid().Advance(reference, 32);
        double[] beforeSecond = [.. Grid().Beats.Where(b => b.Seconds <= corrected + 1e-9).Select(b => b.Seconds)];

        double target2 = Grid().Advance(corrected, 32);
        var drag2 = Grid().BeginBeatDrag(anchors, target2);
        BeatGrid.ApplyBeatDrag(anchors, drag2, target2 - 0.20);
        BeatGrid.PruneDrag(anchors, drag2);

        double[] afterSecond = [.. Grid().Beats.Where(b => b.Seconds <= corrected + 1e-9).Select(b => b.Seconds)];
        bool secondOk = afterSecond.Length == beforeSecond.Length
                        && afterSecond.Zip(beforeSecond).All(x => Math.Abs(x.First - x.Second) < 1e-9);
        Report(secondOk, "a later edit leaves everything up to the previous edit alone");
    }

    // Cue points must be completely unaffected by anything done to the beat grid. They are stored
    // in absolute SOURCE seconds, so they stay glued to the audio they mark - which stays true even
    // once playback is warped, because warping changes when a source instant is heard, not which
    // instant a cue names.
    static void CuePointsSurvive() {
        Console.WriteLine("\n[14] Cue points are untouched by grid edits");

        var file = new DFile { BPM = 120, DownbeatAt = 0.0, Duration = 120 };
        file.CuePoints.Add(new DFile.CuePoint { Id = 1, Position = 12.345, Name = "Drop" });
        file.CuePoints.Add(new DFile.CuePoint { Id = 2, Position = 60.5, Name = "Break" });
        file.BeatGridMarkers.Add(new DFile.BeatGridMarker { Id = 1, Position = 0.0, BPM = 120, IsDownbeat = true });
        file.BeatGridMarkers.Add(new DFile.BeatGridMarker { Id = 2, Position = 30.0, BPM = 120, IsDownbeat = false });

        // Every editing operation, run over the anchors.
        BeatGrid.ApplyBend(file.BeatGridMarkers, BeatGrid.BeginBend(file.BeatGridMarkers, 1), 31.7);
        BeatGrid.Insert(file.BeatGridMarkers, 50.0, 118.0, false);
        BeatGrid.ShiftTail(file.BeatGridMarkers, 1, 0.25);
        BeatGrid.ShiftAll(file.BeatGridMarkers, 0.1);
        BeatGrid.Delete(file.BeatGridMarkers, 2);

        Report(file.CuePoints.Count == 2, "the cue points are all still there");
        Report(Near(file.CuePoints[0].Position, 12.345), "bend, insert, shift and delete leave cue positions alone");
        Report(Near(file.CuePoints[1].Position, 60.5), "...including one past every anchor that moved");
        Report(file.CuePoints[0].Name == "Drop", "...and their names");

        // Clone has to deep-copy cues as well as anchors, or Cancel in the dialog would not cancel.
        var copy = (DFile)file.Clone();
        copy.CuePoints[0].Position = 99.0;
        copy.CuePoints.Add(new DFile.CuePoint { Position = 5.0, Name = "Extra" });
        Report(Near(file.CuePoints[0].Position, 12.345) && file.CuePoints.Count == 2,
               "Clone deep-copies cue points, so editing the copy does not reach the original");

        // ApplyEditsFrom now also diffs anchors. It must STILL leave cue points alone - they belong
        // to CuePoints.UpdateCuePoints, which saves against its own context.
        var live = new DFile { Duration = 120 };
        live.CuePoints.Add(new DFile.CuePoint { Id = 9, Position = 3.5, Name = "Intro" });

        var edited = new DFile { BPM = 128, Duration = 120 };
        edited.BeatGridMarkers.Add(new DFile.BeatGridMarker { Position = 1.0, BPM = 128, IsDownbeat = true });
        live.ApplyEditsFrom(edited);

        Report(live.CuePoints.Count == 1 && Near(live.CuePoints[0].Position, 3.5),
               "ApplyEditsFrom leaves cue points alone while diffing anchors in");
        Report(live.BeatGridMarkers.Count == 1, "...and still applies the anchors");

        // A cue's position in GRID time moves when the grid changes - that is correct and is what
        // keeps it drawn over the right audio in the straightened view - but its source position,
        // which is what seeking uses, does not.
        var warped = new BeatGrid([
            new BeatGrid.Anchor(0.0, 128.0, true),
            new BeatGrid.Anchor(30.0, 124.0, false),
        ], 120.0, 1, 128.0);

        double cue = 60.0;
        Report(!Near(warped.ToGridTime(cue), cue), "a cue's grid time does shift under a warp");
        Report(Near(warped.FromGridTime(warped.ToGridTime(cue)), cue, 1e-8),
               "...but it maps back to exactly the same audio, which is what seeking uses");
    }



    // ------------------------------------------------------------------ onset detection

    // A click track: a short decaying burst at each beat, band-limited so the check can say which
    // of the detector's three bands it is exercising. Everything else about the signal is silence,
    // so anything the detector reports that is not a click is a false positive.
    static float[] Clicks(int rate, double duration, double first, double bpm,
                          double frequency, double amplitude = 0.7, Func<int, bool>? silent = null) {
        float[] samples = new float[(int)(rate * duration)];
        double step = 60.0 / bpm;

        for(int k = 0; ; k++) {
            double at = first + k * step;
            if(at >= duration - 0.05) break;
            if(silent != null && silent(k)) continue;

            int from = (int)(at * rate);

            // 25 ms of exponentially decaying tone. A real drum is broadband, but a tone is the
            // honest test of one band: if the low band alone can find a 60 Hz thump, it works.
            for(int i = 0; i < rate * 0.025 && from + i < samples.Length; i++) {
                double t = (double)i / rate;
                samples[from + i] += (float)(amplitude * Math.Exp(-t * 120.0) * Math.Sin(2 * Math.PI * frequency * t));
            }
        }

        return samples;
    }

    // Feeds a float array through OnsetEnvelope the way OnsetDetector feeds it a decode channel.
    static List<BeatAlign.Onset> Detect(float[] samples, int rate, double startSeconds = 0, double fromSeconds = 0) {
        int read = 0;

        return OnsetEnvelope.Onsets(buffer => {
            int n = Math.Min(buffer.Length, samples.Length - read);
            if(n <= 0) return 0;

            Array.Copy(samples, read, buffer, 0, n);
            read += n;
            return n;
        }, rate, startSeconds, fromSeconds);
    }

    // Worst error between each expected click and the nearest onset reported, plus how many
    // expected clicks got no onset at all.
    static (double Worst, int Missed) Compare(List<BeatAlign.Onset> onsets, double first, double bpm,
                                              double duration, Func<int, bool>? silent = null) {
        double step = 60.0 / bpm, worst = 0;
        int missed = 0;

        for(int k = 0; ; k++) {
            double at = first + k * step;
            if(at >= duration - 0.05) break;
            if(silent != null && silent(k)) continue;

            double nearest = double.MaxValue;
            foreach(BeatAlign.Onset onset in onsets) {
                nearest = Math.Min(nearest, Math.Abs(onset.Seconds - at));
            }

            if(nearest > 0.030) missed++; else worst = Math.Max(worst, nearest);
        }

        return (worst, missed);
    }

    static void OnsetDetection() {
        Console.WriteLine("\n[18] Onset detection");

        const int rate = 44100;
        const double duration = 20.0;

        // A kick drum, which is what the beat of most of this material actually is.
        {
            var samples = Clicks(rate, duration, 0.517, 128.0, 60.0);
            var onsets = Detect(samples, rate);
            (double worst, int missed) = Compare(onsets, 0.517, 128.0, duration);

            Report(missed == 0, $"every kick in a 20-second click track is found ({missed} missed of {onsets.Count} onsets)");
            Report(worst < 0.012, $"...to within {worst * 1000:F1} ms, well inside the 18 ms tolerance");
        }

        // A hat, to prove the high band is wired the right way round. A low-pass where a high-pass
        // was meant would find nothing here and everything above.
        {
            var samples = Clicks(rate, duration, 0.25, 174.0, 6000.0);
            var onsets = Detect(samples, rate);
            (double worst, int missed) = Compare(onsets, 0.25, 174.0, duration);

            Report(missed == 0, $"a high-frequency click track is found too ({missed} missed)");
            Report(worst < 0.012, $"...to within {worst * 1000:F1} ms");
        }

        // Silence has to produce nothing. This is the requirement the whole "wait for the next
        // beat" behaviour rests on, and it is why the flux is log(1 + lambda E) and not log E.
        {
            var onsets = Detect(new float[rate * 10], rate);
            Report(onsets.Count == 0, $"ten seconds of silence produces no onsets ({onsets.Count})");
        }

        // Digital black is easy; a noise floor is the real case. At -60 dB there is nothing to
        // detect, and the local statistics of noise must not promote it.
        {
            float[] samples = new float[rate * 10];
            var random = new Random(1);
            for(int i = 0; i < samples.Length; i++) samples[i] = (float)((random.NextDouble() - 0.5) * 0.002);

            var onsets = Detect(samples, rate);
            Report(onsets.Count == 0, $"a quiet noise floor produces no onsets either ({onsets.Count})");
        }

        // The absolute flux floor has to reject a noise floor without rejecting quiet music. A
        // click track 23 dB down is still perfectly audible material.
        {
            var samples = Clicks(rate, duration, 0.4, 120.0, 60.0, amplitude: 0.05);
            var onsets = Detect(samples, rate);
            (double worst, int missed) = Compare(onsets, 0.4, 120.0, duration);

            Report(missed == 0, $"a click track 23 dB down is still found ({missed} missed)");
            Report(worst < 0.012, $"...to within {worst * 1000:F1} ms");
        }

        // A breakdown in the middle: nothing reported there, and the beat found again after it.
        {
            var samples = Clicks(rate, duration, 0.5, 128.0, 60.0, silent: k => k >= 16 && k < 32);
            var onsets = Detect(samples, rate);

            double from = 0.5 + 16 * 60.0 / 128.0, to = 0.5 + 32 * 60.0 / 128.0;
            int inside = onsets.Count(x => x.Seconds > from + 0.05 && x.Seconds < to - 0.05);
            (_, int missed) = Compare(onsets, 0.5, 128.0, duration, k => k >= 16 && k < 32);

            Report(inside == 0, $"a silent stretch reports no onsets ({inside} inside it)");
            Report(missed == 0, $"...and the clicks after it are all still found ({missed} missed)");
        }

        // The pre-roll: samples are decoded before the start point so the filters settle, and
        // nothing from it may be reported.
        {
            var samples = Clicks(rate, duration, 0.5, 128.0, 60.0);
            var onsets = Detect(samples, rate, startSeconds: 8.0, fromSeconds: 8.5);

            Report(onsets.All(x => x.Seconds >= 8.5), $"no onset is reported before the start point");
            Report(onsets.Count > 30, $"...and the rest are still there ({onsets.Count})");
        }

        // Throughput. This runs behind a button press with a spinner on it, so it does not have to
        // be fast, but it does have to be bounded - and the filters are per-sample, so the cost is
        // linear in the length of the track and worth knowing.
        {
            var samples = Clicks(rate, 300.0, 0.5, 128.0, 60.0);

            var clock = System.Diagnostics.Stopwatch.StartNew();
            var onsets = Detect(samples, rate);
            clock.Stop();

            Report(clock.Elapsed.TotalSeconds < 3.0,
                   $"a five-minute track is analysed in {clock.Elapsed.TotalSeconds:F2}s ({onsets.Count} onsets)");
        }

        // The two halves together, on a click track that drifts: the acceptance test for the
        // feature as a whole, with nothing synthesised except the audio.
        {
            const double drifting = 60.0;
            var samples = Clicks(rate, drifting, 0.517, 127.4, 60.0);

            var file = new DFile { BPM = 128, DownbeatAt = 0.517, Duration = drifting };
            var onsets = Detect(samples, rate);
            var report = BeatAlign.Apply(file, onsets);

            double after = WorstGridError(file, onsets, 0.517);
            Report(report.Changed, $"a drifting click track is corrected end to end (\"{report.Summary}\")");
            Report(after < 0.015, $"...and the grid ends up on the clicks (worst {after * 1000:F1} ms)");
            Report(Math.Abs(report.MeasuredBPM - 127.4) < 0.15,
                   $"...at the tempo the clicks were actually written at ({report.MeasuredBPM:F2} vs 127.40)");
        }
    }

    // ------------------------------------------------------------------ automatic gridding

    // Onsets on a tempo schedule, starting at the downbeat. `legs` is a run of (beats, BPM) pairs,
    // so a track that changes tempo, or drifts in steps, is one line to describe.
    static List<BeatAlign.Onset> Onsets(double downbeat, (int Beats, double BPM)[] legs,
                                        double jitter = 0, Func<int, bool>? drop = null) {
        List<BeatAlign.Onset> onsets = [];
        double t = downbeat;
        int k = 0;

        foreach((int beats, double bpm) in legs) {
            double step = 60.0 / bpm;

            for(int i = 0; i < beats; i++) {
                // Deterministic, so a failure is reproducible. Real onset times wobble by a few
                // milliseconds even on a machine-made track.
                double wobble = jitter == 0 ? 0 : jitter * Math.Sin(k * 2.399963);
                if(drop == null || !drop(k)) onsets.Add(new BeatAlign.Onset(t + wobble, 4.0));

                t += step;
                k++;
            }
        }

        return onsets;
    }

    // The only measure that matters in the end: how far the audio is from the grid drawn over it.
    static double WorstGridError(DFile file, IEnumerable<BeatAlign.Onset> onsets, double from) {
        BeatGrid grid = BeatGrid.FromFile(file, 1);
        double worst = 0;

        foreach(BeatAlign.Onset onset in onsets) {
            if(onset.Seconds < from) continue;
            worst = Math.Max(worst, Math.Abs(onset.Seconds - grid.NearestBeat(onset.Seconds)));
        }

        return worst;
    }

    static double[] BeatsFrom(DFile file, double from)
        => [.. BeatGrid.FromFile(file, 1).Beats.Where(b => b.Seconds >= from - 1e-9).Select(b => b.Seconds)];

    static bool Same(double[] a, double[] b, double tolerance = 1e-9)
        => a.Length == b.Length && a.Zip(b).All(x => Math.Abs(x.First - x.Second) < tolerance);

    static DFile Track(double bpm, double downbeat, double duration)
        => new() { BPM = (float)bpm, DownbeatAt = downbeat, Duration = duration };

    static void AutomaticGridding() {
        Console.WriteLine("\n[19] Automatic gridding");

        // A track that is already right is left alone. This is the property that makes the button
        // safe to press twice, and safe to press on a track someone has already gridded by hand.
        {
            var file = Track(128, 0.5, 300);
            var onsets = Onsets(0.5, [(600, 128.0)]);
            var report = BeatAlign.Apply(file, onsets);

            Report(!report.Changed && file.BeatGridMarkers.Count == 0,
                   $"a track already on the grid is left untouched ({file.BeatGridMarkers.Count} markers)");
            Report(report.BeatsMatched > 500, $"...and its beats were still all found ({report.BeatsMatched} of {report.BeatsChecked})");
        }

        // The commonest case by far: BPM detection was very slightly wrong, so the whole track
        // walks away from the grid. One tempo explains it, so it costs no markers at all - only
        // the downbeat anchor's tempo changes.
        {
            var file = Track(128, 0.5, 300);
            var onsets = Onsets(0.5, [(600, 128.4)]);

            double before = WorstGridError(file, onsets, 0.5);
            var report = BeatAlign.Apply(file, onsets);
            double after = WorstGridError(file, onsets, 0.5);

            // Half a beat is the ceiling this measure can report - past that the grid is nearer
            // the NEXT beat - and at 128 BPM that is 234 ms. Reaching the ceiling is the point.
            Report(before > 0.2, $"a 0.4 BPM error walks the grid right off the beat ({before * 1000:F0} ms by the end)");
            Report(report.AnchorsAdded == 0 && Math.Abs(file.BPM - 128.4) < 1e-4,
                   $"correcting it needs no markers at all - the number was the error ({report.AnchorsAdded} added, BPM now {file.BPM:F2})");
            Report(after < 0.005, $"...and the grid now sits on every beat (worst {after * 1000:F1} ms)");
            Report(Math.Abs(report.MeasuredBPM - 128.4) < 0.02, $"the measured tempo is reported ({report.MeasuredBPM:F2})");
        }

        // A real tempo change needs exactly one marker, at the change. More than a handful would
        // mean it is fitting noise instead of the track.
        {
            var file = Track(128, 0.5, 320);
            var onsets = Onsets(0.5, [(64, 128.0), (600, 126.5)]);

            double before = WorstGridError(file, onsets, 0.5);
            var report = BeatAlign.Apply(file, onsets);
            double after = WorstGridError(file, onsets, 0.5);

            Report(report.AnchorsAdded == 1, $"one tempo change costs one marker ({report.AnchorsAdded})");
            Report(before > 0.2 && after < 0.02, $"and it fixes the grid ({before * 1000:F0} ms -> {after * 1000:F0} ms)");

            double change = 0.5 + 64 * 60.0 / 128.0;
            var placed = file.BeatGridMarkers.Where(m => !m.IsDownbeat).ToList();
            Report(placed.Count == 1 && Math.Abs(placed[0].Position - change) < 4 * 60.0 / 128.0,
                   $"the marker lands within a bar of the change (at {placed.FirstOrDefault()?.Position:F2}s, change at {change:F2}s)");
        }

        // Drift in several steps, which is what a tape transfer or a live recording actually does.
        {
            var file = Track(124, 2.0, 400);
            var onsets = Onsets(2.0, [(128, 124.0), (128, 123.4), (128, 124.7), (400, 123.9)], jitter: 0.004);

            double before = WorstGridError(file, onsets, 2.0);
            var report = BeatAlign.Apply(file, onsets);
            double after = WorstGridError(file, onsets, 2.0);

            Report(after < 0.025, $"a track that drifts in steps ends up on the grid ({before * 1000:F0} ms -> {after * 1000:F0} ms)");
            Report(report.AnchorsAdded >= 2 && report.AnchorsAdded <= 8,
                   $"...for a handful of markers, not one per beat ({report.AnchorsAdded})");
        }

        // Running it again must be a no-op. If it were not, every press would add anchors and the
        // grid would ratchet.
        {
            var file = Track(124, 2.0, 400);
            var onsets = Onsets(2.0, [(128, 124.0), (128, 123.4), (400, 124.6)], jitter: 0.004);

            BeatAlign.Apply(file, onsets);
            int first = file.BeatGridMarkers.Count;
            var again = BeatAlign.Apply(file, onsets);

            Report(again.AnchorsAdded == 0, $"a second run adds nothing ({again.AnchorsAdded})");
            Report(file.BeatGridMarkers.Count == first, $"...and the grid is the same size ({first} vs {file.BeatGridMarkers.Count})");
        }

        // Every judgement is made against the grid the track has NOW, including every correction
        // already made to it - not against the detected BPM. So a track whose grid has already been
        // corrected to 126.5, playing at 126.5, is already right and must be left alone, even
        // though it is 200 ms per minute away from where the base 128 BPM grid would put it.
        {
            var file = Track(128, 0.5, 200);
            BeatGrid.Insert(file.BeatGridMarkers, 0.5, 126.5, true, isReference: true);

            var onsets = Onsets(0.5, [(400, 126.5)]);
            double[] before = BeatsFrom(file, 0.5);
            var report = BeatAlign.Apply(file, onsets);

            Report(report.AnchorsAdded == 0 && report.SegmentsRetimed == 0,
                   $"an already-corrected grid is judged against itself, not the detected BPM ({report.AnchorsAdded} added, {report.SegmentsRetimed} retimed)");
            Report(Same(before, BeatsFrom(file, 0.5)), "...so not one beat after the downbeat moved");

            // What WAS still wrong is the number the grid was being corrected against. 126.5 is
            // the track's real tempo, so adopting it says the same thing the anchor was saying -
            // and now says it with no warp at all, instead of stretching the whole track by 1.2%.
            Report(Math.Abs(file.BPM - 126.5) < 1e-4, $"...but the BPM it was fighting is corrected ({file.BPM:F2})");
            Report(file.BeatGridMarkers.Count == 0, "...which leaves the anchor saying nothing, so it goes");
            Report(!BeatGrid.FromFile(file, 1).IsWarped, "...and the track plays at its own speed, unwarped");
        }

        // The same thing one segment in: a hand correction after a reference sets the tempo from
        // there on, and the beats after it are measured against THAT, not against nominal.
        {
            var file = Track(128, 0.5, 300);
            var anchors = file.BeatGridMarkers;
            BeatGrid.Insert(anchors, 0.5, 128.0, true, isReference: true);

            double at = BeatGrid.FromFile(file, 1).Advance(0.5, 64);
            BeatGrid.Insert(anchors, at, 126.0, false, isReference: true);

            var onsets = Onsets(0.5, [(64, 128.0), (500, 126.0)]);
            double[] before = BeatsFrom(file, 0.5);
            var report = BeatAlign.Apply(file, onsets);

            Report(report.AnchorsAdded == 0 && report.SegmentsRetimed == 0,
                   $"a corrected segment after a reference is left alone too ({report.AnchorsAdded} added, {report.SegmentsRetimed} retimed)");
            Report(anchors.Count == 2, $"...and no marker was added inside it ({anchors.Count} anchors)");
            Report(Same(before, BeatsFrom(file, 0.5)), "...and no beat after the downbeat moved");

            // Two real tempos, so any single target warps one of them. The duration-weighted
            // average is the target that stretches the track least in total, which is what the
            // measured tempo already is.
            Report(file.BPM > 126.0 && file.BPM < 128.0, $"the BPM becomes the track's average, not either half ({file.BPM:F2})");
        }

        // Corrections made EARLIER IN THE SAME RUN count as the current grid as well: a second
        // tempo change is measured from the first correction, not from where the track started.
        {
            var file = Track(128, 0.5, 400);
            var onsets = Onsets(0.5, [(64, 128.0), (128, 126.4), (500, 127.3)]);

            var report = BeatAlign.Apply(file, onsets);
            double after = WorstGridError(file, onsets, 0.5);

            Report(report.AnchorsAdded == 2, $"two tempo changes cost two markers, not more ({report.AnchorsAdded})");
            Report(after < 0.02, $"...because the second is measured from the first (worst {after * 1000:F1} ms)");
        }

        // The guard rails. A reference the user placed is a promise about where that beat is, and
        // automatic gridding is not allowed to break it - nor to move the downbeat, nor to reach
        // behind it into the intro.
        {
            var file = Track(128, 8.183, 400);
            var anchors = file.BeatGridMarkers;
            BeatGrid.Insert(anchors, file.DownbeatAt, file.BPM, true, isReference: true);

            double reference = BeatGrid.FromFile(file, 1).Advance(file.DownbeatAt, 64);
            BeatGrid.FromFile(file, 1).TogglePin(anchors, reference);

            double[] intro = [.. BeatGrid.FromFile(file, 1).Beats.Where(b => b.Seconds < file.DownbeatAt).Select(b => b.Seconds)];

            var onsets = Onsets(file.DownbeatAt, [(64, 128.0), (600, 127.1)]);
            BeatAlign.Apply(file, onsets);

            Report(anchors.Any(a => Math.Abs(a.Position - file.DownbeatAt) < 1e-12), "the downbeat anchor has not moved");
            Report(anchors.Any(a => Math.Abs(a.Position - reference) < 1e-12), "the reference has not moved");

            // The intro is drawn at the nominal tempo, so correcting the BPM re-spaces it - and it
            // should, because the intro of a 127 BPM track is 127 too. What must still hold is that
            // it moved for THAT reason and no other: every intro beat exactly one corrected beat
            // from the next, counted back from a downbeat that has not moved.
            double[] introAfter = [.. BeatGrid.FromFile(file, 1).Beats.Where(b => b.Seconds < file.DownbeatAt).Select(b => b.Seconds)];
            double corrected = 60.0 / file.BPM;

            bool spacedAtNominal = introAfter.Length > 0
                                   && Math.Abs(file.DownbeatAt - introAfter[^1] - corrected) < 1e-9
                                   && introAfter.Zip(introAfter.Skip(1)).All(x => Math.Abs(x.Second - x.First - corrected) < 1e-9);

            Report(spacedAtNominal, $"the intro is re-spaced at the corrected tempo, and by nothing else ({introAfter.Length} beats at {file.BPM:F2})");
            Report(intro.Length != introAfter.Length || !Same(intro, introAfter),
                   "...which does mean it moved - a corrected BPM is a corrected intro");
        }

        // The headline case, and the one a real track hits: the track never drifted at all, its
        // detected BPM was simply a beat out. Correcting the grid onto the wrong number would warp
        // the whole track by 0.8% for its entire length; correcting the number instead costs
        // nothing and leaves no markers behind.
        {
            var file = Track(124, 8.183, 300);
            var onsets = Onsets(8.183, [(600, 125.0)]);

            double before = WorstGridError(file, onsets, 8.183);
            var report = BeatAlign.Apply(file, onsets);
            double after = WorstGridError(file, onsets, 8.183);

            Report(Math.Abs(file.BPM - 125.0) < 1e-4, $"a track detected a beat slow has its BPM corrected ({file.BPM:F2})");
            Report(file.BeatGridMarkers.Count == 0, $"...with no markers left behind ({file.BeatGridMarkers.Count})");
            Report(!BeatGrid.FromFile(file, 1).IsWarped, "...and no warp at all, where gridding onto 124 would have stretched everything");
            Report(before > 0.2 && after < 0.005, $"...and the grid is on the beat ({before * 1000:F0} ms -> {after * 1000:F1} ms)");
            Report(report.Summary.Contains("set the BPM to 125"), $"...and it says so (\"{report.Summary}\")");
        }

        // Most tracks were made at a whole number, so a measurement a hair off one is measurement
        // error. Snapping is safe because of the warp: it does not claim the track is 125, it
        // makes it 125.
        {
            var file = Track(128, 0.5, 300);
            var onsets = Onsets(0.5, [(600, 124.985)]);
            BeatAlign.Apply(file, onsets);

            Report(file.BPM == 125.0f, $"a measurement a hair off a whole number snaps to it ({file.BPM})");
        }

        // ...but only a hair. A tape transfer really running at 124.6 keeps 124.6.
        {
            var file = Track(128, 0.5, 300);
            var onsets = Onsets(0.5, [(600, 124.6)]);
            BeatAlign.Apply(file, onsets);

            Report(Math.Abs(file.BPM - 124.6) < 0.01, $"a tempo genuinely between whole numbers is kept ({file.BPM:F2})");
        }

        // And a difference too small to be worth the rewrite is left alone entirely.
        {
            var file = Track(128, 0.5, 300);
            var onsets = Onsets(0.5, [(600, 128.008)]);
            var report = BeatAlign.Apply(file, onsets);

            Report(file.BPM == 128.0f && report.NominalBPM == 0,
                   $"a trivial disagreement does not rewrite the BPM ({file.BPM})");
        }

        // Nothing to go on means nothing happens. Not an empty grid, not a guess - the grid the
        // track already had.
        {
            var file = Track(128, 0.5, 300);
            var report = BeatAlign.Apply(file, []);

            Report(report.AnchorsAdded == 0 && file.BeatGridMarkers.Count == 0,
                   "a track with no detectable onsets is left completely alone");
            Report(report.Summary.Contains("No beats"), $"...and says so (\"{report.Summary}\")");
        }

        // A breakdown with no drums in it. The beats there cannot be checked, so they are not
        // touched - and the tracker has to pick the beat up again on the other side, which is what
        // the widening search window is for.
        {
            var file = Track(128, 0.5, 400);
            var onsets = Onsets(0.5, [(700, 127.6)], drop: k => k >= 64 && k < 160);
            var report = BeatAlign.Apply(file, onsets);
            double after = WorstGridError(file, onsets, 0.5);

            Report(report.BeatsMatched > 500, $"beats after a 45-second breakdown are found again ({report.BeatsMatched} matched)");
            Report(after < 0.02, $"...and the grid still lands on them (worst {after * 1000:F1} ms)");
        }

        // The bail-out. If the tempo the beats imply is nowhere near the nominal one, the
        // measurement is wrong far more often than the track is, so nothing is written.
        {
            var file = Track(128, 0.5, 300);
            var onsets = Onsets(0.5, [(600, 147.0)]);       // ~15% out: a mis-detected BPM
            var report = BeatAlign.Apply(file, onsets);

            Report(!report.Changed, $"an unbelievable tempo is refused, not written ({report.AnchorsAdded} markers, {report.SegmentsRetimed} retimed)");
            Report(file.BeatGridMarkers.Count == 0, "...and the track is left completely ungridded");
            Report(report.Summary.Contains("left alone"), $"...with a reason (\"{report.Summary}\")");
        }

        // A few wrong onsets - a syncopated stab, a vocal - must not each become a tempo change.
        {
            var file = Track(128, 0.5, 300);
            var onsets = Onsets(0.5, [(600, 128.0)]);
            onsets.Add(new BeatAlign.Onset(0.5 + 40 * 60.0 / 128.0 + 0.055, 9.0));
            onsets.Add(new BeatAlign.Onset(0.5 + 41 * 60.0 / 128.0 - 0.060, 9.0));
            onsets.Add(new BeatAlign.Onset(0.5 + 200 * 60.0 / 128.0 + 0.062, 9.0));

            var report = BeatAlign.Apply(file, onsets);
            Report(report.AnchorsAdded == 0, $"strong off-beat onsets do not become markers ({report.AnchorsAdded})");
            Report(report.BeatsMatched > 500 && report.SegmentsRetimed == 0,
                   $"...the real beat next to them wins the match, however loud they are ({report.BeatsMatched} matched)");
        }

        // And when a stray is the ONLY thing near a beat - the kick dropped out for a bar and a
        // syncopated stab is all that is left - it has to be discarded rather than believed, or one
        // wrong onset becomes a tempo change.
        {
            var file = Track(128, 0.5, 300);
            var onsets = Onsets(0.5, [(600, 128.0)], drop: k => k == 300 || k == 301);
            onsets.Add(new BeatAlign.Onset(0.5 + 300 * 60.0 / 128.0 + 0.055, 9.0));
            onsets.Add(new BeatAlign.Onset(0.5 + 301 * 60.0 / 128.0 + 0.058, 9.0));
            onsets = [.. onsets.OrderBy(x => x.Seconds)];

            var report = BeatAlign.Apply(file, onsets);
            Report(report.OutliersIgnored >= 2, $"an onset with no real beat beside it is discarded ({report.OutliersIgnored} outliers)");
            Report(report.AnchorsAdded == 0, $"...and costs no marker ({report.AnchorsAdded})");
        }

        // Half-tempo matching is the failure that produces a confident, completely wrong grid, so
        // the search window is capped below half a beat whatever else happens.
        {
            var file = Track(128, 0.5, 300);
            var offbeat = Onsets(0.5 + 30.0 / 128.0, [(600, 128.0)]);       // every onset half a beat late
            var report = BeatAlign.Apply(file, offbeat);

            Report(report.BeatsMatched == 0, $"onsets half a beat out are not matched at all ({report.BeatsMatched})");
            Report(report.AnchorsAdded == 0, "...so nothing is written");
        }

        // What the algorithm writes has to be indistinguishable from a hand edit, because
        // everything downstream - the warp, Reset, dragging one afterwards - treats it as one.
        {
            var file = Track(128, 0.5, 320);
            var onsets = Onsets(0.5, [(64, 128.0), (600, 126.6)]);
            BeatAlign.Apply(file, onsets);

            var placed = file.BeatGridMarkers.Where(m => !m.IsDownbeat).ToList();
            Report(placed.All(m => !m.IsReference),
                   "automatic markers are adjustments, not references - so they stay draggable");
            Report(file.BeatGridMarkers.Zip(file.BeatGridMarkers.Skip(1)).All(x => x.First.Position < x.Second.Position),
                   "the anchor list comes out strictly ordered");

            BeatGrid grid = BeatGrid.FromFile(file, 1);
            Report(grid.IsWarped, "the grid reports itself warped, so playback will follow it");
            Report(Math.Abs(grid.PlaybackRateAt(0.0) - 1.0) < 1e-12,
                   "and the intro still plays at exactly normal speed");
        }
    }

    // ------------------------------------------------------------------

    static bool Near(double a, double b, double tolerance = 1e-9) => Math.Abs(a - b) < tolerance;

    static void Report(bool ok, string what) {
        Console.WriteLine($"    {(ok ? "ok  " : "FAIL")}  {what}");
        if(!ok) failures++;
    }
}
