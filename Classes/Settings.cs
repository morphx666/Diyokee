using Newtonsoft.Json;
using System.Text.Json;

namespace Diyokee {
    public class Settings {
        public struct MediaProvider {
            [JsonProperty("type")] public string Type;
            [JsonProperty("name")] public string Name;
            [JsonProperty("root-directory")] public string RootDirectory;
        }

        public struct EncoderOptions {
            [JsonProperty("enabled")] public bool Enabled;
            [JsonProperty("port")] public int Port;
            [JsonProperty("url")] public string Url;
            [JsonProperty("bitrate")] public int Bitrate;
        }

        [JsonProperty("webhost-url")] public string WebHostUrl { get; set; }
        [JsonProperty("cert-file")] public string CertFile { get; set; }
        [JsonProperty("cert-password")] public string CertPassword { get; set; }
        [JsonProperty("bassnet-reg-email")] public string BassNetRegEmail { get; set; }
        [JsonProperty("bassnet-reg-key")] public string BassNetRegKey { get; set; }
        [JsonProperty("media-providers")] public List<MediaProvider> MediaProviders { get; set; }
        [JsonProperty("encoder")] public EncoderOptions Encoder { get; set; }
        [JsonProperty("ui")] public Dictionary<string, string> UIElements { get; set; }

        public Settings() {
            WebHostUrl = "http://localhost:5000";
            CertFile = "";
            CertPassword = "";
            BassNetRegEmail = "";
            BassNetRegKey = "";
            MediaProviders = [
                new() {
                    Type = "local",
                    Name = "Local",
                    RootDirectory = ""
                }
            ];
            Encoder = new() {
                Enabled = true,
                Port = 2132,
                Url = "stream",
                Bitrate = 128
            };
            UIElements = new() {
                ["main-resize-horizontal"] = "400",
                ["main-resize-vertical"] = "315"
            };
        }

        public async static Task<Settings> Load() {
            string workingDirectory = AppDomain.CurrentDomain.RelativeSearchPath ?? AppDomain.CurrentDomain.BaseDirectory;
            if(File.Exists(Path.Combine(workingDirectory, "settings.json"))) {
                Settings settings = JsonConvert.DeserializeObject<Settings>(await File.ReadAllTextAsync(Path.Combine(workingDirectory, "settings.json"))) ?? new();
                settings.MediaProviders.RemoveAt(0); // FIXME: Delete the provider created by the ctor
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
