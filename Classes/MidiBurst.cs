using Un4seen.Bass.AddOn.Midi;

namespace Diyokee {
    // Learning a button needs one event. Learning a platter needs many, and not because one event
    // is imprecise - because one event cannot answer the question at all. A single value says where
    // the control landed, never how far it moved, and it certainly cannot say how a tick count was
    // signed: +1 is 0x01 in all three relative formats.
    //
    // So the learn flow collects a burst while the control is worked, and this reads its shape.
    //
    // What it deliberately does not do is guess whether a control is absolute or relative. That
    // guess is unreliable in both directions - a fader swept in coarse jumps and a platter spun
    // hard produce very similar distributions - and it is also unnecessary, because the thing being
    // learned is already known: JogWheel is a platter, everything else is not. The caller says
    // which, and the burst answers the questions that genuinely need data: which control moved, how
    // its ticks are signed, and how many of them there are in one turn.
    public static class MidiBurst {
        public readonly record struct Sample(BASSMIDIEvent EventType, int Channel, int Param);

        public readonly record struct Result(
            BASSMIDIEvent EventType,
            int Channel,
            int Selector,       // controller or note number; -1 when the event type is the identity
            int SampleCount,
            MidiControllerProfile.MappingModes Mode,
            MidiControllerProfile.RelativeFormats Format,
            int NetTicks,       // signed total, decoded with Format
            int TotalTicks,     // total distance turned, both directions counted
            int TicksPerRevolution,  // the furthest it got from where it started - see Revolution
            int ModalParam,     // the commonest raw param, for the mappings that match on it
            bool FormatIsCertain,
            string Summary);

        // A control is identified by its event type and channel, plus the controller or note
        // number for the two event types that carry one.
        private static int Selector(Sample s) {
            return s.EventType is BASSMIDIEvent.MIDI_EVENT_CONTROL or BASSMIDIEvent.MIDI_EVENT_NOTE
                    ? s.Param & 0xFF
                    : -1;
        }

        private static int Value(Sample s) {
            return s.EventType is BASSMIDIEvent.MIDI_EVENT_CONTROL or BASSMIDIEvent.MIDI_EVENT_NOTE
                    ? (s.Param >> 8) & 0xFF
                    : s.Param;
        }

        /// <param name="relative">True when the mapping being learned is a platter.</param>
        /// <param name="exclude">
        /// A control that is known to be something else, and must not win. This exists for the
        /// platter pair: one turn of the wheel with the plate held reports on BOTH the top plate's
        /// CC and the rim's, in near-equal numbers, so whichever happened to arrive more often won
        /// and the two mappings could silently learn the same CC. Whichever of the pair is learned
        /// second passes the first, and the ambiguity disappears.
        /// </param>
        public static Result? Analyse(IReadOnlyList<Sample> samples, bool relative,
                                      (BASSMIDIEvent Type, int Channel, int Selector)? exclude = null) {
            if(samples.Count == 0) return null;

            // Whatever moved most is the control being learned. A burst also picks up everything
            // else the controller happens to be saying - the LSB partner of a 14-bit pair, a level
            // meter feeding back - and none of that is what the user just touched.
            List<IGrouping<(BASSMIDIEvent Type, int Channel, int Selector), Sample>> groups = samples
                .GroupBy(s => (Type: s.EventType, Channel: s.Channel, Selector: Selector(s)))
                .OrderByDescending(g => g.Count())
                .ToList();

            // Dropped before the winner is picked, never after - the point is to let the runner-up
            // win. Never drops the last group standing, so an exclusion that turns out to match
            // everything leaves the old behaviour rather than failing the learn.
            if(exclude is { } skip) {
                List<IGrouping<(BASSMIDIEvent Type, int Channel, int Selector), Sample>> kept =
                    groups.Where(g => g.Key != skip).ToList();
                if(kept.Count > 0) groups = kept;
            }

            IGrouping<(BASSMIDIEvent Type, int Channel, int Selector), Sample> top = groups[0];

            // A 14-bit control sends its MSB on CC n and its LSB on CC n+32, one of each per move,
            // so the two groups tie and the winner comes down to ordering. The MSB is the one worth
            // mapping: it carries the whole range by itself, where the LSB wraps every step and
            // would give a control that jitters rather than sweeps. This is also what stops a learn
            // binding to the wrong half of a pair on a controller that sends the LSB first.
            if(groups.Count > 1) {
                IGrouping<(BASSMIDIEvent Type, int Channel, int Selector), Sample> second = groups[1];
                if(second.Count() == top.Count()
                    && second.Key.Type == top.Key.Type
                    && second.Key.Channel == top.Key.Channel
                    && top.Key.Selector - second.Key.Selector == 32) {
                    top = second;
                }
            }

            (BASSMIDIEvent type, int channel, int selector) = top.Key;
            List<int> values = top.Select(Value).ToList();

            // Several mappings still match on the whole param - General notes, and the encoder pair
            // BrowseUp/BrowseDown, which are one controller told apart by value - so the burst has
            // to keep one. The commonest is the right one: for an encoder held one way every event
            // carries the same value, which is exactly what a single-event learn used to record,
            // and for a fader nothing reads it.
            int modalParam = top.GroupBy(s => s.Param).OrderByDescending(g => g.Count()).First().Key;

            string what = selector >= 0 ? $"{Name(type)} {selector} ch {channel}" : $"{Name(type)} ch {channel}";

            if(!relative || type == BASSMIDIEvent.MIDI_EVENT_NOTE) {
                string detail = type == BASSMIDIEvent.MIDI_EVENT_NOTE
                        ? $"{values.Count} event(s)"
                        : $"{values.Count} events, values {values.Min()}-{values.Max()}";
                return new Result(type, channel, selector, values.Count,
                                  MidiControllerProfile.MappingModes.Absolute,
                                  MidiControllerProfile.RelativeFormats.TwosComplement,
                                  0, 0, 0, modalParam, true, $"{what}, {detail}");
            }

            (MidiControllerProfile.RelativeFormats format, bool certain) = InferFormat(values);
            int net = values.Sum(v => MidiControllerProfile.DecodeRelative(v, format));
            int total = values.Sum(v => Math.Abs(MidiControllerProfile.DecodeRelative(v, format)));
            int perRevolution = Revolution(values, format);

            // Not a gate, a warning. Arm the platter and then sweep a fader by mistake and every
            // number below is meaningless; nothing else in the app would ever say so.
            string caveat = LooksAbsolute(values) ? " - WARNING: looks like an absolute control, not a platter"
                          : !certain ? " - turned one way only, so the format is a guess"
                          : "";

            return new Result(type, channel, selector, values.Count,
                              MidiControllerProfile.MappingModes.Relative, format, net, total, perRevolution,
                              modalParam, certain,
                              $"{what}, relative {FormatName(format)}, {values.Count} events, "
                              + $"{total} ticks turned, {perRevolution} per revolution{caveat}");
        }

