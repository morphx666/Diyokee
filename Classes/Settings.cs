using Newtonsoft.Json;
using System.Text.Json;

namespace Diyokee {
    public class Settings {
        public class MediaProvider {
            [JsonProperty("type")] public string Type { get; set; } = "local";
            [JsonProperty("name")] public string Name { get; set; } = "Local";
            [JsonProperty("root-directory")] public string RootDirectory { get; set; } = "";
        }

        public class EncoderOptions {
            [JsonProperty("enabled")] public bool Enabled { get; set; } = true;
            [JsonProperty("port")] public int Port { get; set; } = 2132;
            [JsonProperty("url")] public string Url { get; set; } = "stream";
            [JsonProperty("bitrate")] public int Bitrate { get; set; } = 320;
        }

        public class AudioSettings {
            [JsonProperty("main-output-device")] public string MainOutputDevice { get; set; } = "";
        }

        public class PlaybackSettings {
            [JsonProperty("lock-onplay")] public bool LockOnPlay { get; set; } = true;
            [JsonProperty("eq-profile")] public string EqProfile { get; set; } = "";
            [JsonProperty("sync-players-bpm")] public bool SyncPlayersBpm { get; set; } = false;
            [JsonProperty("players")] public PlayerSettings[] Players { get; set; } = [
                new() { Name = "A", Color = "#977696" },
                new() { Name = "B", Color = "#279597" },
            ];
        }

        public class PlayerSettings {
            [JsonProperty("name")] public string Name { get; set; } = "";
            [JsonProperty("color")] public string Color { get; set; } = "";
            [JsonProperty("loopjump-lock")] public bool LoopJumpLock { get; set; } = true;
            [JsonProperty("loop-size")] public double LoopSize { get; set; } = 8;
            [JsonProperty("jump-size")] public double JumpSize { get; set; } = 8;
        }

        [JsonProperty("webhost-url")] public string WebHostUrl { get; set; } = "http://localhost:5000";
        [JsonProperty("cert-file")] public string CertFile { get; set; } = "";
        [JsonProperty("cert-password")] public string CertPassword { get; set; } = "";
        [JsonProperty("bassnet-reg-email")] public string BassNetRegEmail { get; set; } = "";
        [JsonProperty("bassnet-reg-key")] public string BassNetRegKey { get; set; } = "";
        [JsonProperty("media-providers")] public List<MediaProvider> MediaProviders { get; set; } = [new()];
        [JsonProperty("encoder")] public EncoderOptions Encoder { get; set; } = new();
        [JsonProperty("ui")] public Dictionary<string, string> UIElements { get; set; } = new() {
            ["main-resize-horizontal"] = "400",
            ["main-resize-vertical"] = "396"
        };
        [JsonProperty("audio-settings")] public AudioSettings Audio { get; set; } = new();
        [JsonProperty("playback-settings")] public PlaybackSettings Playback { get; set; } = new();

        public void AutoSave() {
            Task.Run(async () => {
                while(true) {
                    await Task.Delay(5000);
                    await Save();
                }
            });
        }

        public async static Task<Settings> Load() {
            string workingDirectory = AppDomain.CurrentDomain.RelativeSearchPath ?? AppDomain.CurrentDomain.BaseDirectory;
            if(File.Exists(Path.Combine(workingDirectory, "settings.json"))) {
                Settings settings = JsonConvert.DeserializeObject<Settings>(await File.ReadAllTextAsync(Path.Combine(workingDirectory, "settings.json"))) ?? new();
                settings.MediaProviders.RemoveAt(0); // FIXME: Delete the provider created by the ctor
                settings.AutoSave();
                return settings;
            } else {
                Settings settings = new();
                await settings.Save();
                return settings;
            }
        }

        public async Task Save() {
            string workingDirectory = AppDomain.CurrentDomain.RelativeSearchPath ?? AppDomain.CurrentDomain.BaseDirectory;
            await File.WriteAllTextAsync(Path.Combine(workingDirectory, "settings.json"), JsonConvert.SerializeObject(this, Formatting.Indented));
        }
    }
}
