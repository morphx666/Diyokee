namespace Diyokee {
    // Kept free of dependencies so the ScratchEngine, the Settings model and the engine's test
    // harness can all share them without dragging the rest of the app along.

    // What grabbing the platter does to playback.
    public enum TouchModes {
        Vinyl,      // the hand takes over completely, as on a real turntable
        Bend        // the hand offsets play speed instead of replacing it (pitch bend)
    }

    // What letting go of the platter does.
    public enum ReleaseModes {
        Inertia,    // spin back up to play speed over SpinUpTime (0 gives an instant snap back)
        Stop        // brake to a standstill over BrakeTime, leaving the deck stopped
    }
}
