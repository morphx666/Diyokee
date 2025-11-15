
using Newtonsoft.Json;
using Un4seen.Bass.AddOn.Midi;

namespace Diyokee {
    public class MidiControllerProfile {
        public class MidiMapping {
            public BASSMIDIEvent EventType { get; set; } = BASSMIDIEvent.MIDI_EVENT_NONE;
            public int Note { get; set; } = -1;
            public int Channel { get; set; } = -1;
            public int Velocity { get; set; } = -1;
            public int Parameter { get; set; } = -1;
        }

        public class GeneralMapping {
            public MidiMapping FoldersFilesSwitch { get; set; } = new();
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
            public MidiMapping Filter { get; set; } = new();
            public MidiMapping TempoUp { get; set; } = new();
            public MidiMapping TempoDown { get; set; } = new();
            public MidiMapping LoopSet { get; set; } = new();
            public MidiMapping LoopSize { get; set; } = new();
            public MidiMapping JumpSize { get; set; } = new();
            public MidiMapping JumpForward { get; set; } = new();
            public MidiMapping JumpBackward { get; set; } = new();
        }

        public int DeviceIndex { get; set; } = -1;
        public string DeviceName { get; set; } = string.Empty;
        public string Name { get; set; } = string.Empty;
        public bool Enabled { get; set; } = true;
        public GeneralMapping General { get; set; } = new();
        public PlayerMapping[] Players { get; set; } = [
            new() { Index = 0 },
            new() { Index = 1 },
        ];

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
    }
}