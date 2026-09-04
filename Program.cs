using System.Net;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Win32;
using SpotifyAPI.Web;
using SpotifyAPI.Web.Auth;
using Windows.Media;
using Windows.Media.Playback;
using Windows.Storage.Streams;

string? configDir = null;
string? clientIdArg = null;
var doOauth = false;

for (var i = 0; i < args.Length; i++)
{
    switch (args[i])
    {
        case "-c" or "--config" when i + 1 < args.Length: configDir = args[++i]; break;
        case "-i" or "--client-id" when i + 1 < args.Length: clientIdArg = args[++i]; break;
        case "-j" or "--oauth": doOauth = true; break;
        case "-h" or "--help": Usage(); return;
        default:
            Console.Error.WriteLine($"bad argument: {args[i]}");
            Usage();
            Environment.ExitCode = 1;
            return;
    }
}

configDir = configDir is null ? null : Environment.ExpandEnvironmentVariables(configDir);

// Catch this now rather than after the browser dance, when Save would throw.
if (configDir is not null && File.Exists(configDir))
{
    Console.Error.WriteLine($"--config wants a directory, but {configDir} is a file.");
    Environment.ExitCode = 1;
    return;
}

var config = Load(configDir);

// A client id on the command line wins over one already in the config.
var clientId = clientIdArg ?? config.ClientId;
if (string.IsNullOrEmpty(clientId))
{
    Console.Error.WriteLine("No client id. Pass --client-id, or use a --config that has one.");
    Environment.ExitCode = 1;
    return;
}

// Windows media overlay identity.
const string appId = "SpotSMTCSrv";
SetCurrentProcessExplicitAppUserModelID(appId);
Registry.SetValue($@"HKEY_CURRENT_USER\Software\Classes\AppUserModelId\{appId}", "DisplayName", "SpotSMTC");

var spotify = await Connect(clientId, config.RefreshToken, doOauth, configDir);

var player = new MediaPlayer();
var smtc = player.SystemMediaTransportControls;
smtc.IsEnabled = true;

smtc.IsPlayEnabled = true;
smtc.IsPauseEnabled = true;
smtc.IsNextEnabled = true;
smtc.IsPreviousEnabled = true;

smtc.ButtonPressed += async (_, e) =>
{
    try
    {
        switch (e.Button)
        {
            case SystemMediaTransportControlsButton.Play: await spotify.Player.ResumePlayback(); break;
            case SystemMediaTransportControlsButton.Pause: await spotify.Player.PausePlayback(); break;
            case SystemMediaTransportControlsButton.Next: await spotify.Player.SkipNext(); break;
            case SystemMediaTransportControlsButton.Previous: await spotify.Player.SkipPrevious(); break;
        }
    }
    catch (APIException ex)
    {
        Console.WriteLine($"{e.Button} failed: {ex.Message}");
    }
};

// Spotify has no way to notify us of changes, so asking on a timer is the only option.
// ponytail: a fixed 1s poll. If you start seeing 429s, back off while nothing is playing.
Console.WriteLine("Watching Spotify. Ctrl+C to stop.");
string? lastTrackId = null;

while (true)
{
    try
    {
        var playback = await spotify.Player.GetCurrentPlayback();

        if (playback?.Item is FullTrack track)
        {
            // Only redraw when the song actually changes; Update() on every tick makes it flicker.
            if (track.Id != lastTrackId)
            {
                lastTrackId = track.Id;
                var artists = string.Join(", ", track.Artists.Select(a => a.Name));

                var display = smtc.DisplayUpdater;
                display.Type = MediaPlaybackType.Music;
                display.MusicProperties.Title = track.Name;
                display.MusicProperties.Artist = artists;
                display.MusicProperties.AlbumTitle = track.Album.Name;
                if (track.Album.Images.Count > 0)
                    display.Thumbnail = RandomAccessStreamReference.CreateFromUri(
                        new Uri(track.Album.Images[0].Url));
                display.Update();

                Console.WriteLine($"{track.Name} - {artists}");
            }

            smtc.PlaybackStatus = playback.IsPlaying
                ? MediaPlaybackStatus.Playing
                : MediaPlaybackStatus.Paused;

            // Drives the scrub bar in the overlay.
            smtc.UpdateTimelineProperties(new SystemMediaTransportControlsTimelineProperties
            {
                StartTime = TimeSpan.Zero,
                MinSeekTime = TimeSpan.Zero,
                Position = TimeSpan.FromMilliseconds(playback.ProgressMs),
                MaxSeekTime = TimeSpan.FromMilliseconds(track.DurationMs),
                EndTime = TimeSpan.FromMilliseconds(track.DurationMs),
            });
        }
        else
        {
            // Nothing playing, or it's a podcast episode rather than a track.
            smtc.PlaybackStatus = MediaPlaybackStatus.Stopped;
            lastTrackId = null;
        }
    }
    catch (APIException ex)
    {
        Console.WriteLine($"spotify: {ex.Message}");
    }

    // player is unreferenced after line 62; without this the GC collects it and the session vanishes.
    GC.KeepAlive(player);
    await Task.Delay(1000);
}

