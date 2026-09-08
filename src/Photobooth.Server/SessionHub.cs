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

    /// <summary>
    /// Delivery, sent separately from the session state because an upload
    /// outlives the session that produced it.
    /// </summary>
    public const string DeliveryMessage = "delivery";

    private readonly SessionEngine _engine;
    private readonly SessionCoordinator _coordinator;

    public SessionHub(SessionEngine engine, SessionCoordinator coordinator)
    {
        _engine = engine;
        _coordinator = coordinator;
    }

    /// <summary>New window: send it the current state rather than making it wait.</summary>
    public override async Task OnConnectedAsync()
    {
        await Clients.Caller.SendAsync(StateMessage, _engine.Snapshot);
        await Clients.Caller.SendAsync(DeliveryMessage, _coordinator.CurrentDelivery());
        await base.OnConnectedAsync();
    }
}
