using static FFTLib.FFT;
using Un4seen.Bass;

// FIXME: This is not working!

// https://github.com/Corentin-Lcs/music-key-finder/tree/main/src
// https://dsp.stackexchange.com/questions/93745/can-the-constant-q-transform-be-implemented-more-efficiently-using-an-fft

namespace Diyokee {
    public class Krumhansl_Schmuckler {
        private static double[] majorProfile = [6.35, 2.23, 3.48, 2.33, 4.38, 4.09, 2.52, 5.19, 2.39, 3.66, 2.29, 2.88];
        private static double[] minorProfile = [6.33, 2.68, 3.52, 5.38, 2.60, 3.53, 2.54, 4.75, 3.98, 2.69, 3.34, 3.17];
        private static string[] chromaLabels = ["C", "C#", "D", "D#", "E", "F", "F#", "G", "G#", "A", "A#", "B"];

        public static string DetectKey(int handle) {
            Bass.BASS_ChannelSetPosition(handle, 0, BASSMode.BASS_POS_BYTE);
            BASS_CHANNELINFO channel_info = Bass.BASS_ChannelGetInfo(handle);

            int samplingRate = channel_info.freq;
            int segmentLength = 10 * samplingRate;
            Int16 maxValue = Int16.MaxValue;
            Int16[] buffer = new Int16[segmentLength];
            int fftSize = (int)FFTSizeConstants.FFTs16384;
            int fftLen = fftSize / 2;
            ComplexDouble[] fftOutput = new ComplexDouble[fftSize];

            double[] cqtResult = new double[12];

            double[] fftWindowValues = GetWindowValues((FFTSizeConstants)fftSize, FFTWindowConstants.Hanning);
            //double fftWindowSum = GetWindowSum((FFTSizeConstants)fftSize, FFTWindowConstants.Hanning);

            while(true) {
                if(Bass.BASS_ChannelGetData(handle, buffer, buffer.Length) <= 0) break;

                double[] doubleBuffer = new double[buffer.Length];
                for(int i = 0; i < buffer.Length; i++) {
                    int windowIndex = (int)Math.Floor((double)i / buffer.Length * fftSize);
                    doubleBuffer[i] = (double)buffer[i] / maxValue * fftWindowValues[windowIndex];
                }

                FourierTransform(fftSize, doubleBuffer, fftOutput, false);

                int cqtIndex = 0;
                for(int bin = 0; bin < 12; bin++) {
                    double binStart = bin * segmentLength;
                    double binEnd = (bin + 1) * segmentLength;
                    ComplexDouble binSum = new();

                    for(int i = 0; i < fftLen; i++) {
                        double frequency =  binStart + (i * (binEnd - binStart) / fftLen);
                        double omega = 2.0 * Math.PI * frequency / samplingRate;
                        binSum += fftOutput[i] * ComplexDouble.Pow(Math.E, new(0, -omega * i));
                    }
                    cqtResult[cqtIndex++] += binSum.Power(); // / fftSize;
                }
            }

            double max = cqtResult.Max();
            cqtResult = [.. cqtResult.Select(x => x / max)];

            double[] majorCorrelation = new double[12];
            double[] minorCorrelation = new double[12];

            for(int i = 0; i < 12; i++) {
                majorCorrelation[i] = PearsonCorrelation(cqtResult, RollRight(majorProfile, i));
                minorCorrelation[i] = PearsonCorrelation(cqtResult, RollRight(minorProfile, i));
            }

            double majorMax = majorCorrelation.Max();
            double minorMax = minorCorrelation.Max();
            if(majorMax > minorMax) {
                int majorIndex = Array.IndexOf(majorCorrelation, majorMax);
                return chromaLabels[majorIndex] + " Major";
            } else if(minorMax > majorMax) {
                int minorIndex = Array.IndexOf(minorCorrelation, minorMax);
                return chromaLabels[minorIndex] + " Minor";
            } else {
                return "Ambiguous";
            }
        }

        private static double[] RollRight(double[] values, int times) {
            double[] rolled = [.. values];

            for(int i = 0; i < times; i++) {
                double last = rolled[^1];
                for(int j = rolled.Length - 1; j > 0; j--) {
                    rolled[j] = rolled[j - 1];
                }
                rolled[0] = last;
            }

            return rolled;
        }

        private static double PearsonCorrelation(double[] cqtResult, double[] profile) {
            double meanCqt = cqtResult.Average();
            double meanProfile = profile.Average();
            double numerator = 0;
            double denominatorCqt = 0;
            double denominatorProfile = 0;
            for(int i = 0; i < cqtResult.Length; i++) {
                double diffCqt = cqtResult[i] - meanCqt;
                double diffProfile = profile[i] - meanProfile;
                numerator += diffCqt * diffProfile;
                denominatorCqt += diffCqt * diffCqt;
                denominatorProfile += diffProfile * diffProfile;
            }
            if(denominatorCqt == 0 || denominatorProfile == 0) {
                return 0;
            } else {
                return numerator / Math.Sqrt(denominatorCqt * denominatorProfile);
            }
        }
    }
}
