namespace Diyokee {
    public class Loop {
        public double Start { get; set; } = 0;
        public double End { get; set; } = 0;
        public bool Enabled { get; set; } = false;
        public bool Reset { get; set; } = false;
        public long StartBytes { get; set; } = 0;
        public long EndBytes { get; set; } = 0;
        public int BASSHandle { get; set; } = 0;
    }
}
