using System.Runtime.InteropServices;
using System.Text.Json;
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

var spotify = await Connect(clientId, config.RefreshToken, doOauth, configDir);

// Windows media overlay
// Without this Windows has no idea who we are and the overlay says "Unknown app".
// Must happen before the session below is created.
SetCurrentProcessExplicitAppUserModelID("SpotSMTCSrv");

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

    first run:   SpotSMTCSrv -i <client id> -c C:\path\spotsmtc
    after that:  SpotSMTCSrv -c C:\path\spotsmtc
    """);

// Config
static Config Load(string? dir)
{
    if (dir is null) return new Config(null, null);

    var file = ConfigFile(dir);
    if (!File.Exists(file)) return new Config(null, null);

    try
    {
        return JsonSerializer.Deserialize<Config>(File.ReadAllText(file)) ?? new Config(null, null);
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
        new Config(clientId, refreshToken),
        new JsonSerializerOptions { WriteIndented = true }));
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
    var done = new TaskCompletionSource<PKCETokenResponse>();

    var server = new EmbedIOAuthServer(redirect, 5000);
    await server.Start();
    server.AuthorizationCodeReceived += async (_, response) =>
    {
        await server.Stop();
        done.SetResult(await new OAuthClient().RequestToken(
            new PKCETokenRequest(clientId, response.Code, redirect, verifier)));
    };

    var login = new LoginRequest(server.BaseUri, clientId, LoginRequest.ResponseType.Code)
    {
        CodeChallengeMethod = "S256",
        CodeChallenge = challenge,
        Scope = new List<string> { Scopes.UserReadPlaybackState, Scopes.UserModifyPlaybackState },
    };

    Console.WriteLine("Opening your browser to log in to Spotify...");
    BrowserUtil.Open(login.ToUri());
    return await done.Task;
}

[DllImport("shell32.dll", CharSet = CharSet.Unicode, PreserveSig = false)]
static extern void SetCurrentProcessExplicitAppUserModelID(string appID);

record Config(string? ClientId, string? RefreshToken);
