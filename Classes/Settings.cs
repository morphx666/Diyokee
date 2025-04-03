using Newtonsoft.Json;
using System.Text.Json;

namespace Diyokee {
    public class Settings {
        public class MediaProvider {
            [JsonProperty("type")] public string Type { get; set; } = "";
            [JsonProperty("name")] public string Name { get; set; } = "";
            [JsonProperty("root-directory")] public string RootDirectory { get; set; } = "";
        }
        [JsonProperty("webhost-url")] public string WebHostUrl { get; set; } = "";
        [JsonProperty("cert-file")] public string CertFile { get; set; } = "";
        [JsonProperty("cert-password")] public string CertPassword { get; set; } = "";
        [JsonProperty("bassnet-reg-email")] public string BassNetRegEmail { get; set; } = "";
        [JsonProperty("bassnet-reg-key")] public string BassNetRegKey { get; set; } = "";
        [JsonProperty("media-providers")] public List<MediaProvider> MediaProviders { get; set; } = [];
    }
}
