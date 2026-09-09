namespace Diyokee;

// Automatic beat gridding: measure where the beats actually are, and insert the fewest tempo
// anchors that keep the grid on them.
//
// This is the same edit the user makes by hand in the grid editor, made by measurement instead of
// by eye. It emits ordinary anchors, so everything downstream - the warp, the drawing, Reset,
// dragging one afterwards - works on the result exactly as it works on a hand-built grid. There is
// no "automatic" flavour of anchor and nothing here the editor cannot undo.
//
// Deliberately NOT a beat tracker over the whole file. It starts from the downbeat, which the user
// owns, and only ever asks "is the beat the grid predicts actually there?" - so a track it cannot
// follow ends up with the grid it already had rather than a confident wrong one.
//
// Four rules shape all of it:
//
//   Existing anchors are never moved or removed. Every one is a guard rail, so a span between two
//   of them is fitted with both ends nailed down and the beat count frozen. That means a hand
//   correction survives a run, and running twice changes nothing the first run got right - a grid
//   that already fits needs no corrections.
//
//   Nothing before the downbeat is looked at. There is no grid there to be right or wrong about:
//   BeatGrid extrapolates that region at the nominal tempo precisely so that nothing can move it.
//
//   A correction that needs an unbelievable tempo is not made. If the fit wants a tempo further
//   than Options.MaxTempoDeviation from nominal, the measurement is wrong far more often than the
//   track is, so the span is abandoned and said so in the report.
//
//   Nothing is written until the result has been checked. Being able to fit a line is not evidence
//   of anything - the fit will always find one through whatever it matched. So the grid this
//   produces is measured against the onsets before it is committed, and kept only if it holds more
//   of the beats than the grid the track already had. That is the bail-out that matters, and it is
//   why a track this does not understand comes out of a run unchanged rather than mangled.
//
// Every judgement is made against the grid the track has NOW, never against the detected BPM. A
// beat is predicted from the segment it is in, and that segment's tempo is whatever the current
// grid says - a hand correction, an earlier automatic run, or a correction made moments ago
// earlier in this same walk. So a track already corrected to 126.5 and playing at 126.5 is
// already right and is left alone, even though it is nowhere near where a 128 BPM grid would put
// it; and a second tempo change is measured from the first correction, not from the start.
//
// The single exception is Options.MaxTempoDeviation, which is deliberately measured against the
// nominal BPM: that band exists to bound the WARP, and the warp corrects onto nominal, so a
// segment 6% off nominal is one that would be played 6% fast however it got there.
//
// A run may also RETUNE the track - set DFile.BPM to the tempo it measured. Gridding onto a wrong
// nominal does work, but it works by warping the whole track for its entire length to force it to
// a tempo it never had; adopting the measured tempo leaves the grid exactly as correct with the
// warp near 1.0. This is the one thing here that moves beats before the downbeat, and it is not
// the guard rail leaking: the intro of a 125 BPM track is 125 too, so re-spacing it at the
// corrected tempo is the fix, not a side effect. It happens after the verification below, never
// before, because the two grids being compared are both built with the BPM the track came in with.
//
// Onset detection lives in Classes/OnsetDetector.cs, which is the only part that needs audio.
// Everything here is arithmetic over a list of times, which is why tools/gridtest can drive it.
public static class BeatAlign {
    // A detected transient. Strength is how far the onset stood out from its surroundings, in local
    // standard deviations, and is only ever used to choose between candidates - never as a veto,
    // which the detector has already applied.
    public readonly record struct Onset(double Seconds, double Strength);

    public sealed class Options {
        // A beat this close to where the grid puts it is already right. Below roughly 10 ms nothing
        // is audible against a mixed track, and onset times are not that repeatable anyway.
        public double ToleranceSeconds { get; init; } = 0.018;

        // How far either side of the predicted beat an onset may be and still be taken as that
        // beat. Hard-capped at 0.4 of a beat further down, so it can never reach the neighbour.
        public double SearchSeconds { get; init; } = 0.070;

