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
        Persistence();
        Warp();
        Editing();
        BeatDragging();
        CuePointsSurvive();

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
        BeatGrid.Prune(a5);
        Report(a5.Count == 1, $"dragging out and back leaves no anchor behind, got {a5.Count}");
        Report(Near(a5[0].BPM, 120.0, 1e-9), "...and the tempo is exactly as it was");

        // A real correction is NOT pruned.
        var (g6, a6) = Build((0.0, 120.0));
        BeatGrid.ApplyBeatDrag(a6, g6.BeginBeatDrag(a6, 10.0), 10.4);
        BeatGrid.Prune(a6);
        Report(a6.Count == 2, "a genuine correction keeps its anchor");

        // Downbeat anchors carry bar phase, not tempo, so they always survive pruning.
        var a7 = Anchors((0.0, 120.0), (10.0, 120.0));
        a7[1].IsDownbeat = true;
        BeatGrid.Prune(a7);
        Report(a7.Count == 2, "a downbeat anchor is never pruned");

        var (g8, a8) = Build((0.0, 120.0));
        Report(!g8.BeginBeatDrag([], 10.0).IsValid, "grabbing with no anchors at all is refused");
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

    // ------------------------------------------------------------------

    static bool Near(double a, double b, double tolerance = 1e-9) => Math.Abs(a - b) < tolerance;

    static void Report(bool ok, string what) {
        Console.WriteLine($"    {(ok ? "ok  " : "FAIL")}  {what}");
        if(!ok) failures++;
    }
}