// CLI Flags
static void Usage() => Console.WriteLine("""
      -c, --config <dir>      directory to keep the client id and login in.
                              Created if it does not exist. Omit it and
                              nothing is written to disk.
      -i, --client-id <id>    client id from your Spotify developer app
      -j, --oauth             force a fresh browser login, to switch account.
                              Logging in happens on its own when there is
                              no saved login.
      -h, --help              this message

    first run:   SpotSMTC -i <client id> -c %APPDATA%\SpotSMTC
    after that:  SpotSMTC -c %APPDATA%\SpotSMTC
    """);

// Config
static Config Load(string? dir)
{
    if (dir is null) return new Config(null, null);

    var file = ConfigFile(dir);
    if (!File.Exists(file)) return new Config(null, null);

    try
    {
        var stored = JsonSerializer.Deserialize<Config>(File.ReadAllText(file)) ?? new Config(null, null);
        return stored with { RefreshToken = Unprotect(stored.RefreshToken) };
    }
    catch (JsonException ex)
    {
        Console.Error.WriteLine($"ignoring unreadable config {file}: {ex.Message}");
        return new Config(null, null);
    }
}

static void Save(string? dir, string clientId, string? refreshToken)
{
    // No --config means the login is never written down; it lasts as long as the process.
    if (dir is null || string.IsNullOrEmpty(refreshToken)) return;

    Directory.CreateDirectory(dir);
    File.WriteAllText(ConfigFile(dir), JsonSerializer.Serialize(
        new Config(clientId, Protect(refreshToken)),
        new JsonSerializerOptions { WriteIndented = true }));
}

// DPAPI, so the file is inert for any other Windows account that can read it.
static string Protect(string token) => Convert.ToBase64String(
    ProtectedData.Protect(Encoding.UTF8.GetBytes(token), null, DataProtectionScope.CurrentUser));

static string? Unprotect(string? stored)
{
    if (string.IsNullOrEmpty(stored)) return null;

    try
    {
        return Encoding.UTF8.GetString(
            ProtectedData.Unprotect(Convert.FromBase64String(stored), null, DataProtectionScope.CurrentUser));
    }
    catch (Exception ex) when (ex is CryptographicException or FormatException)
    {
        // Another account's file, or one written before this was encrypted. Log in again.
        return null;
    }
}

static string ConfigFile(string dir) => Path.Combine(dir, "config.json");

// Auth
static async Task<SpotifyClient> Connect(string clientId, string? refreshToken, bool forceLogin, string? configDir)
{
    PKCETokenResponse token;

    if (!forceLogin && !string.IsNullOrEmpty(refreshToken))
    {
        try
        {
            token = await new OAuthClient().RequestToken(
                new PKCETokenRefreshRequest(clientId, refreshToken));
        }
        catch (APIException)
        {
            // Saved login was revoked or is no longer valid; start over.
            token = await LogIn(clientId);
        }
    }
    else
    {
        token = await LogIn(clientId);
    }

    Save(configDir, clientId, token.RefreshToken);

    // Renews the access token by itself every hour, and hands us the new refresh token to store.
    var authenticator = new PKCEAuthenticator(clientId, token);
    authenticator.TokenRefreshed += (_, t) => Save(configDir, clientId, t.RefreshToken);

    return new SpotifyClient(SpotifyClientConfig.CreateDefault().WithAuthenticator(authenticator));
}

static async Task<PKCETokenResponse> LogIn(string clientId)
{
    var redirect = new Uri("http://127.0.0.1:5000/callback");
    var (verifier, challenge) = PKCEUtil.GenerateCodes();
    var state = Convert.ToHexString(RandomNumberGenerator.GetBytes(16));

    // Our own listener rather than EmbedIOAuthServer, which binds every interface.
    using var listener = new HttpListener();
    listener.Prefixes.Add("http://127.0.0.1:5000/");
    listener.Start();

    var login = new LoginRequest(redirect, clientId, LoginRequest.ResponseType.Code)
    {
        CodeChallengeMethod = "S256",
        CodeChallenge = challenge,
        State = state,
        Scope = new List<string> { Scopes.UserReadPlaybackState, Scopes.UserModifyPlaybackState },
    };

    Console.WriteLine("Opening your browser to log in to Spotify...");
    BrowserUtil.Open(login.ToUri());

    // Anything without our state is someone else knocking; answer it and keep waiting.
    string? code = null, error = null;
    while (code is null && error is null)
    {
        var ctx = await listener.GetContextAsync();
        var query = ctx.Request.QueryString;

        if (query["state"] == state)
        {
            code = query["code"];
            error = query["error"];
        }

        var body = Encoding.UTF8.GetBytes(
            code is not null ? "Logged in. You can close this tab."
            : error is not null ? $"Login failed: {error}"
            : "Waiting for the Spotify callback.");
        ctx.Response.ContentType = "text/plain; charset=utf-8";
        ctx.Response.ContentLength64 = body.Length;
        await ctx.Response.OutputStream.WriteAsync(body);
        ctx.Response.Close();
    }

    if (code is null) throw new InvalidOperationException($"Spotify refused the login: {error}");

    return await new OAuthClient().RequestToken(
        new PKCETokenRequest(clientId, code, redirect, verifier));
}

[DllImport("shell32.dll", CharSet = CharSet.Unicode, PreserveSig = false)]
static extern void SetCurrentProcessExplicitAppUserModelID(string appID);

record Config(string? ClientId, string? RefreshToken);