        // Added to the search window for each beat that went unmatched. A breakdown with no drums
        // is exactly where drift goes unnoticed, so the longer we have been guessing, the less sure
        // we are of the phase - without this, a track never re-locks after a long quiet passage.
        public double DriftPerBeatSeconds { get; init; } = 0.0008;

        // A fitted tempo further than this from nominal is not believed. This is the bail-out the
        // whole thing needs: it is the difference between "this track drifts" and "we matched the
        // wrong transients".
        public double MaxTempoDeviation { get; init; } = 0.06;

        // Consecutive measurements that must disagree with the current segment before it is split.
        // One stray onset is a stray onset; two in a row is a tempo change.
        public int ConfirmBeats { get; init; } = 2;

        // No anchor closer than this to the one before it, or to the end of the span. Drift is
        // gradual; anchors every other bar would be fitting noise, and each one is a tempo step
        // that has to be audible to be worth having.
        public int MinSegmentBeats { get; init; } = 8;

        // Belt and braces against a pathological track producing hundreds of anchors.
        public int MaxAnchorsPerSpan { get; init; } = 64;

        // Total disagreeing measurements a span tolerates before it is given up on.
        public int MaxStrikes { get; init; } = 24;

        // Onsets weaker than this are not even considered. The detector thresholds already, so this
        // is a second, blunter filter for callers that want one.
        public double MinOnsetStrength { get; init; } = 0.0;

        // Fraction of the beats checked that must actually have been found before anything is
        // written. This is the whole-track version of the bail-out, and it is not redundant with
        // the tempo band: on a track whose tempo is nothing like the nominal one the predicted
        // beats and the real ones drift in and out of phase, so a few coincidental matches - far
        // apart, and each in band on its own - can fit a confident line through noise. A track
        // that is being followed matches most of its beats; one that is not, does not.
        public double MinMatchedFraction { get; init; } = 0.25;

        // Beats in a row that have to land on the audio before that stretch counts as being
        // followed. Isolated hits are what a wrong tempo produces as its predictions wander in and
        // out of phase; a tempo that is right produces runs hundreds of beats long.
        public int MinRunBeats { get; init; } = 8;

        // Fraction of the beats after the downbeat that have to end up in such a run for the
        // corrected grid to be accepted at all.
        public double MinLockedFraction { get; init; } = 0.5;

        // Put anchors on a bar line where one will do, counting from the span's start. Cosmetic -
        // the fit is re-checked against the shorter segment before the snapped point is used, so
        // this can only ever cost bars, never accuracy.
        public bool SnapToBar { get; init; } = true;

        // Smallest disagreement between the measured tempo and the track's BPM worth acting on.
        // Below this the warp it would save is not worth rewriting the number for.
        public double MinBPMCorrection { get; init; } = 0.02;

        // A measured tempo this close to a whole number is taken to BE that whole number. Most
        // tracks were made at an integer BPM, and the residue is measurement error - but the reason
        // this is safe rather than merely tidy is the warp: whatever nominal says, the track is
        // played at it. Snapping to 125 does not claim the track is 125, it makes it 125.
        public double WholeBPMSnap { get; init; } = 0.05;
    }

    public sealed class Report {
        public int AnchorsAdded { get; set; }

        // Segments whose tempo was rewritten without a new anchor. The commonest correction of all
        // is this with AnchorsAdded at zero: BPM detection was a fraction out, so the whole track
        // walks off the grid, and one tempo explains all of it.
        public int SegmentsRetimed { get; set; }

        public int BeatsChecked { get; set; }
        public int BeatsMatched { get; set; }
        public int OutliersIgnored { get; set; }
        public int SpansAbandoned { get; set; }

        // How far the worst matched beat was from where the grid had it, before any correction.
        public double WorstDriftSeconds { get; set; }

        // Tempo actually measured over everything that was fitted.
        public double MeasuredBPM { get; set; }

