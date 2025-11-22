using System.Diagnostics;

namespace Diyokee;

internal class Runtime {
    public enum Platforms {
        Windows,
        Linux,
        MacIntel,
        MacApple,
        ARMSoft,
        ARMHard,
        Aarch64
    }
    private static Platforms? mPlatform;

    public static Platforms Platform {
        get {
            if(mPlatform == null) DetectPlatform();
            return mPlatform.HasValue ? mPlatform.Value : default;
        }
    }

    public static char PathSeparator {
        get {
            return Path.DirectorySeparatorChar;
            //if(mPathSeparator == null) DetectPlatform();
            //return mPathSeparator.HasValue ? mPathSeparator.Value : default;
        }
    }

    public static string RunningDirectory {
        get {
            //return Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location) ?? "";
            return AppDomain.CurrentDomain.BaseDirectory;
        }
    }

    private static void DetectPlatform() {
        switch (Environment.OSVersion.Platform) {
            case PlatformID.Win32NT:
            case PlatformID.Win32S:
            case PlatformID.Win32Windows:
            case PlatformID.WinCE:
            case PlatformID.Xbox:
                mPlatform = Platforms.Windows;
                break;

            case PlatformID.MacOSX:
                mPlatform = System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture == System.Runtime.InteropServices.Architecture.Arm64 ? Platforms.MacApple : Platforms.MacIntel;
                break;

            default:
                if(Directory.Exists("/Applications") &&
                    Directory.Exists("/System") &&
                    Directory.Exists("/Users") &&
                    Directory.Exists("/Volumes")) {
                    mPlatform = System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture == System.Runtime.InteropServices.Architecture.Arm64 ? Platforms.MacApple : Platforms.MacIntel;
                } else {
                    mPlatform = Platforms.Linux;

                    string distro = GetLinuxInfo("-a").ToLower();
                    string arch = GetLinuxInfo("-m").ToLower();
                    if(distro.Contains("raspberrypi") || distro.Contains("rpi")) {
                        mPlatform = Platforms.ARMSoft;
                        if(distro.Contains("armv7l")) mPlatform = Platforms.ARMHard;
                        if(arch == "aarch64") mPlatform = Platforms.Aarch64;
                    }
                }

                break;
        }
    }

    private static string GetLinuxInfo(string param) {
        List<string> lines = [];

        Process catProcess = new();
        catProcess.StartInfo.FileName = "uname";
        catProcess.StartInfo.Arguments = param;
        catProcess.StartInfo.CreateNoWindow = true;
        catProcess.StartInfo.UseShellExecute = false;
        catProcess.StartInfo.RedirectStandardOutput = true;
        catProcess.StartInfo.RedirectStandardError = true;
        catProcess.StartInfo.RedirectStandardInput = false;

        catProcess.OutputDataReceived += (sender, e) => lines.Add(e.Data ?? "");

        try {
            catProcess.Start();
            catProcess.BeginOutputReadLine();
            catProcess.WaitForExit();
            catProcess.Dispose();

            Thread.Sleep(500);

            if(lines.Count > 0)
                return lines.First();
            else
                return "Unknown";
        } catch {
            return Environment.OSVersion.Platform.ToString();
        }
    }
}
