using System.Reflection;
using System.Security.Cryptography;
using Un4seen.Bass;
using Un4seen.Bass.AddOn.Midi;

namespace Diyokee {
    public class MidiTools {
        public delegate void MidiEvent(string propertyName, string section, MidiControllerProfile.MidiMapping mapping, BASS_MIDI_EVENT midiEvent);

        // Declared nullable because "-=" compiles to Delegate.Remove, which returns null once the
        // last handler is removed - assigning that back to a non-nullable field is what produced
        // the CS8601 warning that previously made unsubscribing look impossible.
        public event MidiEvent? OnMidiEvent;

        private MIDIINPROC midiProc = default!;
        private int midiStream = -1;

        // Dispatch used to walk the whole profile by reflection for every event that arrived:
        // General (8 properties), both players (25 each) and Keyboard (5), so roughly 63 GetValue
        // calls and four fresh GetProperties() arrays per event. That was sized for buttons. It is
        // the wrong shape for a platter, which fires hundreds of times a second - and on a
        // controller like the DDJ-SB3, which sends 14-bit CC pairs, about half of those events are
        // LSBs that match nothing and paid the full sweep anyway (measured: 152 of 316 events in a
        // 45s capture). The cost that matters is not CPU but jitter on the MIDI callback thread,
        // whose timestamps are the velocity ground truth for scratching.
        //
        // The mappings are known the moment a profile is chosen, so they are indexed once instead.
        // What a match depends on that is fixed per mapping - event type and channel - becomes the
        // key; what varies per event is checked on the way out.
        private enum MatchOn {
            Channel,        // the key is the whole test
            Parameter,      // General notes match note and velocity together, as they always have
            Controller,
            Note,
            KeyboardRange,  // inside the profile's own playable range
        }

        private readonly record struct DispatchEntry(string PropertyName, string Section, MidiControllerProfile.MidiMapping Mapping, MatchOn MatchOn);

        private Dictionary<int, List<DispatchEntry>> dispatchTable = [];
        private MidiControllerProfile dispatchProfile = new();

        private static int DispatchKey(BASSMIDIEvent eventType, int channel) => ((int)eventType << 8) | (channel & 0xFF);

        private static IEnumerable<PropertyInfo> MappingProperties(Type type) {
            return type.GetProperties().Where(p => p.PropertyType == typeof(MidiControllerProfile.MidiMapping));
        }

