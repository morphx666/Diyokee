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
    public readonly record struct Anchor(double Position, double BPM, bool IsDownbeat, bool IsReference = false);

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
        List<Anchor> anchors = [.. file.BeatGridMarkers.Select(m => new Anchor(m.Position, m.BPM, m.IsDownbeat, m.IsReference))];

        // No anchors of its own, which is every track until one is edited: fall back to the pair
        // analysis produces. This is the ONLY compatibility mechanism, and it is why there is one
        // code path here rather than a legacy one and a new one.
        if(anchors.Count == 0 && file.DownbeatAt >= 0) anchors.Add(new Anchor(file.DownbeatAt, file.BPM, true, true));

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

        // Backwards from the first anchor, down to the start of the track, at the NOMINAL tempo -
        // deliberately not segment 0's.
        //
        // A drag whose previous anchor is the first one re-times segment 0. If the extrapolation
        // followed that tempo, every beat before the first anchor would move with it: on a track
        // whose downbeat is 8 s in, that is the whole intro re-spacing because something 30 s away
        // was corrected. The first anchor is a guard rail like any other, and nothing behind a
        // guard rail may move.
        //
        // The fallback keeps the harness's three-argument constructor honest, and for an unedited
        // track the two tempos are the same number anyway, which is why check 1 still matches the
        // pre-BeatGrid grid bit for bit.
        double back = nominalBPM > 0 ? 60.0 / nominalBPM : SecondsPerBeat(0);
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

        // The backward run is phased BACKWARDS from the downbeat, so the beat just before it is the
        // last of a bar and the one four back is a downbeat. Counting forward from the earliest
        // extrapolated beat instead - which is what this used to do - left a short bar wherever the
        // backward count was not a multiple of BeatsPerBar, and put a stray downbeat line a few
        // beats before the real one.
        int bar = ((-before.Count) % BeatsPerBar + BeatsPerBar) % BeatsPerBar;
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

    // ---------------------------------------------------------------- editing
    //
    // Pure functions over the anchor list, so the operations can be checked in tools/gridtest
    // without a UI. They mutate the list in place and keep each anchor's identity, because those
    // ids are database rows - see DFile.ApplyEditsFrom.

    // The beat counts either side of the anchor being dragged, frozen when the drag STARTS.
    // Re-deriving them on every mouse-move lets the rounding flip mid-drag, which makes the
    // marker visibly jump under the hand. OriginalBPM is kept so a drag can be cancelled.
    public readonly record struct Bend(int Index, double PrevBeats, double NextBeats,
                                       double Lower, double Upper,
                                       double OriginalPosition, double OriginalPrevBPM, double OriginalBPM);

    // Smallest a segment may become. A segment that reaches zero length is a division by zero and
    // an anchor that has swallowed its neighbour; half a beat at 200 BPM is already absurd.
    private const double MinSegmentSeconds = 0.15;

    public static Bend BeginBend(List<DFile.BeatGridMarker> anchors, int index) {
        DFile.BeatGridMarker a = anchors[index];

        double prevBeats = 0, nextBeats = 0;
        double lower = 0, upper = double.PositiveInfinity;

        if(index > 0) {
            DFile.BeatGridMarker p = anchors[index - 1];
            prevBeats = Math.Round((a.Position - p.Position) * p.BPM / 60.0);
            lower = p.Position + MinSegmentSeconds;
        }

        if(index + 1 < anchors.Count) {
            DFile.BeatGridMarker n = anchors[index + 1];
            nextBeats = Math.Round((n.Position - a.Position) * a.BPM / 60.0);
            upper = n.Position - MinSegmentSeconds;
        }

        return new Bend(index, prevBeats, nextBeats, lower, upper,
                        a.Position, index > 0 ? anchors[index - 1].BPM : 0, a.BPM);
    }

    // Moves the anchor and re-times the segments either side so both keep the beat count they had.
    // Dragging right therefore SLOWS the section leading up to the anchor and speeds up the one
    // after it - which is what "stretch this bit to line up" means.
    //
    // The neighbors do not move, so a bend is local: it cannot disturb a part of the track that
    // has already been gridded.
    public static void ApplyBend(List<DFile.BeatGridMarker> anchors, Bend bend, double position) {
        if(bend.Index < 0 || bend.Index >= anchors.Count) return;

        position = Math.Clamp(position, bend.Lower, bend.Upper);
        if(!double.IsFinite(position)) return;

        DFile.BeatGridMarker a = anchors[bend.Index];
        a.Position = position;

        if(bend.Index > 0 && bend.PrevBeats > 0) {
            DFile.BeatGridMarker p = anchors[bend.Index - 1];
            p.BPM = 60.0 * bend.PrevBeats / (position - p.Position);
        }

        if(bend.Index + 1 < anchors.Count && bend.NextBeats > 0) {
            a.BPM = 60.0 * bend.NextBeats / (anchors[bend.Index + 1].Position - position);
        }
    }

    // Dragging a beat line, which is the only gesture the grid editor has.
    //
    // Grabbing a beat PINS it: an anchor is created there if one is not already, so the drag
    // re-times only the stretch back to the previous anchor and forward to the next one. Without
    // that pin a drag re-times everything back to the last anchor, so correcting beat 500 silently
    // un-corrects beats 1-499 - you could never fix a track whose drift is not perfectly linear.
    //
    // The user never places, names or deletes these. Dragging a beat is the only way one appears
    // and dragging it back is the only way one goes, which is what keeps the anchors an invisible
    // consequence of the gesture rather than a thing to manage.
    public readonly record struct BeatDrag(Bend Bend, int Index, bool Created) {
        public static readonly BeatDrag None = new(default, -1, false);
        public bool IsValid => Index >= 0;
    }

    // `beatSeconds` is the beat the user grabbed, as it stands right now.
    public BeatDrag BeginBeatDrag(List<DFile.BeatGridMarker> anchors, double beatSeconds) {
        if(anchors.Count == 0) return BeatDrag.None;

        int index = anchors.FindIndex(a => Math.Abs(a.Position - beatSeconds) < 1e-6);

        // A reference is draggable like any other beat. "Immovable" is about what a reference
        // protects, not about the reference itself: dragging it re-times the two segments either
        // side, so beats between it and the PREVIOUS anchor move - and that previous anchor is the
        // guard rail for this drag. Nothing behind it can be reached.
        bool created = index < 0;
        if(created) index = Insert(anchors, beatSeconds, TempoAt(beatSeconds), false);

        return new BeatDrag(BeginBend(anchors, index), index, created);
    }

    public static void ApplyBeatDrag(List<DFile.BeatGridMarker> anchors, BeatDrag drag, double position) {
        if(drag.IsValid) ApplyBend(anchors, drag.Bend, position);
    }

    // Called when a drag finishes. Removes ONLY the anchor that this drag created, and only if it
    // turned out to say nothing - a tempo matching the segment before it is not a tempo change.
    // That way a press that moves nothing, or a drag taken back to where it started, leaves no
    // trace, and the user cannot accumulate invisible state by fidgeting.
    //
    // Deliberately targeted rather than sweeping the whole list: a pin the user placed on purpose
    // also has the tempo of the segment before it - that is exactly what makes it a pin and not a
    // tempo change - so a general sweep would delete every pin the moment anything was dragged.
    public static void PruneDrag(List<DFile.BeatGridMarker> anchors, BeatDrag drag) {
        if(!drag.Created || drag.Index <= 0 || drag.Index >= anchors.Count) return;
        if(anchors[drag.Index].IsDownbeat || anchors[drag.Index].IsReference) return;

        if(Math.Abs(anchors[drag.Index].BPM - anchors[drag.Index - 1].BPM) < 1e-6) anchors.RemoveAt(drag.Index);
    }

    // Pins a beat, or unpins one already pinned.
    //
    // A pin is just an anchor carrying the tempo already in force, so it changes nothing you can
    // hear or see - it exists to say "everything before here is correct". Because a bend only ever
    // re-times the two segments either side of the anchor being dragged, an anchor is a wall:
    // dragging any later beat cannot reach past it. That is the whole mechanism.
    //
    // Returns true if a pin was added, false if one was removed.
    public bool TogglePin(List<DFile.BeatGridMarker> anchors, double beatSeconds) {
        int at = anchors.FindIndex(a => Math.Abs(a.Position - beatSeconds) < 1e-6);

        if(at >= 0) {
            // The first anchor is the track's downbeat and is what the whole grid hangs from;
            // removing it would leave the grid with nothing to reference.
            if(at > 0) anchors.RemoveAt(at);
            return false;
        }

        Insert(anchors, beatSeconds, TempoAt(beatSeconds), false, isReference: true);
        return true;
    }

    // Every anchor moves by the same amount, tempos untouched. "The whole track is 18 ms early."
    public static void ShiftAll(List<DFile.BeatGridMarker> anchors, double delta) {
        foreach(DFile.BeatGridMarker a in anchors) a.Position += delta;
    }

    // This anchor and every later one move, tempos untouched. The fix for a splice or an edit
    // point where the track jumps but the tempo does not.
    public static void ShiftTail(List<DFile.BeatGridMarker> anchors, int index, double delta) {
        for(int i = index; i < anchors.Count; i++) anchors[i].Position += delta;
    }

    // A new anchor, inserted in order. Its tempo defaults to whatever was already in force there,
    // so dropping one changes nothing until it is bent or its BPM is set - you place it, then edit.
    public static int Insert(List<DFile.BeatGridMarker> anchors, double position, double bpm,
                            bool isDownbeat, bool isReference = false) {
        int at = anchors.FindIndex(m => m.Position > position);
        if(at < 0) at = anchors.Count;

        anchors.Insert(at, new DFile.BeatGridMarker {
            Position = position,
            BPM = bpm,
            IsDownbeat = isDownbeat,
            IsReference = isReference
        });
        return at;
    }

    // The previous segment simply extends to the next anchor, keeping its own tempo.
    public static void Delete(List<DFile.BeatGridMarker> anchors, int index) {
        if(index >= 0 && index < anchors.Count) anchors.RemoveAt(index);
    }

    // ---------------------------------------------------------------- the warp
    //
    // The point of gridding a drifting track is to play it back locked to a quantised beat, which
    // means varying the playback SPEED per segment. These are that map. Nothing here touches audio;
    // they are the arithmetic both the warped waveform view and, later, the playback stage read -
    // deliberately the same functions, so what you see and what you would hear cannot disagree.
    //
    // Two timelines:
    //   source time - where a sample actually is in the file
    //   grid time   - where it WOULD be if the track had been played at TargetBPM throughout
    //
    // Segment i has actual tempo B. To make it sound like T, one source beat of 60/B seconds must
    // come out 60/T seconds long, so grid time advances at B/T per source second and the playback
    // rate - source consumed per output second - is T/B. A segment detected at 127 against a target
    // of 128 plays at 1.0079x.
    //
    // The first anchor is pinned: grid time and source time agree there, so correcting drift does
    // not shift the whole track. Audio before it warps at the first segment's rate, extrapolated
    // backwards, exactly as its beats do.

    // The uniform tempo the track is corrected onto. The nominal BPM analysis found, which is what
    // makes an unedited track's warp the identity: one anchor at the nominal tempo means every
    // segment rate is T/T = 1, so nothing moves until a second anchor says the track drifts.
    public double TargetBPM => nominalBPM;

    public bool IsWarped {
        get {
            if(!(nominalBPM > 0) || anchors.Count == 0) return false;
            return anchors.Any(a => Math.Abs(a.BPM - nominalBPM) > 1e-9);
        }
    }

    // Grid time of each segment's start, accumulated so a segment begins where the previous one
    // left off. Built lazily because most tracks never warp.
    private double[]? gridStarts;

    private double[] GridStarts() {
        if(gridStarts != null) return gridStarts;

        double[] starts = new double[anchors.Count];
        if(anchors.Count > 0) starts[0] = anchors[0].Position;      // the pin

        for(int i = 1; i < anchors.Count; i++) {
            starts[i] = starts[i - 1] + (anchors[i].Position - anchors[i - 1].Position) * SlopeOf(i - 1);
        }

        return gridStarts = starts;
    }

    private double SlopeOf(int segment) {
        double bpm = anchors[segment].BPM;
        return bpm > 0 && nominalBPM > 0 ? bpm / nominalBPM : 1.0;
    }

    // Slope at a POSITION, which is not the same as the slope of the segment SegmentAt would name.
    //
    // Everything before the first anchor belongs to segment 0 for the purpose of "how long is a
    // beat here", because that is the only tempo there is to extrapolate with. But its beats are
    // DRAWN at the nominal tempo, deliberately, so that no drag can move them - and if it were
    // warped at segment 0's rate it would be played at a tempo the grid on screen does not show.
    // That is a picture and a sound disagreeing about the same audio.
    //
    // So the region before the first anchor is not warped at all. The first anchor is where
    // correction starts, which is what makes it a guard rail in the audio as well as in the grid.
    private double SlopeAt(double seconds) {
        if(anchors.Count == 0 || seconds < anchors[0].Position) return 1.0;
        return SlopeOf(SegmentAt(seconds));
    }

    // Playback rate for the segment containing `sourceSeconds`: what the source has to be consumed
    // at, relative to normal, for that stretch to come out at TargetBPM. This is the number that
    // becomes playback speed.
    public double PlaybackRateAt(double sourceSeconds) {
        double slope = SlopeAt(sourceSeconds);
        return slope > 0 ? 1.0 / slope : 1.0;
    }

    public double ToGridTime(double sourceSeconds) {
        if(anchors.Count == 0 || !(nominalBPM > 0)) return sourceSeconds;

        // Unwarped before the first anchor, so grid time and source time agree there - the same
        // reason PlaybackRateAt leaves it alone.
        if(sourceSeconds < anchors[0].Position) return sourceSeconds;

        int segment = SegmentAt(sourceSeconds);
        return GridStarts()[segment] + (sourceSeconds - anchors[segment].Position) * SlopeOf(segment);
    }

    public double FromGridTime(double gridSeconds) {
        if(anchors.Count == 0 || !(nominalBPM > 0)) return gridSeconds;

        double[] starts = GridStarts();
        if(gridSeconds < starts[0]) return gridSeconds;

        int lo = 0, hi = starts.Length - 1, segment = 0;
        while(lo <= hi) {
            int mid = lo + (hi - lo) / 2;
            if(starts[mid] <= gridSeconds) {
                segment = mid;
                lo = mid + 1;
            } else {
                hi = mid - 1;
            }
        }

        double slope = SlopeOf(segment);
        return anchors[segment].Position + (gridSeconds - starts[segment]) / slope;
    }

    // ----------------------------------------------------------------

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
