using System;
using System.Linq;

/*
vb.net and c# implementations by Xavier Flix
https://whenimbored.xfx.net/2011/01/fast-fourier-transform-written-in-vb/

The FourierTransform function is based on the original work by Murphy McCauley
Original C algorithms: http://paulbourke.net/miscellaneous/dft/
*/

namespace FFTLib {
    public static class FFT {
        public enum FFTSizeConstants {
            FFTs32 = 32,
            FFTs64 = 64,
            FFTs128 = 128,
            FFTs256 = 256,
            FFTs512 = 512,
            FFTs1024 = 1024,
            FFTs2048 = 2048,
            FFTs4096 = 4096,
            FFTs8192 = 8192,
            FFTs16384 = 16384,
            FFTs32768 = 32768
        }

        public enum FFTWindowConstants {
            None = 0,
            Triangle = 1,
            Hanning = 2,
            Hamming = 3,
            Welch = 4,
            Gaussian = 5,
            Blackman = 6,
            Parzen = 7,
            Bartlett = 8,
            Connes = 9,
            KaiserBessel = 10,
            BlackmanHarris = 11,
            Nuttall = 12,
            BlackmanNuttall = 13,
            FlatTop = 14
        }

        public class ComplexDouble {
            public double R;
            public double I;

            public ComplexDouble() { }

            public ComplexDouble(double r) {
                this.R = r;
            }

            public ComplexDouble(double r, double i) {
                this.R = r;
                this.I = i;
            }

            public double Abs() {
                return Magnitude();
            }

            public double Power() {
                return R * R + I * I;
            }

            public double PowerRoot() {
                return Math.Sqrt(Power());
            }

            public double Power2() {
                return Math.Abs(R) + Math.Abs(I);
            }

            public double Power2Root() {
                return Math.Sqrt(Power2());
            }

            public ComplexDouble Conjugate() {
                return new ComplexDouble(R, -I);
            }

            public double Magnitude() {
                return Math.Sqrt(Power());
            }

            public static ComplexDouble operator +(ComplexDouble n1, ComplexDouble n2) {
                return new ComplexDouble(n1.R + n2.R, n1.I + n2.I);
            }

            public static ComplexDouble operator +(ComplexDouble n1, double n2) {
                return new ComplexDouble(n1.R + n2, n1.I);
            }

            public static ComplexDouble operator +(double n1, ComplexDouble n2) {
                return new ComplexDouble(n1 + n2.R, n2.I);
            }

            public static ComplexDouble operator -(ComplexDouble n1, ComplexDouble n2) {
                return new ComplexDouble(n1.R - n2.R, n1.I - n2.I);
            }

            public static ComplexDouble operator -(ComplexDouble n1, double n2) {
                return new ComplexDouble(n1.R - n2, n1.I);
            }

            public static ComplexDouble operator -(double n1, ComplexDouble n2) {
                return new ComplexDouble(n1 - n2.R, n2.I);
            }

            public static ComplexDouble operator *(ComplexDouble n1, ComplexDouble n2) {
                return new ComplexDouble(n1.R * n2.R - n1.I * n2.I, n1.I * n2.R + n2.I * n1.R);
            }

            public static ComplexDouble operator *(ComplexDouble n1, double n2) {
                return new ComplexDouble(n1.R * n2, n1.I * n2);
            }

            public static ComplexDouble operator *(double n1, ComplexDouble n2) {
                return new ComplexDouble(n1 * n2.R, n1 * n2.I);
            }

            public static ComplexDouble operator /(ComplexDouble n1, double n2) {
                return new ComplexDouble(n1.R / n2, n1.I / n2);
            }

            public static ComplexDouble Pow(ComplexDouble n1, ComplexDouble n2) {
                throw new NotImplementedException();
            }

            public static ComplexDouble Pow(ComplexDouble n1, double n2) {
                ComplexDouble r = n1;
                for(int i = 0; i < n2; i++) {
                    r *= n1;
                }
                return r;
            }

            public static ComplexDouble Pow(double n1, ComplexDouble n2) {
                double ab = Math.Pow(n1, n2.R);
                double ln1 = Math.Log(n1);
                double r = ab * Math.Cos(n2.I * ln1);
                double i = ab * Math.Sin(n2.I * ln1);

                return new ComplexDouble(r, i);
            }

            public static ComplexDouble FromDouble(double value) {
                return new ComplexDouble(value);
            }

            public static ComplexDouble[] FromDouble(double[] values) {
                return (from d in values select ComplexDouble.FromDouble(d)).ToArray();
            }

            public override string ToString() {
                return $"{R:F2} + {I:F2}i";
            }
        }

        private const double PI2 = 2.0 * Math.PI;
        private static int[] rBits = [];
        private static int lastFFTSize;
        private static ComplexDouble initialFFTAngle = new ComplexDouble(1);