        // Built once per Start(), which is also the only point at which the running profile can
        // change - so this is exactly as fresh as the closure it replaces, and no staler.
        internal void BuildDispatchTable(MidiControllerProfile profile) {
            Dictionary<int, List<DispatchEntry>> table = [];
            dispatchProfile = profile;

            void Add(object owner, PropertyInfo prop, string section, Func<BASSMIDIEvent, MatchOn?> matchFor) {
                // The Players loop always filtered by property type, because PlayerMapping carries
                // an int Index; General and Keyboard did not, so any non-mapping property added to
                // either would have thrown on the MIDI thread. All three filter now. A null value
                // is possible too, from a profile JSON that names a mapping and leaves it null.
                if(prop.GetValue(owner) is not MidiControllerProfile.MidiMapping mapping) return;

                // Neither can ever match a real event: none carries MIDI_EVENT_NONE, and a channel
                // outside 0-15 cannot equal the chan of one. Dropping them here keeps unconfigured
                // mappings - the common case - out of the buckets entirely.
                if(mapping.EventType == BASSMIDIEvent.MIDI_EVENT_NONE) return;
                if(mapping.Channel < 0 || mapping.Channel > 15) return;

                if(matchFor(mapping.EventType) is not MatchOn matchOn) return;

                int key = DispatchKey(mapping.EventType, mapping.Channel);
                if(!table.TryGetValue(key, out List<DispatchEntry>? entries)) table[key] = entries = [];
                entries.Add(new DispatchEntry(prop.Name, section, mapping, matchOn));
            }

            // Order within a bucket is the order the sweep visited them in - General, then each
            // player, then Keyboard - because more than one mapping legitimately matches a single
            // event and every one of them is dispatched. The XDJ-RR profile relies on that:
            // BrowseUp and BrowseDown are both controller 79, told apart by value in MainConsole.
            foreach(PropertyInfo prop in MappingProperties(typeof(MidiControllerProfile.GeneralMapping))) {
                Add(profile.General, prop, "general", t => t switch {
                    BASSMIDIEvent.MIDI_EVENT_NOTE => MatchOn.Parameter,
                    BASSMIDIEvent.MIDI_EVENT_EXPRESSION => MatchOn.Channel,
                    BASSMIDIEvent.MIDI_EVENT_CONTROL => MatchOn.Controller,
                    _ => null,
                });
            }

            foreach(MidiControllerProfile.PlayerMapping player in profile.Players) {
                foreach(PropertyInfo prop in MappingProperties(typeof(MidiControllerProfile.PlayerMapping))) {
                    Add(player, prop, $"player{player.Index}", t => t switch {
                        BASSMIDIEvent.MIDI_EVENT_NOTE => MatchOn.Note,
                        BASSMIDIEvent.MIDI_EVENT_CONTROL => MatchOn.Controller,
                        _ => MatchOn.Channel,
                    });
                }
            }

            foreach(PropertyInfo prop in MappingProperties(typeof(MidiControllerProfile.KeyboardMapping))) {
                Add(profile.Keyboard, prop, "keyboard", t => t switch {
                    BASSMIDIEvent.MIDI_EVENT_VOLUME => MatchOn.Channel,
                    BASSMIDIEvent.MIDI_EVENT_MODULATION => MatchOn.Channel,
                    BASSMIDIEvent.MIDI_EVENT_NOTE => MatchOn.KeyboardRange,
                    BASSMIDIEvent.MIDI_EVENT_PITCH => MatchOn.Channel,
                    _ => null,
                });
            }

            dispatchTable = table;
        }

        internal void DispatchEvent(BASS_MIDI_EVENT midiEvent) {
            bool handled = false;

            if(dispatchTable.TryGetValue(DispatchKey(midiEvent.eventtype, midiEvent.chan), out List<DispatchEntry>? entries)) {
                for(int i = 0; i < entries.Count; i++) {
                    DispatchEntry entry = entries[i];
                    if(!Matches(entry, midiEvent)) continue;
                    handled = true;
                    OnMidiEvent?.Invoke(entry.PropertyName, entry.Section, entry.Mapping, midiEvent);
                }
            }

            if(!handled) OnMidiEvent?.Invoke("unknown", "", null!, midiEvent);
        }

        private bool Matches(DispatchEntry entry, BASS_MIDI_EVENT midiEvent) {
            switch(entry.MatchOn) {
                case MatchOn.Channel:
                    return true;
                case MatchOn.Parameter:
                    return entry.Mapping.Parameter == midiEvent.param;
                case MatchOn.Controller:
                    return entry.Mapping.Controller == (midiEvent.param & 0xFF);
                case MatchOn.Note:
                    return entry.Mapping.Note == (midiEvent.param & 0xFF);
                case MatchOn.KeyboardRange:
                    // Read live rather than captured at build time, so editing the range in place
                    // behaves as it did when this was a closure over the profile.
                    int keyNumber = midiEvent.param & 0xFF;
                    return keyNumber >= dispatchProfile.Keyboard.FirstKey.Note
                        && keyNumber <= dispatchProfile.Keyboard.LastKey.Note;
                default:
                    return false;
            }
        }

