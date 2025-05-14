namespace Diyokee {
    public class AudioDevice(string name, List<AudioDevice.DeviceSpeakers> speakers) {
        public enum DeviceSpeakers {
            FrontStereo     = 0b00000011,
            FrontLeft       = 0b00000001,
            FrontRight      = 0b00000010,

            SideStereo      = 0b00001100,
            SideLeft        = 0b00000100,
            SideRight       = 0b00001000,

            RearStereo      = 0b00110000,
            RearLeft        = 0b00010000,
            RearRight       = 0b00100000,

            CenterAndLFE    = 0b11000000,
            Center          = 0b01000000,
            LFE             = 0b10000000,
        }

        public string Name { get; set; } = name;
        public List<DeviceSpeakers> Speakers { get; set; } = speakers;

        public AudioDevice(string name, DeviceSpeakers channel) : this(name, [channel]) {}
        public AudioDevice(string name) : this(name, []) { }
        public AudioDevice() : this("", []) { }
    }
}