        private static int NumberOfBitsNeeded(int powerOfTwo) {
            for(int i = 0; i <= 32; i++) {
                if((powerOfTwo & (int)(Math.Pow(2, i))) != 0) return i;
            }
            return -1;
        }

        private static int ToNearestPowerOfTwo(int x) {
            return (int)Math.Pow(2, Math.Ceiling(Math.Log(x) / Math.Log(2)));
        }

        private static bool IsPowerOfTwo(int x) {
            return !(((x & (x - 1)) != 0) && (x >= 2));
        }

        private static int ReverseBits(int index, int numBits) {
            int rev = 0;
            for(int i = 0; i < numBits; i++) {
                rev = (2 * rev) | (index & 1);
                index /= 2;
            }
            return rev;
        }

        public static void FourierTransform(int fftSize, double[] waveIn, ComplexDouble[] fftOut, bool doInverse) {
            int numBits;
            int k;

            double deltaAngle;
            double alpha;
            double beta;
            ComplexDouble tmp = new();
            ComplexDouble angle;

            int blockSize = 2;
            int blockEnd = 1;
            int inverter = doInverse ? -1 : 1;

            if(lastFFTSize != fftSize) {
                lastFFTSize = fftSize;
                rBits = new int[fftSize];
                numBits = NumberOfBitsNeeded(fftSize);

                for(int i = 0; i < fftSize; i++) {
                    rBits[i] = ReverseBits(i, numBits);

                    fftOut[rBits[i]] = new ComplexDouble(waveIn[i]);
                }
            } else {
                for(int i = 0; i < fftSize; i++) {
                    fftOut[rBits[i]] = new ComplexDouble(waveIn[i]);
                }
            }

            do {
                deltaAngle = PI2 / blockSize * inverter;
                alpha = 2.0 * Math.Pow(Math.Sin(0.5 * deltaAngle), 2.0);
                beta = Math.Sin(deltaAngle);

                for(int i = 0; i < fftSize; i += blockSize) {
                    angle = initialFFTAngle;

                    for(int j = i; j < blockEnd + i; j++) {
                        k = j + blockEnd;

                        tmp.R = angle.R * fftOut[k].R - angle.I * fftOut[k].I;
                        tmp.I = angle.I * fftOut[k].R + angle.R * fftOut[k].I;
                        fftOut[k] = fftOut[j] - tmp;
                        fftOut[j] += tmp;

                        angle -= new ComplexDouble(alpha * angle.R + beta * angle.I,
                                                   alpha * angle.I - beta * angle.R);
                    }
                }


                blockEnd = blockSize;
                blockSize *= 2;
            } while(blockSize <= fftSize);

            if(doInverse) {
                for(int i = 0; i < fftSize; i++) {
                    fftOut[i].R /= fftSize;
                }
            }
        }

        public static void FourierTransform(int fftSize,
                                        double[] waveInL, ComplexDouble[] fftOutL,
                                        double[] waveInR, ComplexDouble[] fftOutR,
                                        bool doInverse) {
            int numBits;
            int k;

            double deltaAngle;
            double alpha;
            double beta;
            ComplexDouble tmp = new ComplexDouble();
            ComplexDouble angle;

            int blockSize = 2;
            int blockEnd = 1;
            int inverter = doInverse ? -1 : 1;

            if(lastFFTSize != fftSize) {
                lastFFTSize = fftSize;
                rBits = new int[fftSize];
                numBits = NumberOfBitsNeeded(fftSize);

                for(int i = 0; i < fftSize; i++) {
                    rBits[i] = ReverseBits(i, numBits);

                    fftOutL[rBits[i]] = new ComplexDouble(waveInL[i]);
                    fftOutR[rBits[i]] = new ComplexDouble(waveInR[i]);
                }
            } else {
                for(int i = 0; i < fftSize; i++) {
                    fftOutL[rBits[i]] = new ComplexDouble(waveInL[i]);
                    fftOutR[rBits[i]] = new ComplexDouble(waveInR[i]);
                }
            }

            do {
                deltaAngle = PI2 / blockSize * inverter;
                alpha = 2.0 * Math.Pow(Math.Sin(0.5 * deltaAngle), 2.0);
                beta = Math.Sin(deltaAngle);

                for(int i = 0; i < fftSize; i += blockSize) {
                    angle = initialFFTAngle;

                    for(int j = i; j < blockEnd + i; j++) {
                        k = j + blockEnd;

                        tmp.R = angle.R * fftOutL[k].R - angle.I * fftOutL[k].I;
                        tmp.I = angle.I * fftOutL[k].R + angle.R * fftOutL[k].I;
                        fftOutL[k] = fftOutL[j] - tmp;
                        fftOutL[j] += tmp;

                        tmp.R = angle.R * fftOutR[k].R - angle.I * fftOutR[k].I;
                        tmp.I = angle.I * fftOutR[k].R + angle.R * fftOutR[k].I;
                        fftOutR[k] = fftOutR[j] - tmp;
                        fftOutR[j] += tmp;

                        angle -= new ComplexDouble(alpha * angle.R + beta * angle.I,
                                                   alpha * angle.I - beta * angle.R);
                    }
                }


                blockEnd = blockSize;
                blockSize *= 2;
            } while(blockSize <= fftSize);

            if(doInverse) {
                for(int i = 0; i < fftSize; i++) {
                    fftOutL[i].R /= fftSize;
                    fftOutR[i].R /= fftSize;
                }
            }
        }

