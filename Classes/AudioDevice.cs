namespace Diyokee {
    public class AudioDevice(string name, List<AudioDevice.AudioChannel> channels) {
        public enum AudioChannel {
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
        public List<AudioChannel> Channels { get; set; } = channels;

        public AudioDevice(string name, AudioChannel channel) : this(name, [channel]) {}
        public AudioDevice(string name) : this(name, []) { }
        public AudioDevice() : this("", []) { }
    }
}