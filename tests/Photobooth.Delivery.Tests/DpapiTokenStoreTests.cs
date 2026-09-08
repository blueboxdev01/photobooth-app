using Google.Apis.Auth.OAuth2.Responses;
using Photobooth.Delivery;

namespace Photobooth.Delivery.Tests;

/// <summary>
/// The refresh token on disk.
///
/// Worth testing directly because a booth that has quietly lost its sign-in
/// looks exactly like a booth with no network, and this is where that would
/// hide. The naming matters as much as the encryption: <c>Authorised</c> is a
/// file-existence check, so if the path this writes to and the path that checks
/// ever disagree, the console reports "not signed in" while a perfectly good
/// token sits on disk.
/// </summary>
public sealed class DpapiTokenStoreTests : IDisposable
{
    private readonly string _folder = Path.Combine(
        Path.GetTempPath(), $"pb-token-{Guid.NewGuid():N}");

    private string Path_ => System.IO.Path.Combine(_folder, "drive-token.bin");

    public void Dispose()
    {
        try { Directory.Delete(_folder, recursive: true); } catch { /* best effort */ }
    }

    private static TokenResponse ATokenResponse() => new()
    {
        AccessToken = "access-123",
        RefreshToken = "refresh-abc",
        ExpiresInSeconds = 3600,
        IssuedUtc = new DateTime(2026, 9, 8, 12, 0, 0, DateTimeKind.Utc),
    };

    [Fact]
    public async Task A_stored_token_comes_back()
    {
        var store = new DpapiTokenStore(Path_);
        await store.StoreAsync("booth", ATokenResponse());

        var read = await store.GetAsync<TokenResponse>("booth");

        Assert.NotNull(read);
        Assert.Equal("refresh-abc", read.RefreshToken);
        Assert.Equal("access-123", read.AccessToken);
    }

    /// <summary>
    /// The check the Setup page relies on. If this and StoreAsync ever disagree
    /// about the filename, the booth reports itself signed out while holding a
    /// working token.
    /// </summary>
    [Fact]
    public async Task Storing_a_token_makes_HasToken_true_for_that_key()
    {
        var store = new DpapiTokenStore(Path_);
        Assert.False(store.HasToken("booth"));

        await store.StoreAsync("booth", ATokenResponse());

        Assert.True(store.HasToken("booth"));
    }

    /// <summary>A fresh machine, or a booth nobody has signed in on yet.</summary>
    [Fact]
    public async Task Nothing_stored_reads_back_as_nothing()
    {
        var store = new DpapiTokenStore(Path_);

        Assert.False(store.HasToken("booth"));
        Assert.Null(await store.GetAsync<TokenResponse>("booth"));
    }

    [Fact]
    public async Task It_survives_being_reopened()
    {
        await new DpapiTokenStore(Path_).StoreAsync("booth", ATokenResponse());

        // A new instance, as a restarted app would build.
        var reopened = new DpapiTokenStore(Path_);

        Assert.True(reopened.HasToken("booth"));
        Assert.Equal("refresh-abc", (await reopened.GetAsync<TokenResponse>("booth"))!.RefreshToken);
    }

    /// <summary>It is encrypted at rest, not sitting there as readable JSON.</summary>
    [Fact]
    public async Task The_refresh_token_is_not_readable_on_disk()
    {
        var store = new DpapiTokenStore(Path_);
        await store.StoreAsync("booth", ATokenResponse());

        var file = Directory.EnumerateFiles(_folder, "drive-token-*.bin").Single();
        var raw = await File.ReadAllBytesAsync(file);

        Assert.DoesNotContain("refresh-abc", System.Text.Encoding.UTF8.GetString(raw));
        Assert.DoesNotContain("refresh-abc", System.Text.Encoding.Unicode.GetString(raw));
    }

    /// <summary>
    /// Copied from another machine or another Windows account: DPAPI cannot
    /// decrypt it. That has to read as "sign in again", not as a crash on the
    /// upload path.
    /// </summary>
    [Fact]
    public async Task A_token_this_machine_cannot_decrypt_reads_as_no_token()
    {
        var store = new DpapiTokenStore(Path_);
        await store.StoreAsync("booth", ATokenResponse());

        var file = Directory.EnumerateFiles(_folder, "drive-token-*.bin").Single();
        await File.WriteAllBytesAsync(file, [1, 2, 3, 4, 5, 6, 7, 8]);

        Assert.Null(await store.GetAsync<TokenResponse>("booth"));
    }

    [Fact]
    public async Task Signing_out_removes_it()
    {
        var store = new DpapiTokenStore(Path_);
        await store.StoreAsync("booth", ATokenResponse());

        await store.ClearAsync();

        Assert.False(store.HasToken("booth"));
        Assert.Null(await store.GetAsync<TokenResponse>("booth"));
    }
}
