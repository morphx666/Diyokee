using Diyokee;
using Diyokee.Components;
using Diyokee.Data;
using Diyokee.MediaProviders;
using Microsoft.EntityFrameworkCore;
using System.Diagnostics;
using Un4seen.Bass;
using Un4seen.Bass.AddOn.Enc;
using Un4seen.Bass.AddOn.EncMp3;
using Un4seen.Bass.AddOn.Fx;
using Un4seen.Bass.AddOn.Mix;

internal class Program {
    public const int SAMPLING_FREQUENCY = 44100;
    public static List<(int Handle, int DeviceIndex, string Name)> BassMixHandles = [];
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
        builder.Services.AddScoped<UiBusyState>();
        builder.Services.AddSingleton<SessionState>();

#if DEBUG
        builder.Services.AddSassCompiler();
#endif

        builder.Services.AddDbContextFactory<CacheDbContext>(options => options.UseSqlite(connectionString));

        var app = builder.Build();

        Diyokee.Secrets.Initialize(app.Services.GetRequiredService<Microsoft.AspNetCore.DataProtection.IDataProtectionProvider>());

        Logger = app.Logger;
        Logger.LogInformation("Setting up BASS...");
        InitBASS(workingDirectory);
        ReconcileAudioDevices();

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

    // Brings the running set of mixer streams in line with Settings.Audio without disturbing the
    // ones already playing, so adding or removing an output no longer needs a restart. Called once
    // at startup and again from Player.ApplyAudioDeviceChanges whenever the audio matrix is edited.
    //
    // BassMixHandles is kept ordered as MainOutputDevice ++ MonitorDevice: DeviceIndex is what
    // Player.GetSpeakersMatrix uses to look the device back up in Settings.Audio, and each Player's
    // splitter list is positionally parallel to this one. Both break if the ordering drifts.
    public static void ReconcileAudioDevices() {
        var devices = Settings.Audio.MainOutputDevice.Concat(Settings.Audio.MonitorDevice).ToList();
        List<(int Handle, int DeviceIndex, string Name)> reconciled = [];
        List<AudioDevice> failed = [];

        // Matches are consumed rather than searched, because one device can legitimately appear
        // twice - checked as both a master and a monitor output - and each of those entries owns
        // its own mixer on that device. Matching by name alone would hand them the same one.
        List<(int Handle, int DeviceIndex, string Name)> unclaimed = [.. BassMixHandles];

        foreach(AudioDevice device in devices) {
            // Already running. Keep the stream as it is - it may only have moved in the ordering.
            int claimed = unclaimed.FindIndex(m => m.Name == device.Name);
            if(claimed >= 0) {
                reconciled.Add((unclaimed[claimed].Handle, reconciled.Count, device.Name));
                unclaimed.RemoveAt(claimed);
                continue;
            }

            int bassDeviceIndex = GetDeviceIndexByName(device.Name);

            // A device configured at startup was initialised by SetupDevice, and one picked in the
            // audio matrix by its own BASS_Init, so BASS_ALREADY is the normal answer here.
            if(!Bass.BASS_Init(bassDeviceIndex, SAMPLING_FREQUENCY, BASSInit.BASS_DEVICE_DEFAULT | BASSInit.BASS_DEVICE_LATENCY, IntPtr.Zero)
               && Bass.BASS_ErrorGetCode() != BASSError.BASS_ERROR_ALREADY) {
                Logger.LogError($"Failed to initialize BASS device '{device.Name}': {Bass.BASS_ErrorGetCode()}");
                failed.Add(device);
                continue;
            }

            if(!Bass.BASS_SetDevice(bassDeviceIndex) || Bass.BASS_ErrorGetCode() != BASSError.BASS_OK) {
                Logger.LogError($"Failed to set BASS device '{device.Name}': {Bass.BASS_ErrorGetCode()}");
                failed.Add(device);
                continue;
            }

            int handle = BassMix.BASS_Mixer_StreamCreate(SAMPLING_FREQUENCY, 8, BASSFlag.BASS_MIXER_NONSTOP | BASSFlag.BASS_MIXER_NORAMPIN);
            if(handle == 0) {
                Logger.LogError($"Failed to create mixer for BASS device '{device.Name}': {Bass.BASS_ErrorGetCode()}");
                failed.Add(device);
                continue;
            }

            Bass.BASS_ChannelSetAttribute(handle, BASSAttribute.BASS_ATTRIB_BUFFER, 0);
            Bass.BASS_ChannelPlay(handle, true);
            reconciled.Add((handle, reconciled.Count, device.Name));
        }

        // Whatever no entry claimed has dropped out of the matrix. Callers release their splitters
        // first, so by the time we get here nothing is feeding these.
        foreach(var stale in unclaimed) {
            if(stale.Handle == encoderMixHandle) encoderHandle = encoderMixHandle = 0;  // AUTOFREE takes it with the mixer
            Bass.BASS_ChannelStop(stale.Handle);
            Bass.BASS_StreamFree(stale.Handle);
        }

        BassMixHandles = reconciled;

        foreach(AudioDevice device in failed) {
            Settings.Audio.MainOutputDevice.Remove(device);
            Settings.Audio.MonitorDevice.Remove(device);
        }

        // A running maximum is meaningless once a device can leave, so it is measured afresh.
        BassLatencyMs = 0;
        foreach(var m in BassMixHandles) {
            Bass.BASS_SetDevice(GetDeviceIndexByName(m.Name));
            BASS_INFO bassInfo = new();
            Bass.BASS_GetInfo(bassInfo);
            BassLatencyMs = Math.Max(BassLatencyMs, bassInfo.latency);
        }

        SetupEncoder();
    }

