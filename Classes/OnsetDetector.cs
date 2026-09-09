using Diyokee.MediaProviders;
using Un4seen.Bass;

namespace Diyokee;

// Gets a track's samples out of BASS and into OnsetEnvelope, which does the actual work. This is
// the only part of automatic gridding that touches audio, and all it does is open, seek and read -
// so the signal processing stays in a file tools/gridtest can compile.
public static class OnsetDetector {
    // Audio decoded before the start point, to let the filters settle and to give the first real
    // frame a predecessor to difference against.
    private const double PreRollSeconds = 0.5;

    // Opens the track, measures from `fromSeconds` on, and closes it again. Everything before that
    // point is skipped outright: BeatAlign starts at the downbeat, and audio before the downbeat
    // has no grid to be judged against.
    public static List<BeatAlign.Onset> Detect(DFile file, double fromSeconds) {
        int handle = BassStreamFactory.CreateStreamFile(file.Filename, 0, 0,
            BASSFlag.BASS_STREAM_DECODE | BASSFlag.BASS_SAMPLE_MONO
            | BASSFlag.BASS_SAMPLE_FLOAT | BASSFlag.BASS_STREAM_PRESCAN);

        if(handle == 0) {
            Program.Logger.LogWarning($"Onset detection could not open {file.Filename}: {Bass.BASS_ErrorGetCode()}");
            return [];
        }

        try {
            return Detect(handle, fromSeconds);
        } finally {
            Bass.BASS_StreamFree(handle);
        }
    }

    // Takes a mono float decode channel, so a caller with a stream already open can reuse it.
    public static List<BeatAlign.Onset> Detect(int handle, double fromSeconds) {
        BASS_CHANNELINFO info = Bass.BASS_ChannelGetInfo(handle);
        if(info.freq <= 0) return [];

        double start = Math.Max(0, fromSeconds - PreRollSeconds);
        Bass.BASS_ChannelSetPosition(handle, Bass.BASS_ChannelSeconds2Bytes(handle, start), BASSMode.BASS_POS_BYTE);

        return OnsetEnvelope.Onsets(
            buffer => Math.Max(0, Bass.BASS_ChannelGetData(handle, buffer, buffer.Length * sizeof(float))) / sizeof(float),
            info.freq, start, fromSeconds);
    }
}
