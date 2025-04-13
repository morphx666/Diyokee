using System.ComponentModel.DataAnnotations.Schema;
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
        [NotMapped] public bool IsValid { get; set; } = true;

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
            int h = (int)(seconds / 3600);
            seconds -= h * 3600;
            int m = (int)(seconds / 60);
            seconds -= m * 60;
            int s = (int)seconds;
            int ms = (int)Math.Round((seconds - s) * 1000.0, 3);

            StringBuilder sb = new();
            if(h > 0 || includeMs) sb.Append($"{h:00}:");
            sb.Append($"{m:00}:");
            sb.Append($"{s:00}");
            if(includeMs) sb.Append($".{ms:000}");
            return sb.ToString();
        }

        public static double ParseTime(double h, double m, double s, double ms) {
            return (h * 3600) + (m * 60) + s + (ms / 1000.0);
        }

        public object Clone() {
            return this.MemberwiseClone();
        }
    }
}