        // The track's BPM after the run, when the measurement said the old one was wrong; zero if
        // it was left alone. See the note on retuning in the header.
        public double NominalBPM { get; set; }

        public bool Changed => AnchorsAdded > 0 || SegmentsRetimed > 0 || NominalBPM > 0;
        public string Summary { get; set; } = "";

        // Running totals the measured tempo is derived from.
        internal double FittedBeats { get; set; }
        internal double FittedSeconds { get; set; }
    }

    private readonly record struct Obs(int K, double T, double Strength);

    // Reads onsets, writes anchors onto file.BeatGridMarkers. The caller is expected to follow up
    // with Player.RefreshBeatGrid so the warp and both waveforms pick the change up.
    public static Report Apply(DFile file, IReadOnlyList<Onset> onsets, Options? options = null) {
        Options o = options ?? new Options();
        Report r = new();

        if(!(file.BPM > 0)) {
            r.Summary = "This track has no BPM, so there is no tempo to measure against.";
            return r;
        }

        if(file.DownbeatAt < 0) {
            r.Summary = "This track has no downbeat, so there is nothing to align to.";
            return r;
        }

        Onset[] sorted = [.. onsets.Where(x => x.Strength >= o.MinOnsetStrength).OrderBy(x => x.Seconds)];
        if(sorted.Length == 0) {
            r.Summary = "No beats could be detected in this track.";
            return r;
        }

        List<DFile.BeatGridMarker> anchors = file.BeatGridMarkers;

        // An ungridded track has one implied anchor: the pair analysis produced. It is only written
        // out at the end, and only if there turns out to be something to correct - pressing the
        // button on a track that is already right must leave no trace at all, for the same reason
        // BeatGrid.PruneDrag removes an anchor a drag did not change.
        bool implied = anchors.Count == 0;

        // Snapshot before touching anything, because the spans are indexed against this list and
        // the writes at the end insert into it.
        (double Position, double BPM)[] fixedPoints = implied
            ? [(file.DownbeatAt, file.BPM)]
            : [.. anchors.OrderBy(a => a.Position).Select(a => (a.Position, a.BPM))];

        // Start at the anchor on or before the downbeat - normally the downbeat itself. Anything
        // earlier is deliberately left alone.
        int first = Array.FindLastIndex(fixedPoints, p => p.Position <= file.DownbeatAt + 1e-6);
        if(first < 0) first = 0;

        List<(double Position, double BPM)> inserts = [];
        List<(double Position, double BPM)> tempos = [];

        for(int i = first; i < fixedPoints.Length; i++) {
            double spanStart = fixedPoints[i].Position;
            double spanBPM = fixedPoints[i].BPM > 0 ? fixedPoints[i].BPM : file.BPM;
            bool closed = i + 1 < fixedPoints.Length;
            double spanEnd = closed ? fixedPoints[i + 1].Position : file.Duration;

            if(spanEnd <= spanStart) continue;

            // The beat count of a span is frozen, exactly as it is for a hand drag: a correction
            // re-times beats, it never invents or loses one. A closed span rounds, because its two
            // anchors were placed a whole number of beats apart; an open tail takes what fits.
            double period = 60.0 / spanBPM;
            int spanBeats = closed ? (int)Math.Round((spanEnd - spanStart) / period)
                                   : (int)Math.Floor((spanEnd - spanStart) / period);
            if(spanBeats < o.MinSegmentBeats + 1) continue;

            List<Obs> breaks = [];
            FitSpan(o, r, sorted, file.BPM, spanStart, period, spanEnd, spanBeats, closed, breaks, out double tail);

            // Turn the breakpoints into anchors. Each segment's tempo is its frozen beat count over
            // its measured length - the same arithmetic BeatGrid.ApplyBend uses, so a run produces
            // a grid indistinguishable from one dragged by hand.
            List<Obs> points = [new Obs(0, spanStart, 0), .. breaks];
            if(closed) points.Add(new Obs(spanBeats, spanEnd, 0));

            List<(double Position, double BPM)> spanTempos = [];
            List<(double Position, double BPM)> spanInserts = [];
            double spanFittedBeats = 0, spanFittedSeconds = 0;
            bool sane = true;

            for(int p = 0; p + 1 < points.Count; p++) {
                double bpm = 60.0 * (points[p + 1].K - points[p].K) / (points[p + 1].T - points[p].T);
                if(!Believable(bpm, file.BPM, o)) { sane = false; break; }

                if(p == 0) spanTempos.Add((spanStart, bpm)); else spanInserts.Add((points[p].T, bpm));

                spanFittedBeats += points[p + 1].K - points[p].K;
                spanFittedSeconds += points[p + 1].T - points[p].T;
            }

            // The open tail has no anchor after it to divide into, so it takes the fitted tempo.
            if(sane && !closed) {
                double bpm = 60.0 / tail;
                if(!Believable(bpm, file.BPM, o)) {
                    sane = false;
                } else if(points.Count == 1) {
                    spanTempos.Add((spanStart, bpm));
                    spanFittedBeats += spanBeats;
                    spanFittedSeconds += spanBeats * tail;
                } else {
                    spanInserts.Add((points[^1].T, bpm));
                    spanFittedBeats += spanBeats - points[^1].K;
                    spanFittedSeconds += (spanBeats - points[^1].K) * tail;
                }
            }

            if(!sane) {
                // A span whose arithmetic came out unbelievable contributes nothing at all, rather
                // than a half-applied correction.
                r.SpansAbandoned++;
                continue;
            }

            tempos.AddRange(spanTempos);
            inserts.AddRange(spanInserts);
            r.FittedBeats += spanFittedBeats;
            r.FittedSeconds += spanFittedSeconds;
        }

        if(r.FittedSeconds > 0) r.MeasuredBPM = 60.0 * r.FittedBeats / r.FittedSeconds;

        // Too few of the beats were where any tempo put them. Whatever this track is, it is not
        // being followed, and a grid built from the handful that did match would be confident and
        // wrong. It keeps the grid it came in with.
        if(r.BeatsChecked > 0 && r.BeatsMatched < r.BeatsChecked * o.MinMatchedFraction) {
            r.Summary = $"Only {r.BeatsMatched} of {r.BeatsChecked} beats could be found, "
                        + "which is too few to grid from - the track was left alone.";
            return r;
        }

        // A tempo that matches what the anchor already carries is not a correction. Filtering
        // these out is what makes a run on an already-correct track a genuine no-op, rather than
        // one that rewrites every tempo with the same number and seeds an anchor to hold it.
        List<(double Position, double BPM)> changes = [];

        foreach((double position, double bpm) in tempos) {
            double current = implied ? file.BPM
                                     : anchors.FirstOrDefault(a => Math.Abs(a.Position - position) < 1e-9)?.BPM ?? bpm;
            if(Math.Abs(bpm - current) > 1e-4) changes.Add((position, bpm));
        }

        // The track's own tempo, which may not be the one analysis detected. Correcting the grid
        // onto a wrong nominal works - the beats land - but it does it by warping the whole track
        // for its entire length to force it to a tempo it never had. Adopting the measured tempo
        // instead leaves the grid exactly as correct and the warp near 1.0, which is the same
        // answer with none of the time-stretching.
        double retuned = ChooseNominal(file.BPM, r.MeasuredBPM, o);
        bool retune = Math.Abs(retuned - file.BPM) > 1e-6;

        if(changes.Count == 0 && inserts.Count == 0 && !retune) {
            r.Summary = Describe(r, o);
            return r;
        }

        // Everything above proposes. This decides.
        //
        // The fit can always find some line through whatever it matched, so being able to fit is
        // not evidence of anything - the only question worth asking is the one asked after the
        // fact: does the grid this produces sit on the beats, and does it sit on them better than
        // the grid the track already had? A run that cannot answer yes to both is discarded whole.
        // That is what makes the button safe to press on a track it turns out not to understand.
        List<DFile.BeatGridMarker> existing = Copy(anchors);
        if(implied) BeatGrid.Insert(existing, file.DownbeatAt, file.BPM, true, isReference: true);

        List<DFile.BeatGridMarker> candidate = Copy(existing);
        Commit(candidate, changes, inserts);

        (int lockedNow, int total) = Locked(file, sorted, existing, o);
        (int lockedNext, _) = Locked(file, sorted, candidate, o);

        if(total > 0 && (lockedNext < lockedNow || lockedNext < total * o.MinLockedFraction)) {
            r.Summary = $"The corrected grid held {lockedNext} of {total} beats against the current grid's "
                        + $"{lockedNow}, which is not good enough to keep - the track was left alone.";
            return r;
        }

        if(implied) BeatGrid.Insert(anchors, file.DownbeatAt, file.BPM, true, isReference: true);

        // Tempos first, positions second: writing a segment's tempo onto the anchor that starts it
        // has to happen while the anchor list is still the one the spans were indexed against.
        foreach((double position, double bpm) in changes) {
            DFile.BeatGridMarker? at = anchors.FirstOrDefault(a => Math.Abs(a.Position - position) < 1e-9);
            if(at == null) continue;

            at.BPM = bpm;
            r.SegmentsRetimed++;
        }

        foreach((double position, double bpm) in inserts) {
            BeatGrid.Insert(anchors, position, bpm, false);
            r.AnchorsAdded++;
        }

        // After the anchors, never before: the verification above compares two grids built with the
        // BPM the track came in with, and the anchors carry absolute tempos, so retuning changes
        // neither. What it does change is the warp target, and the beats before the downbeat, which
        // are extrapolated at nominal - and those SHOULD move, because the intro of a 125 BPM track
        // is 125 too. That is a corrected tempo, not an edit reaching behind the downbeat.
        if(retune) {
            file.BPM = (float)retuned;
            r.NominalBPM = retuned;
        }

        // The commonest outcome of all: the track never drifted, its BPM was simply wrong. Once the
        // number is right, a lone downbeat anchor carrying that same tempo says exactly what BPM
        // and DownbeatAt already say - so leaving it behind would light Reset up on a track whose
        // grid is untouched. Same reasoning as BeatGrid.PruneDrag.
        // 1e-4 rather than something tighter because DFile.BPM is a float: a double that has been
        // through it comes back up to 8e-6 away at these tempos, so a stricter test would never
        // fire. It is deliberately NOT loosened to something musical - 0.02 BPM sounds negligible
        // but accumulates past the 18 ms tolerance over a five-minute track, so an anchor that
        // close to nominal is still carrying real information and stays.
        if(anchors.Count == 1 && anchors[0].IsDownbeat
           && Math.Abs(anchors[0].Position - file.DownbeatAt) < 1e-9
           && Math.Abs(anchors[0].BPM - file.BPM) < 1e-4) {
            anchors.Clear();
            r.SegmentsRetimed = 0;
        }

        r.Summary = Describe(r, o);
        return r;
    }

