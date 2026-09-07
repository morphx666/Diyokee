using System.ComponentModel.DataAnnotations.Schema;
using System.Text;

namespace Diyokee {
    // Migrations:
    // dotnet ef migrations add "Added ReplayGain"
    // dotnet ef database update
    public class DFile : ICloneable {
        public class CuePoint {
            public int Id { get; set; }
            public double Position { get; set; } = 0;
            public string Name { get; set; } = "";
        }

        // A tempo anchor: the tempo that applies from Position until the next anchor, and whether
        // the bar count restarts here. An empty list means "use BPM and DownbeatAt", which is the
        // one-anchor case rather than a separate code path - see Classes/BeatGrid.cs.
        //
        // Deliberately NOT stored: whether an anchor came from a bend or a hard reset (that lives
        // in the editing operation, not the data - a segment's beat count is always recoverable),
        // the beat or bar number (derived), and a separate downbeat position (the first anchor with
        // IsDownbeat is it).
        public class BeatGridMarker {
            public int Id { get; set; }
            public double Position { get; set; } = 0;
            public double BPM { get; set; } = 0;
            public bool IsDownbeat { get; set; } = true;
        }

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
        public List<CuePoint> CuePoints { get; set; } = [];
        public List<BeatGridMarker> BeatGridMarkers { get; set; } = [];
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

        public override string ToString() {
            return $"{Artist} - {Title} ({Album}, {Year})";
        }

        // Applies another file's user-editable values onto this instance rather than replacing it:
        // players hold a reference to this very object, so swapping it out would leave them
        // pointing at the old values. Identity and analysis results (Id, Filename, Duration,
        // Waveform, CuePoints) are not the dialog's to change.
        public void ApplyEditsFrom(DFile other) {
            Artist = other.Artist;
            Title = other.Title;
            Genre = other.Genre;
            Album = other.Album;
            Year = other.Year;
            BPM = other.BPM;
            DownbeatAt = other.DownbeatAt;
            Key = other.Key;

            ApplyBeatGridFrom(other);
        }

        // The anchors are diffed in rather than assigned, for the same reason CuePoints.UpdateCuePoints
        // diffs: when this instance is the entity EF is tracking, replacing the collection orphans
        // every row and re-inserts it, so ids churn on every save. Matching on Id keeps an edited
        // anchor the same row.
        //
        // The caller MUST have loaded this instance with .Include(f => f.BeatGridMarkers). Without
        // it EF hands back an empty collection, the removal pass below sees every anchor as deleted,
        // and saving wipes the grid - the same shape as the duplicate-row bug UpdateFile was fixed
        // for.
        private void ApplyBeatGridFrom(DFile other) {
            BeatGridMarkers.RemoveAll(mine => !other.BeatGridMarkers.Any(theirs => theirs.Id != 0 && theirs.Id == mine.Id));

            foreach(BeatGridMarker marker in other.BeatGridMarkers) {
                BeatGridMarker? existing = marker.Id == 0 ? null : BeatGridMarkers.FirstOrDefault(m => m.Id == marker.Id);
                if(existing != null) {
                    existing.Position = marker.Position;
                    existing.BPM = marker.BPM;
                    existing.IsDownbeat = marker.IsDownbeat;
                } else {
                    BeatGridMarkers.Add(new BeatGridMarker {
                        Position = marker.Position,
                        BPM = marker.BPM,
                        IsDownbeat = marker.IsDownbeat
                    });
                }
            }
        }

        public object Clone() {
            DFile clone = (DFile)this.MemberwiseClone();

            // MemberwiseClone is shallow, so without this the copy would share the original's cue
            // point list and edits to it would still reach the loaded track.
            clone.CuePoints = [];
            foreach(CuePoint cuePoint in CuePoints) {
                clone.CuePoints.Add(new CuePoint { Id = cuePoint.Id, Position = cuePoint.Position, Name = cuePoint.Name });
            }

            // Same reason, and it is what makes Cancel in the Track Properties dialog actually
            // cancel a grid edit: without it the clone and the loaded deck share one list.
            clone.BeatGridMarkers = [];
            foreach(BeatGridMarker marker in BeatGridMarkers) {
                clone.BeatGridMarkers.Add(new BeatGridMarker {
                    Id = marker.Id, Position = marker.Position, BPM = marker.BPM, IsDownbeat = marker.IsDownbeat
                });
            }

            return clone;
        }
    }
}