        public void Start() {
            // Indexing [0] here threw ArgumentOutOfRangeException whenever no profiles were loaded
            // - which the settings dialog can produce, since deleting the last profile is allowed
            // - and it ran before the null check below could supply the empty profile that check
            // was plainly meant to provide. Falling back through FirstOrDefault() keeps that
            // intent, and the device still opens: every event then arrives unmapped instead of
            // taking the console down mid-render.
            MidiControllerProfile profile = Program.MidiControllersProfiles.FirstOrDefault(p => p.Name == Program.Settings.MidiProfileName)
                                            ?? Program.MidiControllersProfiles.FirstOrDefault()
                                            ?? new();
            if(Program.MidiControllersProfiles.Count == 0) Program.Logger?.LogWarning("No MIDI controller profiles loaded - no MIDI input will be mapped");
            if(midiStream != -1) Stop();

            BuildDispatchTable(profile);

            midiStream = BassMidi.BASS_MIDI_StreamCreate(16, 0, 0);

            midiProc = (device, time, buffer, length, user) => {
                byte[] bytes = new byte[length];
                System.Runtime.InteropServices.Marshal.Copy(buffer, bytes, 0, length);

                BASS_MIDI_EVENT[] midiEvents = BassMidi.BASS_MIDI_ConvertEvents(bytes, BASSMIDIEventMode.BASS_MIDI_EVENTS_STRUCT);
                if(midiEvents != null) {
                    foreach(BASS_MIDI_EVENT midiEvent in midiEvents) DispatchEvent(midiEvent);
                }

                BassMidi.BASS_MIDI_StreamEvents(midiStream, BASSMIDIEventMode.BASS_MIDI_EVENTS_RAW, 0, buffer, length);
            };

            // TODO: Handle multiple MIDI devices - should we listen to all devices or just a specific one?
            BASS_MIDI_DEVICEINFO[] midiDevices = GetMidiDevices();
            int deviceIndex = midiDevices.ToList().FindIndex(d => d.name == Program.Settings.MidiDeviceName);

            // Every failure here used to be silent, which is why a controller that Windows listed
            // and BASS could not see looked exactly like one that was simply not configured. Say
            // which it was, and always name what BASS can actually see.
            string available = midiDevices.Length == 0 ? "none" : string.Join(", ", midiDevices.Select(d => $"'{d.name}'"));

            if(deviceIndex == -1) {
                if(Program.Settings.MidiDeviceName == "") {
                    Program.Logger?.LogInformation($"No MIDI controller selected - MIDI inputs available: {available}");
                } else {
                    Program.Logger?.LogWarning($"MIDI controller '{Program.Settings.MidiDeviceName}' not found - MIDI inputs available: {available}");
                }
                return;
            }

            if(midiDevices[deviceIndex].IsInitialized) return;

            if(!BassMidi.BASS_MIDI_InInit(deviceIndex, midiProc, IntPtr.Zero)) {
                Program.Logger?.LogError($"Failed to open MIDI controller '{midiDevices[deviceIndex].name}': {Bass.BASS_ErrorGetCode()}");
                return;
            }
            if(!BassMidi.BASS_MIDI_InStart(deviceIndex)) {
                Program.Logger?.LogError($"Failed to start MIDI controller '{midiDevices[deviceIndex].name}': {Bass.BASS_ErrorGetCode()}");
                return;
            }

            Program.Logger?.LogInformation($"MIDI controller '{midiDevices[deviceIndex].name}' listening, using profile '{profile.Name}'");
        }

        public void Stop() {
            if(midiStream == -1) return;

            Bass.BASS_StreamFree(midiStream);
            midiStream = -1;

            BASS_MIDI_DEVICEINFO[] midiDevices = GetMidiDevices();
            int deviceIndex = midiDevices.ToList().FindIndex(d => d.name == Program.Settings.MidiDeviceName);
            if(deviceIndex != -1) {
                if(midiDevices[deviceIndex].IsInitialized) {
                    BassMidi.BASS_MIDI_InStop(deviceIndex);
                    BassMidi.BASS_MIDI_InFree(deviceIndex);
                }
            }
        }

        internal static BASS_MIDI_DEVICEINFO[] GetMidiDevices() {
            int inMidiDevicesCount = BassMidi.BASS_MIDI_InGetDeviceInfos();
            BASS_MIDI_DEVICEINFO[] midiDevices = new BASS_MIDI_DEVICEINFO[inMidiDevicesCount];
            for(int i = 0; i < inMidiDevicesCount; i++) {
                midiDevices[i] = BassMidi.BASS_MIDI_InGetDeviceInfo(i);

            }
            return midiDevices;
        }
    }
}
