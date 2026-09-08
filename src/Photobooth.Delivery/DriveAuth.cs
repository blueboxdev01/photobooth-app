using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text;
using Google.Apis.Auth.OAuth2;
using Google.Apis.Auth.OAuth2.Flows;
using Google.Apis.Auth.OAuth2.Responses;
using Google.Apis.Drive.v3;
using Google.Apis.Services;
using Google.Apis.Util.Store;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Photobooth.Delivery;

/// <summary>
/// Holds the booth's Google credential and hands out a Drive client.
///
/// Three choices here are the difference between this working and failing weekly,
/// and none of them are obvious:
///
/// <list type="bullet">
/// <item>
/// <b>Installed-app OAuth, never a service account.</b> Service accounts have no
/// storage quota of their own and cannot upload to a personal My Drive at all.
/// </item>
/// <item>
/// <b>The <c>drive.file</c> scope only.</b> It reaches solely the files this app
/// created -- it cannot see the rest of the account's Drive -- and it is
/// non-sensitive, so Google requires no verification or security assessment.
/// </item>
/// <item>
/// <b>The consent screen must be published, not left in Testing.</b> That is set
/// in the Google console rather than here, but in Testing the refresh token is
/// revoked after seven days and the booth stops uploading silently. See
/// docs/DRIVE-SETUP.md.
/// </item>
/// </list>
/// </summary>
public sealed class DriveAuth(IOptions<DriveOptions> options, ILogger<DriveAuth> logger)
{
    /// <summary>Files this app created, and nothing else in the account.</summary>
    private static readonly string[] Scopes = [DriveService.ScopeConstants.DriveFile];

    private const string UserKey = "booth";

    private readonly DriveOptions _options = options.Value;
    private readonly Lock _sync = new();

    private UserCredential? _credential;

    public bool Configured =>
        !string.IsNullOrWhiteSpace(_options.ClientId)
        && !string.IsNullOrWhiteSpace(_options.ClientSecret);

    /// <summary>A token is on disk, so uploads should work without anyone signing in.</summary>
    public bool Authorised => Configured && Store().HasToken(UserKey);

    /// <summary>The account the token belongs to, for the console. Null until known.</summary>
    public string? Account { get; private set; }

    /// <summary>
    /// Sign in, opening the browser. Only ever called from the Setup page, never
    /// from the upload queue: a booth mid-event must not pop a browser window
    /// over the guest display because a token expired.
    /// </summary>
    public async Task<UserCredential> AuthorizeAsync(CancellationToken cancellationToken)
    {
        if (!Configured)
        {
            throw new InvalidOperationException(
                "No Google OAuth client is configured. Put ClientId and ClientSecret under "
                + "Delivery:Drive in appsettings.Local.json -- see docs/DRIVE-SETUP.md.");
        }

        var credential = await GoogleWebAuthorizationBroker.AuthorizeAsync(
            new ClientSecrets { ClientId = _options.ClientId, ClientSecret = _options.ClientSecret },
            Scopes,
            UserKey,
            cancellationToken,
            Store());

        lock (_sync)
        {
            _credential = credential;
        }

        logger.LogInformation("Google Drive authorised.");
        return credential;
    }

    /// <summary>
    /// A credential for uploading, refreshed if needed. Null when nobody has
    /// signed in -- the caller turns that into a "press Re-authorise" message
    /// rather than trying to open a browser.
    /// </summary>
    public async Task<UserCredential?> CredentialAsync(CancellationToken cancellationToken)
    {
        if (!Configured)
        {
            return null;
        }

        UserCredential? existing;
        lock (_sync)
        {
            existing = _credential;
        }

        if (existing is null)
        {
            var token = await Store().GetAsync<TokenResponse>(UserKey);
            if (token is null)
            {
                return null;
            }

            var flow = new GoogleAuthorizationCodeFlow(new GoogleAuthorizationCodeFlow.Initializer
            {
                ClientSecrets = new ClientSecrets
                {
                    ClientId = _options.ClientId,
                    ClientSecret = _options.ClientSecret,
                },
                Scopes = Scopes,
                DataStore = Store(),
            });

            existing = new UserCredential(flow, UserKey, token);
            lock (_sync)
            {
                _credential = existing;
            }
        }

        if (existing.Token.IsStale)
        {
            // Throws TokenResponseException when the refresh token has been
            // revoked, which DrivePublisher turns into NeedsAuthorisation.
            await existing.RefreshTokenAsync(cancellationToken);
        }

        return existing;
    }

