using System.Diagnostics;

namespace Diyokee;

internal class Runtime {
    public enum Platforms {
        Windows,
        Linux,
        Mac,
        ARMSoft,
        ARMHard
    }
    private static Platforms? mPlatform;
    private static char? mPathSeparator;

    public static Platforms Platform {
        get {
            if(mPlatform == null) DetectPlatform();
            return mPlatform.HasValue ? mPlatform.Value : default(Platforms);
        }
    }

    public static char PathSeparator {
        get {
            if(mPathSeparator == null) DetectPlatform();
            return mPathSeparator.HasValue ? mPathSeparator.Value : default(Char);
        }
    }

    public static string RunningDirectory {
        get {
            //return Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location) ?? "";
            return AppDomain.CurrentDomain.BaseDirectory;
        }
    }

    private static void DetectPlatform() {
        mPathSeparator = '/';
        switch (Environment.OSVersion.Platform) {
            case PlatformID.Win32NT:
            case PlatformID.Win32S:
            case PlatformID.Win32Windows:
            case PlatformID.WinCE:
            case PlatformID.Xbox:
                mPlatform = Platforms.Windows;
                mPathSeparator = '\\';
                break;

            case PlatformID.MacOSX:
                mPlatform = Platforms.Mac;
                break;

            default:
                if(Directory.Exists("/Applications") &&
                    Directory.Exists("/System") &&
                    Directory.Exists("/Users") &&
                    Directory.Exists("/Volumes")) {
                    mPlatform = Platforms.Mac;
                } else {
                    mPlatform = Platforms.Linux;

                    string distro = GetLinuxDistro().ToLower();
                    if(distro.Contains("raspberrypi")) {
                        mPlatform = Platforms.ARMSoft;
                        if(distro.Contains("armv7l"))
                            mPlatform = Platforms.ARMHard;
                    }
                }

                break;
        }
    }

    private static string GetLinuxDistro() {
        List<string> lines = [];

        Process catProcess = new();
        catProcess.StartInfo.FileName = "uname";
        catProcess.StartInfo.Arguments = "-a";
        catProcess.StartInfo.CreateNoWindow = true;
        catProcess.StartInfo.UseShellExecute = false;
        catProcess.StartInfo.RedirectStandardOutput = true;
        catProcess.StartInfo.RedirectStandardError = true;
        catProcess.StartInfo.RedirectStandardInput = false;

        catProcess.OutputDataReceived += (object sender, DataReceivedEventArgs e) => lines.Add(e.Data ?? "");

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