    // The tempo to grid onto. Zero means "no measurement", which leaves the track's own BPM alone.
    private static double ChooseNominal(double current, double measured, Options o) {
        if(!(measured > 0)) return current;

        double whole = Math.Round(measured);
        if(whole > 0 && Math.Abs(measured - whole) <= o.WholeBPMSnap) measured = whole;

        return Math.Abs(measured - current) < o.MinBPMCorrection ? current : measured;
    }

    private static List<DFile.BeatGridMarker> Copy(List<DFile.BeatGridMarker> anchors)
        => [.. anchors.Select(a => new DFile.BeatGridMarker {
            Id = a.Id, Position = a.Position, BPM = a.BPM, IsDownbeat = a.IsDownbeat, IsReference = a.IsReference
        })];

    private static void Commit(List<DFile.BeatGridMarker> anchors,
                               List<(double Position, double BPM)> tempos,
                               List<(double Position, double BPM)> inserts) {
        foreach((double position, double bpm) in tempos) {
            DFile.BeatGridMarker? at = anchors.FirstOrDefault(a => Math.Abs(a.Position - position) < 1e-9);
            if(at != null) at.BPM = bpm;
        }

        foreach((double position, double bpm) in inserts) BeatGrid.Insert(anchors, position, bpm, false);
    }

