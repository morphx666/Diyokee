using Un4seen.Bass;

// https://github.com/Corentin-Lcs/music-key-finder/tree/main/src
// Krumhansl, C.L. "Cognitive Foundations of Musical Pitch", 1990

// https://rnhart.net/articles/key-finding/

namespace Diyokee {
    public class Krumhansl_Schmuckler {
        // Krumhansl-Schmuckler key-finding profiles (C=0, C#=1, ..., B=11)
        private static readonly double[] majorProfile = [6.35, 2.23, 3.48, 2.33, 4.38, 4.09, 2.52, 5.19, 2.39, 3.66, 2.29, 2.88];
        private static readonly double[] minorProfile = [6.33, 2.68, 3.52, 5.38, 2.60, 3.53, 2.54, 4.75, 3.98, 2.69, 3.34, 3.17];

        // Maps chroma index (C=0, C#=1, ..., B=11) to KeyTools.NotationToKeysMap index
        private static readonly int[] minorKeyMap = [8, 22, 12, 2, 16, 6, 20, 10, 0, 14, 4, 18];
        private static readonly int[] majorKeyMap = [15, 5, 19, 9, 23, 13, 3, 17, 7, 21, 11, 1];

        private const double C1_FREQ = 32.703;    // C1 reference frequency in Hz
        private const double MIN_FREQ = 65.0;     // Lower bound (~C2)
        private const double MAX_FREQ = 2100.0;   // Upper bound (~C7)
        private const int FFT_SIZE = 16384;
        private const int FFT_HALF = FFT_SIZE / 2;

        public static string DetectKey(int handle, KeyTools.Notations notation) {
            Bass.BASS_ChannelSetPosition(handle, 0, BASSMode.BASS_POS_BYTE);
            BASS_CHANNELINFO channelInfo = Bass.BASS_ChannelGetInfo(handle);
            int sampleRate = channelInfo.freq;

            double[] chromagram = new double[12];

            // Precompute FFT bin to pitch class mapping
            int minBin = Math.Max(1, (int)Math.Ceiling(MIN_FREQ * FFT_SIZE / sampleRate));
            int maxBin = Math.Min(FFT_HALF - 1, (int)Math.Floor(MAX_FREQ * FFT_SIZE / sampleRate));

            int[] binPitchClass = new int[FFT_HALF];
            for(int k = minBin; k <= maxBin; k++) {
                double freq = (double)k * sampleRate / FFT_SIZE;
                int pitchClass = (int)Math.Round(12.0 * Math.Log2(freq / C1_FREQ)) % 12;
                if(pitchClass < 0) pitchClass += 12;
                binPitchClass[k] = pitchClass;
            }

            // Process audio using BASS's built-in FFT (Hanning-windowed)
            float[] fftData = new float[FFT_HALF];
            int segmentCount = 0;

            while(Bass.BASS_ChannelGetData(handle, fftData, (int)BASSData.BASS_DATA_FFT16384) > 0) {
                for(int k = minBin; k <= maxBin; k++) {
                    chromagram[binPitchClass[k]] += fftData[k];
                }
                segmentCount++;
            }

            if(segmentCount == 0) return "";

            // Normalize chromagram
            double maxChroma = chromagram.Max();
            if(maxChroma > 0) {
                for(int i = 0; i < 12; i++) chromagram[i] /= maxChroma;
            }

            // Correlate with K-S profiles for all 12 possible root notes
            double bestCorrelation = double.MinValue;
            int bestChromaIndex = 0;
            bool bestIsMajor = true;

            for(int i = 0; i < 12; i++) {
                double majorCorr = PearsonCorrelation(chromagram, Rotate(majorProfile, i));
                double minorCorr = PearsonCorrelation(chromagram, Rotate(minorProfile, i));

                if(majorCorr > bestCorrelation) {
                    bestCorrelation = majorCorr;
                    bestChromaIndex = i;
                    bestIsMajor = true;
                }
                if(minorCorr > bestCorrelation) {
                    bestCorrelation = minorCorr;
                    bestChromaIndex = i;
                    bestIsMajor = false;
                }
            }

            int keyIndex = bestIsMajor ? majorKeyMap[bestChromaIndex] : minorKeyMap[bestChromaIndex];
            return KeyTools.NotationToKeysMap[notation][keyIndex];
        }

        /// <summary>
        /// Rotates the profile array so that the tonic weight aligns with the given chroma index.
        /// </summary>
        private static double[] Rotate(double[] values, int positions) {
            int n = values.Length;
            double[] rotated = new double[n];
            for(int i = 0; i < n; i++) {
                rotated[i] = values[(i - positions + n) % n];
            }
            return rotated;
        }

        private static double PearsonCorrelation(double[] x, double[] y) {
            double meanX = x.Average();
            double meanY = y.Average();
            double numerator = 0;
            double denomX = 0;
            double denomY = 0;
            for(int i = 0; i < x.Length; i++) {
                double dx = x[i] - meanX;
                double dy = y[i] - meanY;
                numerator += dx * dy;
                denomX += dx * dx;
                denomY += dy * dy;
            }
            if(denomX == 0 || denomY == 0) return 0;
            return numerator / Math.Sqrt(denomX * denomY);
        }
    }
}