    private static int encoderHandle = 0;
    private static int encoderMixHandle = 0;

    // The encoder rides on the first mixer, so it has to be moved when that device is the one
    // removed - otherwise the stream dies silently along with the mixer it was attached to.
    private static void SetupEncoder() {
        if(!Settings.Encoder.Enabled || Runtime.Platform == Runtime.Platforms.MacApple) return;
        if(BassMixHandles.Count == 0) return;

        int target = BassMixHandles.First().Handle;
        if(encoderHandle != 0 && encoderMixHandle == target) return;

        if(encoderHandle != 0) BassEnc.BASS_Encode_Stop(encoderHandle);

        encoderMixHandle = target;
        encoderHandle = BassEnc_Mp3.BASS_Encode_MP3_Start(target, $"-b{Settings.Encoder.Bitrate}", BASSEncode.BASS_ENCODE_NOHEAD | BASSEncode.BASS_ENCODE_AUTOFREE, null, IntPtr.Zero);
        _ = BassEnc.BASS_Encode_ServerInit(encoderHandle, $"{Settings.Encoder.Port}/{Settings.Encoder.Url}", 16384 / 2, 16384, BASSEncodeServer.BASS_ENCODE_SERVER_DEFAULT, null, IntPtr.Zero);
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

        // bass_fx is an add-on rather than a plugin, so BASS_PluginLoadDirectory does not pick it
        // up: BASS.NET loads it lazily on the first BassFx call. Until it is loaded, BASS does not
        // recognise the BASS_FX_BFX_* effect types and BASS_ChannelSetFX fails with
        // BASS_ERROR_ILLTYPE. Loading it here removes that ordering dependency - previously it
        // happened to be satisfied by whichever BassFx call ran first while building the chain.
        int bassFxVersion = BassFx.BASS_FX_GetVersion();
        if(bassFxVersion == 0) {
            Logger.LogError("Failed to load BASS_FX - volume, tempo, EQ and pitch controls will not work");
        } else {
            Logger.LogInformation($"BASS_FX version: {Utils.HighWord(bassFxVersion)}.{Utils.LowWord(bassFxVersion)}");
        }

        Bass.BASS_SetConfig(BASSConfig.BASS_CONFIG_DEV_NONSTOP, 1);
        // Bass.BASS_SetConfig(BASSConfig.BASS_CONFIG_BUFFER, 30); Use BASS's default 500ms

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