    // How many beats of a grid actually land on a transient, counting only those in runs long
    // enough to mean the beat is being followed rather than coincided with. Everything before the
    // downbeat is excluded, because nothing here is allowed to have an opinion about it.
    private static (int Locked, int Total) Locked(DFile file, Onset[] onsets,
                                                  List<DFile.BeatGridMarker> anchors, Options o) {
        BeatGrid grid = new(anchors.Select(a => new BeatGrid.Anchor(a.Position, a.BPM, a.IsDownbeat, a.IsReference)),
                            file.Duration, 1, file.BPM);

        int total = 0, locked = 0, run = 0;

        foreach(BeatGrid.Beat beat in grid.Beats) {
            if(beat.Seconds < file.DownbeatAt - 1e-9) continue;

            total++;

            if(Near(onsets, beat.Seconds, o.ToleranceSeconds)) {
                run++;
                continue;
            }

            if(run >= o.MinRunBeats) locked += run;
            run = 0;
        }

        if(run >= o.MinRunBeats) locked += run;
        return (locked, total);
    }

    private static bool Near(Onset[] onsets, double seconds, double tolerance) {
        int i = LowerBound(onsets, seconds - tolerance);
        return i < onsets.Length && onsets[i].Seconds <= seconds + tolerance;
    }

