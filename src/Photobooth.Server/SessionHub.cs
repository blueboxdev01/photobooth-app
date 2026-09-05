using Microsoft.AspNetCore.SignalR;
using Photobooth.Core;

namespace Photobooth.Server;

/// <summary>
/// Pushes session state to both browser windows.
///
/// Replaces M1's polling: with two screens showing the same session, polling let
/// them disagree for up to a poll interval, which is exactly the moment a guest
/// is looking at the countdown.
/// </summary>
public sealed class SessionHub : Hub
{
    public const string StateMessage = "state";

    private readonly SessionEngine _engine;

    public SessionHub(SessionEngine engine) => _engine = engine;

    /// <summary>New window: send it the current state rather than making it wait.</summary>
    public override async Task OnConnectedAsync()
    {
        await Clients.Caller.SendAsync(StateMessage, _engine.Snapshot);
        await base.OnConnectedAsync();
    }
}
