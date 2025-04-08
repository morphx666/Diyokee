using System.Text;

namespace Diyokee {
    // Migrations:
    // dotnet ef migrations add "Added ReplayGain"
    // dotnet ef database update
    public class DFile : ICloneable {
        public int Id { get; set; }
        public string Artist { get; set; } = "";
        public string Title { get; set; } = "";
        public string Genre { get; set; } = "";
        public string Album { get; set; } = "";
        public int Year { get; set; } = 0;
        public string Filename { get; set; } = "";
        public double Duration { get; set; } = 0;
        public string Waveform { get; set; } = ""; // Base64 encoded
        public float BPM { get; set; } = 0;
        public double DownbeatAt { get; set; } = -1;
        public string Key { get; set; } = "";
        public double ReplayGain { get; set; } = 0;
        public bool HasReplayGain { get; set; } = false;

        private string waveformUnZipped = "";
        public string WaveformUnZipped {
            get {
                if(waveformUnZipped == "") {
                    waveformUnZipped = Waveform == "" ? "" : Waveform.UnZip();
                }
                return waveformUnZipped;
            }
        }

        public static string FormatTime(double seconds, bool includeMs = false) {
            double h = Math.Floor(seconds / 3600);
            double m = Math.Floor((seconds % 3600) / 60);
            double s = seconds % 60;
            double ms = (s - Math.Floor(s)) * 1000;
            StringBuilder sb = new();
            if(h > 0 || includeMs) sb.Append($"{h:00}:");
            sb.Append($"{m:00}:");
            sb.Append($"{s:00}");
            if(includeMs) sb.Append($".{ms:000}");
            return sb.ToString();
        }

        public static double ParseTime(double h, double m, double s, double ms) {
            return (h * 3600) + (m * 60) + s + (ms / 1000);
        }

        public object Clone() {
            return this.MemberwiseClone();
            return new DFile {
                Id = Id,
                Artist = Artist,
                Title = Title,
                Genre = Genre,
                Album = Album,
                Year = Year,
                Filename = Filename,
                Duration = Duration,
                Waveform = Waveform,
                BPM = BPM,
                DownbeatAt = DownbeatAt,
                Key = Key,
                ReplayGain = ReplayGain,
                HasReplayGain = HasReplayGain
            };
        }
    }
}
