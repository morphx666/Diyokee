namespace Diyokee {
    // Looping is applied by the ScratchEngine against its own playhead, so the loop only needs
    // to be described in seconds. The byte positions and the BASS_SYNC_POS handle this used to
    // carry went away with the sync-based implementation.
    public class Loop {
        public double Start { get; set; } = 0;
        public double End { get; set; } = 0;
        public bool Enabled { get; set; } = false;
    }
}