    public DriveService ServiceFor(UserCredential credential) =>
        new(new BaseClientService.Initializer
        {
            HttpClientInitializer = credential,
            ApplicationName = "Photobooth",
        });

    /// <summary>Forget the token, so the next publish reports that nobody is signed in.</summary>
    public async Task SignOutAsync()
    {
        lock (_sync)
        {
            _credential = null;
        }

        Account = null;
        await Store().ClearAsync();
    }

    /// <summary>
    /// Which Google account the token belongs to, so the Setup page can show it.
    /// Signing the booth in to a personal account by mistake is easy to do and
    /// otherwise invisible until the photos are in the wrong Drive.
    /// </summary>
    public async Task<string?> RefreshAccountAsync(CancellationToken cancellationToken)
    {
        try
        {
            var credential = await CredentialAsync(cancellationToken);
            if (credential is null)
            {
                return Account = null;
            }

            using var drive = ServiceFor(credential);
            var request = drive.About.Get();
            request.Fields = "user";
            var about = await request.ExecuteAsync(cancellationToken);
            return Account = about.User?.EmailAddress;
        }
        catch (Exception ex)
        {
            // Never fatal: this is a label on a settings page, not the upload path.
            logger.LogDebug(ex, "Could not read the Drive account name.");
            return Account;
        }
    }

    private DpapiTokenStore Store() => new(_options.TokenStore);
}

/// <summary>
/// The refresh token, encrypted for the Windows user account that owns the booth.
///
/// Google's own FileDataStore writes the refresh token as plain JSON. That token
/// is a long-lived key to the booth's Drive, and a laptop taken to events is
/// exactly the sort of machine that gets left on a table, so it is encrypted at
/// rest with DPAPI: readable by this Windows user on this machine, and useless
/// if the file is copied off.
/// </summary>
[SupportedOSPlatform("windows")]
internal sealed class DpapiTokenStore(string path) : IDataStore
{
    private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("Photobooth.Delivery.Drive");

    private string PathFor(string key) =>
        Path.Combine(
            Path.GetDirectoryName(Path.GetFullPath(path)) ?? ".",
            $"{Path.GetFileNameWithoutExtension(path)}-{Sanitise(key)}.bin");

    /// <summary>
    /// Whether a token has been stored for this user key. Google's flow calls
    /// StoreAsync with the user id as the key, so this is the same file the
    /// credential would be loaded from.
    /// </summary>
    public bool HasToken(string key) => File.Exists(PathFor(key));

    public Task StoreAsync<T>(string key, T value)
    {
        var json = Google.Apis.Json.NewtonsoftJsonSerializer.Instance.Serialize(value);
        var file = PathFor(key);
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);

        File.WriteAllBytes(file, ProtectedData.Protect(
            Encoding.UTF8.GetBytes(json), Entropy, DataProtectionScope.CurrentUser));

        return Task.CompletedTask;
    }

    public Task<T> GetAsync<T>(string key)
    {
        var file = PathFor(key);
        if (!File.Exists(file))
        {
            return Task.FromResult<T>(default!);
        }

        try
        {
            var json = Encoding.UTF8.GetString(ProtectedData.Unprotect(
                File.ReadAllBytes(file), Entropy, DataProtectionScope.CurrentUser));

            return Task.FromResult(
                Google.Apis.Json.NewtonsoftJsonSerializer.Instance.Deserialize<T>(json));
        }
        catch (CryptographicException)
        {
            // Copied from another machine or another Windows account. Treat it as
            // no token rather than a crash: the fix is to sign in again.
            return Task.FromResult<T>(default!);
        }
    }

    public Task DeleteAsync<T>(string key)
    {
        File.Delete(PathFor(key));
        return Task.CompletedTask;
    }

    public Task ClearAsync()
    {
        var folder = Path.GetDirectoryName(Path.GetFullPath(path));
        if (folder is null || !Directory.Exists(folder))
        {
            return Task.CompletedTask;
        }

        foreach (var file in Directory.EnumerateFiles(
                     folder, $"{Path.GetFileNameWithoutExtension(path)}-*.bin"))
        {
            File.Delete(file);
        }

        return Task.CompletedTask;
    }

    private static string Sanitise(string key) =>
        new([.. key.Select(c => char.IsLetterOrDigit(c) ? c : '_')]);
}