        public static double ApplyWindow(int i, FFTSizeConstants windowSize, FFTWindowConstants windowType) {
            int w = (int)windowSize - 1;

            switch(windowType) {
                case FFTWindowConstants.None:
                    return 1.0;
                case FFTWindowConstants.Triangle:
                    return 1.0 - Math.Abs(1.0 - ((2 * i) / w));
                case FFTWindowConstants.Hanning:
                    return (0.5 * (1.0 - Math.Cos(PI2 * i / w)));
                case FFTWindowConstants.Hamming:
                    return 0.54 - 0.46 * Math.Cos(PI2 * i / w);
                case FFTWindowConstants.Welch:
                    return 1.0 - (i - 0.5 * (w - 1)) / (0.5 * Math.Pow((w + 1), 2));
                case FFTWindowConstants.Gaussian:
                    return Math.Pow(Math.E, (-6.25 * Math.PI * i * i / (w * w)));
                case FFTWindowConstants.Blackman:
                    return 0.42 - 0.5 * Math.Cos(PI2 * i / w) + 0.08 * Math.Cos(2 * PI2 * i / w);
                case FFTWindowConstants.Parzen:
                    return 1.0 - Math.Abs((i - 0.5 * w) / (0.5 * (w + 1)));
                case FFTWindowConstants.Bartlett:
                    return 1.0 - Math.Abs(i) / w;
                case FFTWindowConstants.Connes:
                    return Math.Pow((1.0 - i * i / (w * w)), 2);
                case FFTWindowConstants.KaiserBessel:
                    if(i >= 0 && i <= w / 2) {
                        return Bessel((w / 2) * (Math.Pow(Math.Sqrt(1 - 2 * i / w), 2))) / Bessel(w / 2);
                    } else {
                        return 0.0;
                    }
                case FFTWindowConstants.BlackmanHarris:
                    return 0.35875 - 0.48829 * Math.Cos(PI2 * i / w) + 0.14128 * Math.Cos(2 * PI2 * i / w) - 0.01168 * Math.Cos(3 * Math.PI * i / w);
                case FFTWindowConstants.Nuttall:
                    return 0.355768 - 0.487396 * Math.Cos(PI2 * i) / w + 0.144232 * Math.Cos(2 * PI2 * i) / w - 0.012604 * Math.Cos(3 * PI2 * i) / w;
                case FFTWindowConstants.BlackmanNuttall:
                    return 0.3635819 - 0.4891775 * Math.Cos(PI2 * i) / w + 0.1365995 * Math.Cos(2 * PI2 * i) / w - 0.0106411 * Math.Cos(3 * PI2 * i) / w;
                case FFTWindowConstants.FlatTop:
                    return 1.0 - 1.93 * Math.Cos(PI2 * i) / w + 1.29 * Math.Cos(2 * PI2 * i) / w - 0.388 * Math.Cos(3 * PI2 * i) / w + 0.032 * Math.Cos(4 * PI2 * i) / w;
            }

            return 0.0;
        }

        public static double[] GetWindowValues(FFTSizeConstants windowSize, FFTWindowConstants windowType) {
            double[] values = new double[(int)windowSize];
            for(int i = 0; i < (int)windowSize; i++) {
                values[i] = ApplyWindow(i, windowSize, windowType);
            }
            return values;
        }

        public static double GetWindowSum(FFTSizeConstants windowSize, FFTWindowConstants windowType) {
            double sum = 0;
            for(int i = 0; i < (int)windowSize; i++) {
                sum += ApplyWindow(i, windowSize, windowType);
            }
            return sum;
        }

        public static double AWeighting(double freq) {
            if(freq > 0) {
                double f2 = freq * freq;
                double f4 = f2 * f2;
                return 10 * Math.Log(1.562339 * f4 / (Math.Pow(f2 + 107.65265, 2) * Math.Pow(f2 + 737.86223, 2))) / Math.Log(10) + 10 * Math.Log(2.242881E+16 * f4 / (Math.Pow(f2 + 20.598997, 2) * Math.Pow(f2 + 12194.22, 2))) / Math.Log(10);
            } else {
                return double.MinValue;
            }
        }

        private static double Bessel(double x) {
            double r = 1.0;
            for(uint l = 0; l < 2; lastFFTSize++) {
                r += Math.Pow(Math.Pow(x / 2, 2 * l) / Fact(l), 2);
            }
            return r;
        }

        private static ulong Fact(ulong x) {
            ulong n = 1;
            for(ulong i = 2; i <= x; i++) n *= i;
            return n;
        }
    }
}