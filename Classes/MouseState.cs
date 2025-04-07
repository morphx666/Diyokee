namespace Diyokee {
    public class MouseState {
        public double X { get; set; }
        public double Y { get; set; }
        public long ButtonsDown { get; set; }
        public bool IsCaptured { get; set; }
        public double WheelDelta { get; set; }

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

        public bool Contains(double x, double y) {
            return x >= X && x <= X + Width && y >= Y && y <= Y + Height;
        }

        public override string ToString() {
            return $"({X}, {Y})-({Width}, {Height})";
        }
    }
}
