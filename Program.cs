using Diyokee;
using Diyokee.Components;
using Diyokee.Data;
using Microsoft.EntityFrameworkCore;
using System.Diagnostics;
using Un4seen.Bass;
using Un4seen.Bass.AddOn.Enc;
using Un4seen.Bass.AddOn.EncMp3;
using Un4seen.Bass.AddOn.Mix;

internal class Program {
    public const int SAMPLING_FREQUENCY = 44100;
    public static List<(int Handle, int DeviceIndex)> BassMixHandles = [];
    public static int BassLatencyMs = 0;
    public static ILogger Logger = null!;

    public static Diyokee.Settings Settings = new();
    public static List<MidiControllerProfile> MidiControllersProfiles = [];
    public static MidiTools MidiTools = new();

    private static async Task Main(string[] args) {
        string workingDirectory = AppDomain.CurrentDomain.RelativeSearchPath ?? AppDomain.CurrentDomain.BaseDirectory;
#if !DEBUG
        Directory.SetCurrentDirectory(workingDirectory);
#endif

        if(args.Length > 0 && ProcessArguments(args, workingDirectory)) return;

        Settings = await Diyokee.Settings.Load();
        MidiControllersProfiles = await MidiControllerProfile.LoadAll();
        AutoSave();

        var builder = WebApplication.CreateBuilder(args);
        var connectionString = builder.Configuration.GetConnectionString("CacheDB");

        builder.WebHost.ConfigureKestrel(serverOptions => {
            int kestrelPort = 5001;
            string[] tokens = Settings.WebHostUrl.Split(":");
            if(tokens.Length > 2 && int.TryParse(tokens[2], out int port)) kestrelPort = port;
            serverOptions.ListenAnyIP(kestrelPort, listenOptions => {
                if(File.Exists(Settings.CertFile)) {
                    listenOptions.UseHttps(Settings.CertFile, Settings.CertPassword);
                }
            });
        });

        if(Settings.WebHostUrl != "") {
            builder.WebHost.UseUrls(Settings.WebHostUrl);
        }

        builder.Services.AddHttpContextAccessor();
        builder.Services.AddDistributedMemoryCache();
        builder.Services.AddSession(options => {
            options.Cookie.HttpOnly = true;
            options.Cookie.IsEssential = true;
        });
        builder.Services.AddRazorComponents().AddInteractiveServerComponents();
        builder.Services.AddServerSideBlazor();
        builder.Services.AddSingleton<SessionState>();

#if DEBUG
        builder.Services.AddSassCompiler();
#endif

        builder.Services.AddDbContextFactory<CacheDbContext>(options => options.UseSqlite(connectionString));

        var app = builder.Build();

        Logger = app.Logger;
        Logger.LogInformation("Setting up BASS...");
        InitBASS(workingDirectory);
        SetupBASS();

        app.Logger.LogInformation("Validating Cache Database...");
        using(IServiceScope? scope = app.Services.CreateScope()) {
            using(CacheDbContext? context = scope.ServiceProvider.GetService<CacheDbContext>()) {
                context?.Database.Migrate();
                //if(!context?.Database.EnsureCreated() ?? false) {
                //    if(context?.Database.GetPendingMigrations().Any() ?? false) {
                //        context.Database.Migrate();
                //    }
                //}
            }
        }

        if(!app.Environment.IsDevelopment()) {
            app.UseExceptionHandler("/Error", createScopeForErrors: true);
            app.UseHsts();
        }
        if(File.Exists(Settings.CertFile)) app.UseHttpsRedirection();

        app.UseAntiforgery();
        app.MapStaticAssets();

        app.MapRazorComponents<App>()
            .AddInteractiveServerRenderMode();

        app.Lifetime.ApplicationStopping.Register(() => {
            Logger.LogInformation("Application stopping, saving settings...");
            Settings.Save().Wait();
            MidiControllerProfile.SaveAll(MidiControllersProfiles).Wait();
        });

        app.Lifetime.ApplicationStarted.Register(() => {
            bool autoStart = Settings.AutoStartBrowser;

#if DEBUG
            autoStart = false;
#endif

            string line = new('—', 57 + Settings.WebHostUrl.Length);
            Logger.LogInformation(
                $"""

                    {line}
                    You may now open your browser and navigate to: {Settings.WebHostUrl}
                    {line}

                """);

            if(autoStart && Settings.WebHostUrl != "") {
                Process.Start(new ProcessStartInfo {
                    FileName = Settings.WebHostUrl,
                    UseShellExecute = true
                });
            }
        });

        app.Run();
    }

