namespace Diyokee {
    public class AudioDevice(string name, List<AudioDevice.DeviceSpeakers> speakers) {
        public enum DeviceSpeakers {
            FrontStereo,
            FrontLeft,
            FrontRight,

            SideStereo,
            SideLeft,
            SideRight,

            RearStereo,
            RearLeft,
            RearRight,

            CenterAndLFE,
            Center,
            LFE
        }

        public string Name { get; set; } = name;
        public List<DeviceSpeakers> Speakers { get; set; } = speakers;

        public AudioDevice(string name, DeviceSpeakers channel) : this(name, [channel]) {}
        public AudioDevice(string name) : this(name, []) { }
        public AudioDevice() : this("", []) { }
    }
}