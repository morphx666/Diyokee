// Ignore Spelling: Eq

using Newtonsoft.Json;
using System.Text.Json;

namespace Diyokee {
    public class Settings {
        public class EqualizerProfile {
            public string Name { get; set; } = string.Empty;
            public float Low { get; set; } = 0.0f;
            public float Mid { get; set; } = 0.0f;
            public float Hi { get; set; } = 0.0f;
        }

        public class MediaProvider {
            [JsonProperty("type")] public string Type { get; set; } = "local";
            [JsonProperty("name")] public string Name { get; set; } = "Local";
            [JsonProperty("root-directory")] public string RootDirectory { get; set; } = "";
            [JsonProperty("initial-path")] public string InitialPath { get; set; } = "";
        }

        public class EncoderOptions {
            [JsonProperty("enabled")] public bool Enabled { get; set; } = true;
            [JsonProperty("port")] public int Port { get; set; } = 2132;
            [JsonProperty("url")] public string Url { get; set; } = "stream";
            [JsonProperty("bitrate")] public int Bitrate { get; set; } = 320;
        }

        public class AudioSettings {
            [JsonProperty("main-output-device")] public List<AudioDevice> MainOutputDevice { get; set; } = [];
            [JsonProperty("monitor-device")] public List<AudioDevice> MonitorDevice { get; set; } = [];
        }

        public class PlaybackSettings {
            [JsonProperty("lock-onplay")] public bool LockOnPlay { get; set; } = true;
            [JsonProperty("eq-profile")] public string EqProfile { get; set; } = "";
            [JsonProperty("sync-players-bpm")] public bool SyncPlayersBpm { get; set; } = false;
            [JsonProperty("sync-playback")] public bool SyncPlayback { get; set; } = true;
            [JsonProperty("tempo-range")] public double TempoRange { get; set; } = 10;
            [JsonProperty("players")] public PlayerSettings[] Players { get; set; } = [
                new() { Name = "A", Color = "#977696" },
                new() { Name = "B", Color = "#279597", ReverseControls = true },
            ];
            [JsonProperty("waveform-zoom")] public int WaveformZoom { get; set; } = 5;
        }

        public class PlayerSettings {
            [JsonProperty("name")] public string Name { get; set; } = "";
            [JsonProperty("color")] public string Color { get; set; } = "";
            [JsonProperty("loopjump-lock")] public bool LoopJumpLock { get; set; } = true;
            [JsonProperty("loop-size")] public double LoopSize { get; set; } = 8;
            [JsonProperty("jump-size")] public double JumpSize { get; set; } = 8;
            [JsonProperty("reverse-controls")] public bool ReverseControls { get; set; } = false;
        }

        [JsonProperty("webhost-url")] public string WebHostUrl { get; set; } = "http://localhost:5000";
        [JsonProperty("cert-file")] public string CertFile { get; set; } = "";
        [JsonProperty("cert-password")] public string CertPassword { get; set; } = "";
        [JsonProperty("bassnet-reg-email")] public string BassNetRegEmail { get; set; } = "";
        [JsonProperty("bassnet-reg-key")] public string BassNetRegKey { get; set; } = "";
        [JsonProperty("media-providers")] public List<MediaProvider> MediaProviders { get; set; } = [];
        [JsonProperty("encoder")] public EncoderOptions Encoder { get; set; } = new();
        [JsonProperty("ui")] public Dictionary<string, string> UIElements { get; set; } = new() {
            ["main-resize-horizontal"] = "400",
            ["main-resize-vertical"] = "420"
        };
        [JsonProperty("audio-settings")] public AudioSettings Audio { get; set; } = new();
        [JsonProperty("playback-settings")] public PlaybackSettings Playback { get; set; } = new();
        [JsonProperty("auto-start-browser")] public bool AutoStartBrowser { get; set; } = true;
        [JsonProperty("midi-device-name")] public string MidiDeviceName { get; set; } = "";

        public List<EqualizerProfile> EqualizerProfiles { get; } = [];

        public async static Task<Settings> Load() {
            string workingDirectory = AppDomain.CurrentDomain.RelativeSearchPath ?? AppDomain.CurrentDomain.BaseDirectory;
            if(File.Exists(Path.Combine(workingDirectory, "settings.json"))) {
                Settings settings = JsonConvert.DeserializeObject<Settings>(await File.ReadAllTextAsync(Path.Combine(workingDirectory, "settings.json"))) ?? new();

                if(settings.MediaProviders.Count == 0) {
                    settings.MediaProviders.Add(new MediaProviders.MediaProviderLocal( ));
                }

                if(settings.EqualizerProfiles.Count == 0) {
                    settings.EqualizerProfiles.AddRange([
                        new() {Name = "Pioneer DJM", Low = 70, Mid = 1000, Hi = 13000},
                        new() {Name = "EVO 4", Low = 200, Mid = 1200, Hi = 6500},
                        new() {Name = "Allen & Heath Xone 42", Low = 420, Mid = 1200, Hi = 2700},
                        new() {Name = "Allen & Heath Xone 4D", Low = 120, Mid = 1400, Hi = 10000},
                        new() {Name = "Rane", Low = 300, Mid = 1200, Hi = 4000},
                        new() {Name = "Behringer DDM", Low = 330, Mid = 1400, Hi = 4200}
                    ]);
                }

                if(settings.Playback.EqProfile == "") {
                    settings.Playback.EqProfile = settings.EqualizerProfiles[0].Name;
                }

                return settings;
            } else {
                Settings settings = new();
                return settings;
            }
        }

        private readonly Lock lockObj = new();
        public async Task Save() {
            lock(lockObj) {
                string workingDirectory = AppDomain.CurrentDomain.RelativeSearchPath ?? AppDomain.CurrentDomain.BaseDirectory;
                File.WriteAllText(Path.Combine(workingDirectory, "settings.json"), JsonConvert.SerializeObject(this, Formatting.Indented));
            }
        }

        public object Clone() {
            var clone = JsonConvert.SerializeObject(this, Formatting.Indented);
            return JsonConvert.DeserializeObject(clone, GetType()) ?? new Settings();
        }
    }
}