    private static bool Believable(double bpm, double nominalBPM, Options o)
        => double.IsFinite(bpm) && bpm > 0
           && Math.Abs(bpm - nominalBPM) <= nominalBPM * o.MaxTempoDeviation;

    // Walks one span beat by beat, growing a single-tempo segment for as long as one tempo can
    // explain the beats it finds, and breaking it where one no longer can.
    //
    // The prediction comes from the segment's own running fit rather than from the nominal tempo,
    // which is what keeps the search window narrow: a window wide enough to absorb the drift of a
    // whole track would be wide enough to grab the wrong beat.
    private static void FitSpan(Options o, Report r, Onset[] onsets, double nominalBPM,
                                double spanStart, double spanPeriod, double spanEnd, int spanBeats,
                                bool closed, List<Obs> breaks, out double tail) {
        double minPeriod = 60.0 / (nominalBPM * (1.0 + o.MaxTempoDeviation));
        double maxPeriod = 60.0 / (nominalBPM * (1.0 - o.MaxTempoDeviation));

        int segStartK = 0;
        double segStartT = spanStart;
        double segPeriod = spanPeriod;
        tail = spanPeriod;

        List<Obs> accepted = [];
        List<Obs> pending = [];
        Obs lastAccepted = new(0, spanStart, 0);
        int strikes = 0;
        int unmatched = 0;

        for(int k = 1; k < spanBeats; k++) {
            double predicted = segStartT + (k - segStartK) * segPeriod;
            if(predicted >= spanEnd) break;

            r.BeatsChecked++;

            // Capped at 0.4 of a beat so the window can never reach the neighbouring beat, however
            // long we have been extrapolating. Half-beat matching is the failure that produces a
            // confident, completely wrong grid.
            double window = Math.Min(0.4 * segPeriod, o.SearchSeconds + unmatched * o.DriftPerBeatSeconds);

            if(!TryMatch(onsets, predicted, window, out Onset hit)) {
                // Nothing there. A soft passage is not evidence of anything, so the grid is left as
                // it is and the next beat is tried.
                unmatched++;
                continue;
            }

            unmatched = 0;
            r.BeatsMatched++;
            r.WorstDriftSeconds = Math.Max(r.WorstDriftSeconds,
                                           Math.Abs(hit.Seconds - (spanStart + k * spanPeriod)));

            Obs obs = new(k, hit.Seconds, hit.Strength);

            accepted.Add(obs);
            double slope = Fit(accepted, segStartK, segStartT);
            bool fits = slope >= minPeriod && slope <= maxPeriod
                        && MaxResidual(accepted, segStartK, segStartT, slope) <= o.ToleranceSeconds;

            if(fits) {
                segPeriod = slope;
                tail = slope;
                lastAccepted = obs;
                pending.Clear();
                strikes = 0;
                continue;
            }

            accepted.RemoveAt(accepted.Count - 1);
            pending.Add(obs);
            strikes++;

            if(pending.Count < o.ConfirmBeats) continue;

            Obs at = lastAccepted;
            bool canSplit = accepted.Count > 0
                            && breaks.Count < o.MaxAnchorsPerSpan
                            && at.K - segStartK >= o.MinSegmentBeats
                            && spanBeats - at.K >= o.MinSegmentBeats;

            // Disagreeing with the current segment is not enough. The measurements that disagreed
            // have to agree with EACH OTHER about a believable tempo, measured from the break
            // about to be made - otherwise two strays in a row (a bar where the kick drops out and
            // a syncopated stab is all that is left) become a tempo change, and a spurious tempo
            // change is audible.
            if(canSplit) {
                double proposed = Fit(pending, at.K, at.T);
                canSplit = proposed >= minPeriod && proposed <= maxPeriod
                           && MaxResidual(pending, at.K, at.T, proposed) <= o.ToleranceSeconds;
            }

            // Cosmetic, and applied only once the break itself has been justified: the fit is
            // re-checked against the shorter segment, and the new segment refits from its own
            // measurements regardless.
            if(canSplit && o.SnapToBar) at = SnapBack(o, accepted, segStartK, segStartT, minPeriod, maxPeriod);

            if(canSplit) {
                breaks.Add(at);
                segStartK = at.K;
                segStartT = at.T;
                lastAccepted = at;
                accepted.Clear();
                pending.Clear();
                strikes = 0;

                // Re-read from the beat after the break: the observations that did not fit the old
                // segment are the start of the new one's evidence, not outliers.
                k = at.K;
                continue;
            }

            // Too early in the segment to split. Either these measurements are strays, or the
            // handful the segment was seeded on were - and if the recent ones agree with each other
            // about a believable tempo, they are the better bet.
            double reseed = Fit(pending, segStartK, segStartT);
            if(reseed >= minPeriod && reseed <= maxPeriod
               && MaxResidual(pending, segStartK, segStartT, reseed) <= o.ToleranceSeconds) {
                r.OutliersIgnored += accepted.Count;
                accepted = [.. pending];
                segPeriod = reseed;
                tail = reseed;
                lastAccepted = pending[^1];
            } else {
                r.OutliersIgnored += pending.Count;
            }

            pending.Clear();

            if(strikes > o.MaxStrikes) {
                // The beats are not where any single tempo puts them and never settle. A difficult
                // track, or a detection that has gone wrong; either way, guessing further is worse
                // than stopping.
                r.SpansAbandoned++;
                return;
            }
        }
    }

