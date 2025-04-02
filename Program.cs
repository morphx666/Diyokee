using Microsoft.EntityFrameworkCore;
using Diyokee.Components;
using Diyokee.Data;
using Un4seen.Bass;
using Un4seen.Bass.AddOn.Enc;
using Un4seen.Bass.AddOn.EncMp3;
using Un4seen.Bass.AddOn.Mix;
using System.Text.Json;
using Diyokee;

internal class Program {
    public static int BassMixHandle = 0;
    public static int BassLatencyMs = 0;

    private static JsonElement secrets;

    private static void Main(string[] args) {
        if(File.Exists("secrets.json")) {
            secrets = JsonDocument.Parse(File.ReadAllText("secrets.json")).RootElement;
        } else {
            secrets = JsonDocument.Parse("{}").RootElement;
        }
        
        InitBASS();
        SetupBASS();

        var builder = WebApplication.CreateBuilder(args);
        var connectionString = builder.Configuration.GetConnectionString("CacheDB");

        if(secrets.TryGetProperty("cert-file", out var certFile)
            && secrets.TryGetProperty("cert-password", out var certPassword)
            && File.Exists(certFile.ToString())) {
            builder.WebHost.ConfigureKestrel(serverOptions => {
                serverOptions.ListenAnyIP(5000, listenOptions => {
                    listenOptions.UseHttps(certFile.ToString(), certPassword.ToString());
                });
            });
        }

        if(secrets.TryGetProperty("webhost-url", out var webHostUrl)) {
            builder.WebHost.UseUrls(webHostUrl.ToString());
        }

        // Add services to the container.
        builder.Services.AddRazorComponents()
            .AddInteractiveServerComponents();

#if DEBUG
        builder.Services.AddSassCompiler();
#endif

        builder.Services.AddDbContextFactory<CacheDbContext>(options => options.UseSqlite(connectionString));

        var app = builder.Build();

        // Configure the HTTP request pipeline.
        if(!app.Environment.IsDevelopment()) {
            app.UseExceptionHandler("/Error", createScopeForErrors: true);
            // The default HSTS value is 30 days. You may want to change this for production scenarios, see https://aka.ms/aspnetcore-hsts.
            app.UseHsts();
        }

        app.UseHttpsRedirection();

        app.UseAntiforgery();

        app.MapStaticAssets();
        app.MapRazorComponents<App>()
            .AddInteractiveServerRenderMode();

        app.Run();
    }

    private static void SetupBASS() {
        BassMixHandle = BassMix.BASS_Mixer_StreamCreate(44100, 2, BASSFlag.BASS_MIXER_NONSTOP | BASSFlag.BASS_MIXER_NORAMPIN);
        Bass.BASS_ChannelSetAttribute(BassMixHandle, BASSAttribute.BASS_ATTRIB_BUFFER, 0);

        int encodeHandle = BassEnc_Mp3.BASS_Encode_MP3_Start(BassMixHandle, "-b320", BASSEncode.BASS_ENCODE_NOHEAD | BASSEncode.BASS_ENCODE_AUTOFREE, null, IntPtr.Zero);
        int serverHandle = BassEnc.BASS_Encode_ServerInit(encodeHandle, $"2132/stream", 16384 / 2, 16384, BASSEncodeServer.BASS_ENCODE_SERVER_DEFAULT, null, IntPtr.Zero);
        Bass.BASS_ChannelPlay(BassMixHandle, true);
    }

    public static bool InitBASS() {
        char c = Runtime.PathSeparator;
        string platform = Runtime.Platform.ToString().ToLower();
        string architecture = Environment.Is64BitProcess || Runtime.Platform == Runtime.Platforms.Mac ? "x64" : "x86";

        if(platform.StartsWith("arm")) {
            architecture = platform.EndsWith("hard") ? "hardfp" : "softfp";  // "armhf" : "armel";
            platform = "arm";
        }

        string srcDir = Path.Combine(Runtime.RunningDirectory, $"bass{c}{platform}{c}{architecture}{c}");

#if DEBUG
        //Console.WriteLine($"Platform: {platform}");
        //Console.WriteLine($"Architecture: {architecture}");
        //Console.WriteLine($"BASS: {srcDir}");
#endif

        foreach(string srcFile in Directory.GetFiles(srcDir)) {
            string trgFile = Path.Combine(Runtime.RunningDirectory, Path.GetFileName(srcFile));

            if(File.Exists(trgFile)) File.Delete(trgFile);

            try {
                File.Copy(srcFile, trgFile, true);
            } catch {
                return false;
            }
        }

        string streamsDir = Path.Combine(Runtime.RunningDirectory, "streams");
        if(!Directory.Exists(streamsDir)) Directory.CreateDirectory(streamsDir);

        if(secrets.TryGetProperty("bassnet-reg-email", out var email) && secrets.TryGetProperty("bassnet-reg-key", out var key)) {
            BassNet.Registration(email.ToString(), key.ToString());
        }

        Bass.BASS_SetConfig(BASSConfig.BASS_CONFIG_DEV_NONSTOP, 1);

        // Device
        // -1 = default
        // 0 = no sound
        Bass.BASS_Init(-1, 44100, BASSInit.BASS_DEVICE_DEFAULT | BASSInit.BASS_DEVICE_LATENCY, IntPtr.Zero);

        BASS_INFO basInfo = new();
        Bass.BASS_GetInfo(basInfo);
        BassLatencyMs = basInfo.latency;

        return true;
    }
}