    private static bool ProcessArguments(string[] args, string workingDirectory) {
        foreach(string arg in args) {
            switch(arg) {
                case "--help":
                case "-h":
                    Console.WriteLine("Usage: Diyokee [options]");
                    Console.WriteLine("Options:");
                    Console.WriteLine("  --help, -h       Show this help message");
                    Console.WriteLine("  --version, -v    Show version information");
                    Console.WriteLine("  --info           Show version information about the files in the working directory");
                    return true;
                case "--version":
                case "-v":
                    string[] ignore = ["microsoft.", "system.", "diyokee", "mono."];
                    List<(string FileName, string FileVerson)> versions = [];
                    versions.Add(("Diyokee", typeof(Program).Assembly.GetName().Version?.ToString() ?? "N/A"));

                    FileInfo[] files = new DirectoryInfo(workingDirectory).GetFiles();
                    foreach(FileInfo file in files.Where(f => !ignore.Any(f.Name.ToLower().Contains)).OrderBy(f => f.Name)) {
                        try {
                            FileVersionInfo fvi = FileVersionInfo.GetVersionInfo(file.FullName);
                            string? ver = fvi.ProductVersion == "" ? fvi.FileVersion : fvi.ProductVersion;
                            if(ver != null) {
                                if(ver.IndexOf("+") > 0) ver = ver.Split("+")[0];
                                versions.Add((file.Name, ver));
                            }
                        } catch { }
                    }

                    int maxNameLength = versions.Max(v => v.FileName.Length) + 2;
                    foreach((string FileName, string FileVerson) in versions) {
                        Console.WriteLine($"{FileName}:{" ".PadRight(maxNameLength - FileName.Length)} {FileVerson}");
                    }
                    return true;
                default:
                    Console.WriteLine($"Unknown argument: {arg}");
                    return false;
            }
        }

        return false;
    }

    private static void AutoSave() { // FIXME: This is just... wrong... 
        Task.Run(async () => {
            while(true) {
                await Task.Delay(5000);
                await Settings.Save();
                await MidiControllerProfile.SaveAll(MidiControllersProfiles);
            }
        });
    }

    // TODO: Implement support to add/remove audio outputs without restarting the application
    // For this to work this function needs to be called every time the Settings.Audio object changes
    // and the BassMixHandles should be used to not re-initialize devices that have already been initialized and
    // to remove those are not longer in use.
    // Then, the Player.CreateStreams() function needs to be modified to update it's splitter streams accordingly.

    private static void SetupBASS() {
        int index = 0;
        var devices = Settings.Audio.MainOutputDevice.Concat(Settings.Audio.MonitorDevice).ToList();
        for(int i = 0; i < devices.Count; i++) {
            AudioDevice device = devices[i];
            int bassDeviceIndex = GetDeviceIndexByName(device.Name);
            bool r = Bass.BASS_SetDevice(bassDeviceIndex);
            if(!r || Bass.BASS_ErrorGetCode() != BASSError.BASS_OK) {
                Logger.LogError($"Failed to set BASS device '{device.Name}': {Bass.BASS_ErrorGetCode()}");
                if(i < Settings.Audio.MainOutputDevice.Count) {
                    Settings.Audio.MainOutputDevice.RemoveAt(i);
                } else {
                    Settings.Audio.MonitorDevice.RemoveAt(i - Settings.Audio.MainOutputDevice.Count);
                }
                continue;
            }

            BASS_INFO basInfo = new();
            Bass.BASS_GetInfo(basInfo);
            BassLatencyMs = Math.Max(BassLatencyMs, basInfo.latency);

            int handle = BassMix.BASS_Mixer_StreamCreate(SAMPLING_FREQUENCY, 8, BASSFlag.BASS_MIXER_NONSTOP | BASSFlag.BASS_MIXER_NORAMPIN);
            Bass.BASS_ChannelSetAttribute(handle, BASSAttribute.BASS_ATTRIB_BUFFER, 0);
            Bass.BASS_ChannelPlay(handle, true);
            BassMixHandles.Add((handle, index++));
        }

        // Attach to the first device, for now...
        if(Settings.Encoder.Enabled && Runtime.Platform != Runtime.Platforms.MacApple) {
            int encodeHandle = BassEnc_Mp3.BASS_Encode_MP3_Start(BassMixHandles.First().Handle, $"-b{Settings.Encoder.Bitrate}", BASSEncode.BASS_ENCODE_NOHEAD | BASSEncode.BASS_ENCODE_AUTOFREE, null, IntPtr.Zero);
            _ = BassEnc.BASS_Encode_ServerInit(encodeHandle, $"{Settings.Encoder.Port}/{Settings.Encoder.Url}", 16384 / 2, 16384, BASSEncodeServer.BASS_ENCODE_SERVER_DEFAULT, null, IntPtr.Zero);
        }
    }

