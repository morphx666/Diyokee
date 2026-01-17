namespace Diyokee {
    public class MouseState {
        public double X { get; set; }
        public double Y { get; set; }
        public double DeltaX { get; set; }
        public double DeltaY { get; set; }
        public long ButtonsDown { get; set; } = -1;
        public bool IsCaptured {get;set; }
        public double WheelDelta { get; set; }

        // https://stackoverflow.com/questions/22029033/can-javascript-tell-the-difference-between-left-and-right-shift-key
        public bool ShiftDown { get; set; }

        public bool Enabled { get; set; } = true;

        public override string ToString() {
            return $"{ButtonsDown} ({X}, {Y})";
        }
    }

    public class Bounds(double x, double y, double width, double height) {
        public double X { get; set; } = x;
        public double Y { get; set; } = y;
        public double Width { get; set; } = width;
        public double Height { get; set; } = height;
        public double Right => X + Width;
        public double Bottom => Y + Height;

        public Bounds(double[] values) : this(values[0], values[1], values[2], values[3]) { }
        public Bounds(Bounds b): this(b.X, b.Y, b.Width, b.Height) { }

        public static Bounds FromString(string bounds) {
            string[] tokens = bounds.Split(',');
            if(tokens.Length != 4) throw new ArgumentException("Invalid bounds format");
            return new Bounds(double.Parse(tokens[0]), double.Parse(tokens[1]), double.Parse(tokens[2]), double.Parse(tokens[3]));
        }

        public bool Contains(double x, double y, int tolerance = 0) {
            for(int i = -tolerance; i <= tolerance; i++) {
                for(int j = -tolerance; j <= tolerance; j++) {
                    if(x >= X + i
                        && x <= X + Width + i
                        && y >= Y + j
                        && y <= Y + Height + j) return true;
                }
            }

            return false;
        }

        public override string ToString() {
            return $"{X},{Y},{Width},{Height}";
        }
    }
}