    // Prefers a bar line to break on, but only one the shortened segment still fits - so the
    // cosmetic choice cannot degrade the correction.
    private static Obs SnapBack(Options o, List<Obs> accepted, int segStartK, double segStartT,
                                double minPeriod, double maxPeriod) {
        for(int j = accepted.Count - 1; j >= 0 && accepted.Count - j <= BeatGrid.BeatsPerBar; j--) {
            if((accepted[j].K - segStartK) % BeatGrid.BeatsPerBar != 0) continue;
            if(accepted[j].K - segStartK < o.MinSegmentBeats) continue;

            List<Obs> prefix = accepted.GetRange(0, j + 1);
            double slope = Fit(prefix, segStartK, segStartT);
            if(slope < minPeriod || slope > maxPeriod) continue;
            if(MaxResidual(prefix, segStartK, segStartT, slope) > o.ToleranceSeconds) continue;

            return accepted[j];
        }

        return accepted[^1];
    }

    // Least squares through the segment's start, which is fixed - so one parameter, the beat
    // period. Fitting the intercept too would let the segment start drift, and its start is a
    // guard rail.
    private static double Fit(List<Obs> obs, int segStartK, double segStartT) {
        double num = 0, den = 0;

        foreach(Obs x in obs) {
            double dk = x.K - segStartK;
            num += dk * (x.T - segStartT);
            den += dk * dk;
        }

        return den > 0 ? num / den : 0;
    }

