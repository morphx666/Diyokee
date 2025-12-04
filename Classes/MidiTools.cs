using Un4seen.Bass;
using Un4seen.Bass.AddOn.Midi;

namespace Diyokee {
    public class MidiTools {
        public delegate void MidiEvent(string propertyName, string section, MidiControllerProfile.MidiMapping mapping, BASS_MIDI_EVENT midiEvent);
        public MidiEvent OnMidiEvent = default!;

        private MIDIINPROC midiProc = default!;
        private int midiStream = -1;

        public void Start() {
            var profile = Program.MidiControllersProfiles.FirstOrDefault();
            if(profile == null) profile = new();
            if(midiStream != -1) Stop();

            midiStream = BassMidi.BASS_MIDI_StreamCreate(16, 0, 0);

            midiProc = (device, time, buffer, length, user) => {
                byte[] bytes = new byte[length];
                System.Runtime.InteropServices.Marshal.Copy(buffer, bytes, 0, length);

                BASS_MIDI_EVENT[] midiEvents = BassMidi.BASS_MIDI_ConvertEvents(bytes, BASSMIDIEventMode.BASS_MIDI_EVENTS_STRUCT);
                if(midiEvents != null) {
                    foreach(BASS_MIDI_EVENT midiEvent in midiEvents) {
                        bool handled = false;
                        //Console.WriteLine($"MIDI Event {midiEvent.eventtype}: Param={(midiEvent.param & 0xFF) >> 8}-{midiEvent.param & 0xFF} Chan={midiEvent.chan} Pos={midiEvent.pos} Tick={midiEvent.tick}");

                        profile.General.GetType().GetProperties().ToList().ForEach(prop => {
                            MidiControllerProfile.MidiMapping mapping = (prop.GetValue(profile.General) as MidiControllerProfile.MidiMapping)!;

                            switch(midiEvent.eventtype) {
                                case BASSMIDIEvent.MIDI_EVENT_NOTE:
                                    if(mapping.EventType == midiEvent.eventtype &&
                                        mapping.Parameter == midiEvent.param &&
                                        mapping.Channel == midiEvent.chan) {
                                        handled = true;
                                        OnMidiEvent?.Invoke(prop.Name, "general", mapping, midiEvent);
                                    }
                                    break;
                                case BASSMIDIEvent.MIDI_EVENT_EXPRESSION:
                                    if(mapping.EventType == midiEvent.eventtype &&
                                        mapping.Channel == midiEvent.chan) {
                                        handled = true;
                                        OnMidiEvent?.Invoke(prop.Name, "general", mapping, midiEvent);
                                    }
                                    break;
                                case BASSMIDIEvent.MIDI_EVENT_CONTROL:
                                    if(mapping.EventType == midiEvent.eventtype &&
                                        mapping.Controller == (midiEvent.param & 0xFF) &&
                                        mapping.Channel == midiEvent.chan) {
                                        handled = true;
                                        OnMidiEvent?.Invoke(prop.Name, "general", mapping, midiEvent);
                                    }
                                    break;
                            }
                        });

                        foreach(var player in profile.Players) {
                            player.GetType().GetProperties().ToList().ForEach(prop => {
                                if(prop.PropertyType != typeof(MidiControllerProfile.MidiMapping)) return;
                                MidiControllerProfile.MidiMapping mapping = (prop.GetValue(player) as MidiControllerProfile.MidiMapping)!;

                                switch(midiEvent.eventtype) {
                                    case BASSMIDIEvent.MIDI_EVENT_NOTE:
                                        if(mapping.EventType == midiEvent.eventtype &&
                                            mapping.Note == (midiEvent.param & 0xFF) &&
                                            mapping.Channel == midiEvent.chan) {
                                            handled = true;
                                            OnMidiEvent?.Invoke(prop.Name, $"player{player.Index}", mapping, midiEvent);
                                            return;
                                        }
                                        break;
                                    default:
                                        bool isControlEvent = midiEvent.eventtype == BASSMIDIEvent.MIDI_EVENT_CONTROL;

                                        if(mapping.EventType == midiEvent.eventtype &&
                                            (isControlEvent ? mapping.Controller == (midiEvent.param & 0xFF) : true) &&
                                            mapping.Channel == midiEvent.chan) {
                                            handled = true;
                                            OnMidiEvent?.Invoke(prop.Name, $"player{player.Index}", mapping, midiEvent);
                                            return;
                                        }

                                        if(mapping.EventType == midiEvent.eventtype &&
                                            !isControlEvent &&
                                            mapping.Channel == midiEvent.chan) {
                                            handled = true;
                                            OnMidiEvent?.Invoke(prop.Name, $"player{player.Index}", mapping, midiEvent);
                                            return;
                                        }
                                        break;
                                }
                            });
                        }

                        profile.Keyboard.GetType().GetProperties().ToList().ForEach(prop => {
                            MidiControllerProfile.MidiMapping mapping = (prop.GetValue(profile.Keyboard) as MidiControllerProfile.MidiMapping)!;

                            switch(midiEvent.eventtype) {
                                case BASSMIDIEvent.MIDI_EVENT_VOLUME:
                                case BASSMIDIEvent.MIDI_EVENT_MODULATION:
                                    if(mapping.EventType == midiEvent.eventtype &&
                                        mapping.Channel == midiEvent.chan) {
                                        handled = true;
                                        OnMidiEvent?.Invoke(prop.Name, "keyboard", mapping, midiEvent);
                                    }
                                    break;
                                case BASSMIDIEvent.MIDI_EVENT_NOTE:
                                    if(mapping.EventType == midiEvent.eventtype &&
                                        mapping.Parameter == midiEvent.param &&
                                        mapping.Channel == midiEvent.chan) {
                                        handled = true;
                                        OnMidiEvent?.Invoke(prop.Name, "keyboard", mapping, midiEvent);
                                    }
                                    break;
                                case BASSMIDIEvent.MIDI_EVENT_PITCH:
                                    if(mapping.EventType == midiEvent.eventtype &&
                                        mapping.Channel == midiEvent.chan) {
                                        handled = true;
                                        OnMidiEvent?.Invoke(prop.Name, "keyboard", mapping, midiEvent);
                                    }
                                    break;
                            }
                        });

                        if(!handled) OnMidiEvent?.Invoke("unknown", "", null!, midiEvent);
                    }
                }

                BassMidi.BASS_MIDI_StreamEvents(midiStream, BASSMIDIEventMode.BASS_MIDI_EVENTS_RAW, 0, buffer, length);
            };

            // TODO: Handle multiple MIDI devices - should we listen to all devices or just a specific one?
            BASS_MIDI_DEVICEINFO[] midiDevices = BassMidi.BASS_MIDI_InGetGeviceInfos();
            int deviceIndex = midiDevices.ToList().FindIndex(d => d.name == Program.Settings.MidiDeviceName);
            if(deviceIndex != -1) {
                if(!midiDevices[deviceIndex].IsInitialized) {
                    BassMidi.BASS_MIDI_InInit(deviceIndex, midiProc, IntPtr.Zero);
                    BassMidi.BASS_MIDI_InStart(deviceIndex);
                }
            }
        }

        public void Stop() {
            if(midiStream == -1) return;

            Bass.BASS_StreamFree(midiStream);

            BASS_MIDI_DEVICEINFO[] midiDevices = BassMidi.BASS_MIDI_InGetGeviceInfos();
            int deviceIndex = midiDevices.ToList().FindIndex(d => d.name == Program.Settings.MidiDeviceName);
            if(deviceIndex != -1) {
                if(midiDevices[deviceIndex].IsInitialized) {
                    BassMidi.BASS_MIDI_InStop(deviceIndex);
                    BassMidi.BASS_MIDI_InFree(deviceIndex);
                }
            }
        }
    }
}