    public static int GetDeviceIndexByName(string name) {
        for(int i = 0; i < Bass.BASS_GetDeviceCount(); i++) {
            BASS_DEVICEINFO deviceInfo = Bass.BASS_GetDeviceInfo(i);
            if(deviceInfo.name == name) return i;
        }
        return 0; // No Sound
    }

    private static bool InitBASS(string workingDirectory) {
        char c = Runtime.PathSeparator;
        string platform = Runtime.Platform.ToString().ToLower();
        string architecture = Environment.Is64BitProcess
                                || Runtime.Platform == Runtime.Platforms.MacIntel
                                || Runtime.Platform == Runtime.Platforms.MacApple
                                ? "x64" : "x86";

        if(platform.StartsWith("arm")) {
            platform = "arm";
            architecture = platform.EndsWith("hard") ? "hardfp" : "softfp";  // "armhf" : "armel";
        } else if(platform.StartsWith("aarch64")) {
            platform = "arm";
            architecture = "aarch64";
        } else if(platform == "macapple") {
            platform = "mac";
            architecture = "arm64";
        } else if(platform == "macintel") {
            platform = "mac";
            architecture = "x64";
        }

        string srcDir = Path.Combine(Runtime.RunningDirectory, $"bass{c}{platform}{c}{architecture}{c}");

        Logger.LogInformation(
            $$"""
            Platform: {{Runtime.Platform}}
            Architecture: {{architecture}}
            Libraries: {{Path.GetRelativePath(workingDirectory, srcDir)}}
            """);

        foreach(string srcFile in Directory.GetFiles(srcDir)) {
            string trgFile = Path.Combine(Runtime.RunningDirectory, Path.GetFileName(srcFile));

            if(File.Exists(trgFile)) File.Delete(trgFile);
            try {
                File.Copy(srcFile, trgFile, true);
            } catch {
                return false;
            }
        }

        if(Settings.BassNetRegEmail != "" && Settings.BassNetRegKey != "") {
            BassNet.Registration(Settings.BassNetRegEmail, Settings.BassNetRegKey);
        }

        Bass.BASS_PluginLoadDirectory(workingDirectory);
        Bass.BASS_SetConfig(BASSConfig.BASS_CONFIG_DEV_NONSTOP, 1);

        SetupDevice(Settings.Audio.MainOutputDevice);
        SetupDevice(Settings.Audio.MonitorDevice, false);

        return true;
    }

    private static void SetupDevice(List<AudioDevice> devices, bool createIfNotSet = true) {
        bool deviceIsSet = false;
        int defaultDeviceIndex = -1;
        for(int i = 0; i < Bass.BASS_GetDeviceCount(); i++) {
            BASS_DEVICEINFO deviceInfo = Bass.BASS_GetDeviceInfo(i);
            if(deviceInfo.IsDefault) defaultDeviceIndex = i;

            Bass.BASS_GetDeviceInfo(i, deviceInfo);
            if(devices.Any(d => d.Name == deviceInfo.name)) {
                if(!deviceInfo.IsInitialized) {
                    deviceIsSet = Bass.BASS_Init(i, SAMPLING_FREQUENCY, BASSInit.BASS_DEVICE_DEFAULT | BASSInit.BASS_DEVICE_LATENCY, IntPtr.Zero);
                    if(!deviceIsSet) Logger.LogError($"Failed to initialize BASS device '{deviceInfo.name}': {Bass.BASS_ErrorGetCode()}");
                }
            }
        }

        if(!deviceIsSet && createIfNotSet) {
            BASS_DEVICEINFO deviceInfo = Bass.BASS_GetDeviceInfo(defaultDeviceIndex);
            devices.Add(new(deviceInfo.name, AudioDevice.DeviceSpeakers.FrontStereo));
            SetupDevice(devices, false);
        }
    }
}