    private static double MaxResidual(List<Obs> obs, int segStartK, double segStartT, double period) {
        double worst = 0;
        foreach(Obs x in obs) worst = Math.Max(worst, Math.Abs(x.T - (segStartT + (x.K - segStartK) * period)));
        return worst;
    }

    // Strongest onset nearest the prediction. Both halves matter: nearest alone lets a weak stray
    // beat a solid kick 25 ms away, and 25 ms is exactly the drift worth correcting; strongest
    // alone picks the loudest thing in the window whether or not it is on the beat.
    private static bool TryMatch(Onset[] onsets, double at, double window, out Onset best) {
        best = default;
        if(!(window > 0)) return false;

        int lo = LowerBound(onsets, at - window);
        double bestScore = double.NegativeInfinity;
        bool found = false;

        for(int i = lo; i < onsets.Length && onsets[i].Seconds <= at + window; i++) {
            double score = (1.0 + Math.Max(0, onsets[i].Strength))
                           * (1.0 - Math.Abs(onsets[i].Seconds - at) / window);
            if(score <= bestScore) continue;

            bestScore = score;
            best = onsets[i];
            found = true;
        }

        return found;
    }

    private static int LowerBound(Onset[] onsets, double seconds) {
        int lo = 0, hi = onsets.Length;

        while(lo < hi) {
            int mid = lo + (hi - lo) / 2;
            if(onsets[mid].Seconds < seconds) lo = mid + 1; else hi = mid;
        }

        return lo;
    }

    private static string Describe(Report r, Options o) {
        if(r.BeatsChecked == 0) return "There was nothing after the downbeat to check.";
        if(r.BeatsMatched == 0) return "No beats were found where the grid expected them, so nothing was changed.";

        List<string> parts = [$"matched {r.BeatsMatched} of {r.BeatsChecked} beats"];

        if(r.NominalBPM > 0) parts.Add($"set the BPM to {r.NominalBPM:0.##}");

        if(r.AnchorsAdded > 0) {
            parts.Add($"added {r.AnchorsAdded} marker{(r.AnchorsAdded == 1 ? "" : "s")}");
        } else if(r.SegmentsRetimed > 0) {
            parts.Add("corrected the tempo");
        } else if(r.NominalBPM == 0) {
            parts.Add($"already within {o.ToleranceSeconds * 1000:F0} ms - nothing to correct");
        }

        // As a rate as well as a total. Constant drift makes the total grow with the length of the
        // track, which reads like a huge correction when it is a steady, tiny one.
        if(r.Changed && r.BeatsMatched > 0 && r.WorstDriftSeconds > 0) {
            parts.Add($"drift {r.WorstDriftSeconds / Math.Max(1, r.BeatsChecked) * 1000:0.##} ms per beat "
                      + $"({r.WorstDriftSeconds * 1000:F0} ms by the end)");
        }

        if(r.SpansAbandoned > 0) parts.Add($"gave up on {r.SpansAbandoned} section{(r.SpansAbandoned == 1 ? "" : "s")}");

        return string.Join(", ", parts) + ".";
    }
}
