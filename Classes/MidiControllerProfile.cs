
using Newtonsoft.Json;
using Un4seen.Bass.AddOn.Midi;

namespace Diyokee {
    public class MidiControllerProfile : ICloneable {
        [AttributeUsage(AttributeTargets.Property)]
        public class DoNotRender : Attribute { }

        // A control that reports where it is, or one that reports how far it has just moved.
        // A platter is the second kind: it has no position to report, only ticks since the last
        // message, so nothing about its value means anything without knowing which kind it is.
        public enum MappingModes { Absolute, Relative }

        // How a relative control signs a tick count into a 7-bit value. All three are in common
        // use, and a single message cannot tell them apart - +1 is 0x01 in all three - which is
        // why the learn flow captures a burst and turns the control both ways.
        public enum RelativeFormats { TwosComplement, SignedBit, BinaryOffset }

        public class MidiMapping {
            public BASSMIDIEvent EventType { get; set; } = BASSMIDIEvent.MIDI_EVENT_NONE;
            public int Note { get; set; } = -1;
            public int Channel { get; set; } = -1;
            public int Velocity { get; set; } = -1;
            public int Parameter { get; set; } = -1;
            public int Controller { get; set; } = -1;

            // Absent from an existing profile JSON, so every profile written before this keeps
            // deserializing and every control in it stays absolute, which is what it was.
            public MappingModes Mode { get; set; } = MappingModes.Absolute;
            public RelativeFormats RelativeFormat { get; set; } = RelativeFormats.TwosComplement;

            // Ticks in one full turn of the platter. Zero means not calibrated, and the jog path
            // does nothing at all until it is rather than inventing a scale - a wrong value here
            // is not a slightly wrong feel, it is the record moving the wrong distance.
            public int TicksPerRevolution { get; set; }
        }

        // Decoding a relative value with the wrong format does not scale the answer, it inverts or
        // wraps it, so the format belongs to the mapping rather than being guessed per message.
        public static int DecodeRelative(int value, RelativeFormats format) {
            value &= 0x7F;
            return format switch {
                RelativeFormats.TwosComplement => value < 64 ? value : value - 128,
                RelativeFormats.SignedBit => (value & 0x40) != 0 ? -(value & 0x3F) : value & 0x3F,
                RelativeFormats.BinaryOffset => value - 64,
                _ => 0,
            };
        }

        public class GeneralMapping {
            public MidiMapping FoldersFilesToggle { get; set; } = new();
            public MidiMapping BrowseUp { get; set; } = new();
            public MidiMapping BrowseDown { get; set; } = new();
            public MidiMapping ExpandFolder { get; set; } = new();
            public MidiMapping LoadSelectedFileToPlayer0 { get; set; } = new();
            public MidiMapping LoadSelectedFileToPlayer1 { get; set; } = new();
            public MidiMapping Gain { get; set; } = new();
            public MidiMapping Crossfader { get; set; } = new();
        }

        public class PlayerMapping {
            public int Index { get; set; } = 0;
            public MidiMapping PlayPause { get; set; } = new();
            public MidiMapping Stop { get; set; } = new();
            public MidiMapping Cue { get; set; } = new();
            public MidiMapping SnapToBeatMarker { get; set; } = new();
            public MidiMapping LoadSelectedFile { get; set; } = new();
            public MidiMapping BpmMatch { get; set; } = new();
            public MidiMapping Volume { get; set; } = new();
            public MidiMapping Tempo { get; set; } = new();
            public MidiMapping EqHi { get; set; } = new();
            public MidiMapping EqMid { get; set; } = new();
            public MidiMapping EqLow { get; set; } = new();
            public MidiMapping Color { get; set; } = new();
            public MidiMapping TempoUp { get; set; } = new();
            public MidiMapping TempoDown { get; set; } = new();
            public MidiMapping LoopJumpLockToggle { get; set; } = new();
            public MidiMapping LoopToggle { get; set; } = new();
            public MidiMapping LoopSizeIncrease { get; set; } = new();
            public MidiMapping LoopSizeDecrease { get; set; } = new();
            public MidiMapping JumpSizeIncrease { get; set; } = new();
            public MidiMapping JumpSizeDecrease { get; set; } = new();
            public MidiMapping JumpForward { get; set; } = new();
            public MidiMapping JumpBackward { get; set; } = new();
            public MidiMapping JogWheelForward { get; set; } = new();
            public MidiMapping JogWheelBackward { get; set; } = new();

            // The platter proper, as opposed to the two discrete nudges above, which stay for
            // controllers that have no platter. JogTouch is the capacitive top plate, a note on
            // and off; JogWheel is the rotation, a relative CC. Both are picked up by the
            // dispatcher and by the settings UI by property name, like every other mapping.
            public MidiMapping JogTouch { get; set; } = new();
            public MidiMapping JogWheel { get; set; } = new();
        }

        public class KeyboardMapping {
            public MidiMapping Volume { get; set; } = new();
            public MidiMapping Pitch { get; set; } = new();
            public MidiMapping FirstKey { get; set; } = new();
            public MidiMapping LastKey { get; set; } = new();
            [DoNotRender] public MidiMapping TargetPlayer { get; set; } = new(); // TODO: Add UI controls to select target player for keyboard
        }

        public string Name { get; set; } = string.Empty;
        public bool Enabled { get; set; } = true;
        public GeneralMapping General { get; set; } = new();
        public PlayerMapping[] Players { get; set; } = [
            new() { Index = 0 },
            new() { Index = 1 },
        ];
        public KeyboardMapping Keyboard { get; set; } = new();

        public static async Task<List<MidiControllerProfile>> LoadAll() {
            string workingDirectory = AppDomain.CurrentDomain.RelativeSearchPath ?? AppDomain.CurrentDomain.BaseDirectory;
            string profilesPath = Path.Combine(workingDirectory, "controllers");
            if(!Directory.Exists(profilesPath)) Directory.CreateDirectory(profilesPath);

            List<MidiControllerProfile> profiles = [];
            foreach(string file in Directory.GetFiles(profilesPath, "*.json")) {
                MidiControllerProfile profile = JsonConvert.DeserializeObject<MidiControllerProfile>(await File.ReadAllTextAsync(Path.Combine(workingDirectory, file))) ?? new();
                profiles.Add(profile);
            }

            return profiles;
        }

        public static async Task SaveAll(List<MidiControllerProfile> profiles) {
            string workingDirectory = AppDomain.CurrentDomain.RelativeSearchPath ?? AppDomain.CurrentDomain.BaseDirectory;
            string profilesPath = Path.Combine(workingDirectory, "controllers");
            if(!Directory.Exists(profilesPath)) Directory.CreateDirectory(profilesPath);

            foreach(MidiControllerProfile profile in profiles) {
                string filePath = Path.Combine(profilesPath, $"{profile.Name}.json");
                await File.WriteAllTextAsync(filePath, JsonConvert.SerializeObject(profile, Formatting.Indented));
            }
        }

        public static string GetProfilePath(string fileName) {
            string workingDirectory = AppDomain.CurrentDomain.RelativeSearchPath ?? AppDomain.CurrentDomain.BaseDirectory;
            string profilesPath = Path.Combine(workingDirectory, "controllers");
            if(!Directory.Exists(profilesPath)) Directory.CreateDirectory(profilesPath);
            return Path.Combine(profilesPath, fileName + ".json");
        }

        public override string ToString() {
            return Name;
        }

        public object Clone() {
            return JsonConvert.DeserializeObject<MidiControllerProfile>(JsonConvert.SerializeObject(this)) ?? new MidiControllerProfile();
        }
    }
}