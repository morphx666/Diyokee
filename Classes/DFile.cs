using System.Text;

namespace Diyokee {
    // Migrations:
    // dotnet ef migrations add "Added ReplayGain"
    // dotnet ef database update
    public class DFile {
        public int Id { get; set; }
        public string Artist { get; set; } = "";
        public string Title { get; set; } = "";
        public string Genre { get; set; } = "";
        public string Album { get; set; } = "";
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

        public static string FormatTime(double seconds) {
            double h = Math.Floor(seconds / 3600);
            double m = Math.Floor((seconds % 3600) / 60);
            double s = seconds % 60;
            StringBuilder sb = new();
            if(h > 0) sb.Append($"{h:00}:");
            sb.Append($"{m:00}:");
            sb.Append($"{s:00}");
            return sb.ToString();
        }
    }
}