        // What one revolution is worth, which is the number the platter's whole feel is geared to.
        //
        // It cannot be the total distance turned, and that is the trap this used to fall into. The
        // prompt asks for a revolution AND THE RETURN, because turning both ways is the only thing
        // that pins the format down - see InferFormat - so the total covers two revolutions. Using
        // it geared every platter ever learned to half the distance the hand actually moved: the
        // scratch tracked the hand perfectly and simply did it at half speed, which does not read
        // as a scale error, it reads as the engine being sluggish.
        //
        // So measure instead of halving. A running sum of the ticks climbs to one revolution and
        // comes back, and the height it reached is the answer. A user who turns one way and stops
        // gives that same answer from the same code, which is why this beats dividing by two - and
        // jitter that reverses for a tick or two cancels here where the total would add it in.
        private static int Revolution(List<int> values, MidiControllerProfile.RelativeFormats format) {
            long running = 0, high = 0, low = 0;

            foreach(int v in values) {
                running += MidiControllerProfile.DecodeRelative(v, format);
                if(running > high) high = running;
                if(running < low) low = running;
            }

            return (int)(high - low);
        }

        // Used only to warn, never to decide. An absolute control sweeps: it travels a long way, it
        // visits most of what it travels, and it gets there without doubling back more than a
        // couple of times. A platter fails at least one of those however hard it is spun.
        private static bool LooksAbsolute(List<int> values) {
            int span = values.Max() - values.Min();
            if(span < 24) return false;

            double density = values.Distinct().Count() / (double)(span + 1);

            long variation = 0;
            for(int i = 1; i < values.Count; i++) variation += Math.Abs(values[i] - values[i - 1]);

            return density >= 0.35 && variation / (double)span <= 3.0;
        }

        private static (MidiControllerProfile.RelativeFormats, bool) InferFormat(List<int> values) {
            List<int> above = values.Where(v => v > 64).ToList();
            List<int> below = values.Where(v => v < 64).ToList();

            // Binary offset is the one that brackets 64 from both sides: reverse is 63 and down,
            // forward is 65 and up, and neither end goes near 0 or 127 unless the platter is spun
            // hard. The other two never produce a value just below 64 for gentle motion.
            if(above.Count > 0 && below.Count > 0 && below.Min() >= 32 && above.Max() <= 96) {
                return (MidiControllerProfile.RelativeFormats.BinaryOffset, true);
            }

            // Forward is identical in the remaining two, so reverse decides - and if the platter
            // was never turned back, nothing decides. Two's complement counts down from 127,
            // signed-bit counts up from 65.
            if(above.Count == 0) return (MidiControllerProfile.RelativeFormats.TwosComplement, false);

            return above.Average() > 96
                    ? (MidiControllerProfile.RelativeFormats.TwosComplement, true)
                    : (MidiControllerProfile.RelativeFormats.SignedBit, true);
        }

        private static string Name(BASSMIDIEvent type) {
            return type switch {
                BASSMIDIEvent.MIDI_EVENT_CONTROL => "CC",
                BASSMIDIEvent.MIDI_EVENT_NOTE => "note",
                _ => type.ToString().Replace("MIDI_EVENT_", "").ToLowerInvariant(),
            };
        }

        private static string FormatName(MidiControllerProfile.RelativeFormats format) {
            return format switch {
                MidiControllerProfile.RelativeFormats.TwosComplement => "two's complement",
                MidiControllerProfile.RelativeFormats.SignedBit => "signed bit",
                _ => "binary offset",
            };
        }
    }
}
