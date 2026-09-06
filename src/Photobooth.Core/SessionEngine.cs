using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Photobooth.Core;

/// <summary>
/// Drives one guest through a session.
///
/// The engine never touches the filesystem, the camera, or the network -- it is
/// fed photos and commands, and emits snapshots. That is what keeps the tricky
/// part (illegal transitions, retakes, timeouts) testable without hardware.
///
/// The countdown is advisory throughout: the app cannot fire the shutter, so a
/// photo may land early, late, or not at all, and every transition here is
/// written to tolerate that.
/// </summary>
public sealed class SessionEngine : IDisposable
{
    private readonly SessionSettings _options;
    private readonly ITemplateProvider _templates;
    private readonly TimeProvider _time;
    private readonly ILogger<SessionEngine> _logger;

    private readonly Lock _sync = new();
    private readonly List<CapturedPhoto> _photos = [];

    /// <summary>
    /// Which photo goes in which slot: entry i holds the index into
    /// <see cref="_photos"/> that the strip's slot i draws.
    ///
    /// Kept separate from <see cref="_photos"/>, which stays in capture order, so
    /// that "retake the last shot" keeps meaning the last shot *taken* however the
    /// operator has since rearranged the strip.
    /// </summary>
    private readonly List<int> _order = [];

    /// <summary>
    /// The slot a retaken shot came out of, so its replacement goes back into the
    /// same place rather than to the end. Null when no retake is outstanding.
    /// </summary>
    private int? _retakeSlot;

    private ITimer? _timer;
    private SessionState _state = SessionState.Idle;
    private DateTimeOffset? _countdownEnds;
    private DateTimeOffset? _timeoutAt;
    private DateTimeOffset? _startedUtc;
    private string? _message;
    private string? _stripUrl;
    private string? _sessionFolder;

    public SessionEngine(
        IOptions<SessionSettings> options,
        ITemplateProvider templates,
        ILogger<SessionEngine> logger,
        TimeProvider? timeProvider = null)
    {
        _options = options.Value;
        _templates = templates;
        _logger = logger;
        _time = timeProvider ?? TimeProvider.System;
    }

    /// <summary>Raised after every state change, outside the lock.</summary>
    public event EventHandler<SessionSnapshot>? Changed;

    /// <summary>
    /// Comes from the template's slot count, not a separate setting. Switching to
    /// a four-frame strip makes sessions capture four photos with nothing else to
    /// remember to change.
    /// </summary>
    public int ShotCount => _templates.Current.ShotCount;

    public SessionSnapshot Snapshot
    {
        get { lock (_sync) { return Build(); } }
    }

    /// <summary>Start a new session for the next guest.</summary>
    public SessionSnapshot Arm()
    {
        SessionSnapshot snapshot;
        lock (_sync)
        {
            _photos.Clear();
            _order.Clear();
            _retakeSlot = null;
            _startedUtc = _time.GetUtcNow();
            _message = null;
            _stripUrl = null;
            _sessionFolder = null;
            BeginCountdown();
            snapshot = Build();
        }

        _logger.LogInformation("Session armed for {Count} shots.", ShotCount);
        Publish(snapshot);
        return snapshot;
    }

    /// <summary>
    /// A photo landed. Accepted during Countdown as well as Collecting: with an
    /// advisory countdown the operator often presses on "1" rather than after it,
    /// and discarding a real photo because it was 200 ms early would be worse than
    /// the countdown being slightly out of step.
    /// </summary>
    public bool SubmitPhoto(CapturedPhoto photo)
    {
        SessionSnapshot snapshot;
        lock (_sync)
        {
            if (_state is not (SessionState.Countdown or SessionState.Collecting
                or SessionState.TimedOut))
            {
                _logger.LogDebug("Ignoring {File}: session is {State}.", photo.FileName, _state);
                return false;
            }

            _photos.Add(photo);

            // A replacement goes back into the slot its predecessor occupied; a
            // brand-new shot goes on the end.
            var captured = _photos.Count - 1;
            if (_retakeSlot is { } slot)
            {
                _order.Insert(Math.Min(slot, _order.Count), captured);
                _retakeSlot = null;
            }
            else
            {
                _order.Add(captured);
            }

            _message = null;

            if (_photos.Count >= ShotCount)
            {
                StopTimer();
                _state = SessionState.ReviewShots;
                _countdownEnds = null;
                _timeoutAt = null;
                _logger.LogInformation("All {Count} shots captured.", _photos.Count);
            }
            else
            {
                BeginCountdown();
            }

            snapshot = Build();
        }

        Publish(snapshot);
        return true;
    }

    /// <summary>Discard the most recent shot and take that pose again.</summary>
    public SessionSnapshot RetakeLast()
    {
        SessionSnapshot snapshot;
        lock (_sync)
        {
            if (_state is SessionState.Idle or SessionState.Done)
            {
                return Build();
            }

            if (_photos.Count > 0)
            {
                var dropped = _photos[^1];
                var captured = _photos.Count - 1;

                // Remember where it sat in the strip before dropping it, so a
                // rearrangement survives the retake.
                var slot = _order.IndexOf(captured);
                _retakeSlot = slot >= 0 ? slot : null;
                _order.Remove(captured);

                _photos.RemoveAt(captured);
                _logger.LogInformation(
                    "Retaking; dropped {File} from slot {Slot}.", dropped.FileName, slot + 1);
            }

            _message = null;
            BeginCountdown();
            snapshot = Build();
        }

        Publish(snapshot);
        return snapshot;
    }

    /// <summary>
    /// Rearrange which shot goes in which slot of the strip.
    ///
    /// <paramref name="positions"/> is expressed in the order the operator is
    /// currently looking at, not capture order: entry i names the position that
    /// should move into slot i. Dragging the fourth thumbnail to the front of six
    /// therefore sends [3, 0, 1, 2, 4, 5].
    ///
    /// Nothing is copied or re-encoded -- this only decides which file the
    /// compositor reads into which slot, and it is refused outside review because
    /// that is the only point at which the full set exists and none of it has been
    /// written anywhere yet.
    /// </summary>
    public ReorderResult Reorder(IReadOnlyList<int> positions)
    {
        SessionSnapshot snapshot;
        lock (_sync)
        {
            if (_state != SessionState.ReviewShots)
            {
                return new ReorderResult(
                    false,
                    $"The shots can only be rearranged while reviewing them, not in {_state}.",
                    Build());
            }

            if (positions.Count != _order.Count)
            {
                return new ReorderResult(
                    false,
                    $"Expected {_order.Count} positions, got {positions.Count}.",
                    Build());
            }

            // A permutation, or the strip would silently lose or duplicate a photo.
            if (positions.Distinct().Count() != positions.Count
                || positions.Any(p => p < 0 || p >= _order.Count))
            {
                return new ReorderResult(
                    false,
                    $"Positions must be each of 0..{_order.Count - 1} exactly once.",
                    Build());
            }

            var rearranged = positions.Select(p => _order[p]).ToList();
            _order.Clear();
            _order.AddRange(rearranged);

            snapshot = Build();
        }

        _logger.LogInformation(
            "Shots rearranged to {Order}.",
            string.Join(", ", snapshot.Order.Select(i => i + 1)));
        Publish(snapshot);
        return new ReorderResult(true, null, snapshot);
    }

    /// <summary>Put the shots back into the order they were taken in.</summary>
    public SessionSnapshot ResetOrder()
    {
        SessionSnapshot snapshot;
        lock (_sync)
        {
            if (_state != SessionState.ReviewShots)
            {
                return Build();
            }

            _order.Sort();
            snapshot = Build();
        }

        _logger.LogInformation("Shot order reset to capture order.");
        Publish(snapshot);
        return snapshot;
    }

    /// <summary>Keep waiting after a timeout, without losing the shots so far.</summary>
    public SessionSnapshot Resume()
    {
        SessionSnapshot snapshot;
        lock (_sync)
        {
            if (_state != SessionState.TimedOut)
            {
                return Build();
            }

            _message = null;
            BeginCollecting();
            snapshot = Build();
        }

        Publish(snapshot);
        return snapshot;
    }

    /// <summary>
    /// Accept the shots and start building the strip.
    ///
    /// This only moves the state; the compositing and archiving happen outside,
    /// which is what keeps this class free of the filesystem. Whoever does the
    /// work calls <see cref="CompleteComposing"/> or <see cref="FailComposing"/>.
    /// </summary>
    public SessionSnapshot Accept()
    {
        SessionSnapshot snapshot;
        lock (_sync)
        {
            if (_state != SessionState.ReviewShots)
            {
                return Build();
            }

            StopTimer();
            _state = SessionState.Composing;
            _countdownEnds = null;
            _timeoutAt = null;
            snapshot = Build();
        }

        _logger.LogInformation("Session accepted; composing.");
        Publish(snapshot);
        return snapshot;
    }

    /// <summary>The strip is built and archived.</summary>
    public SessionSnapshot CompleteComposing(string stripUrl, string sessionFolder)
    {
        SessionSnapshot snapshot;
        lock (_sync)
        {
            if (_state != SessionState.Composing)
            {
                return Build();
            }

            _stripUrl = stripUrl;
            _sessionFolder = sessionFolder;
            _state = SessionState.Done;
            snapshot = Build();
        }

        Publish(snapshot);
        return snapshot;
    }

    /// <summary>
    /// Composing failed. Returns to review rather than Idle: the photos are still
    /// good, and throwing away a guest's session because the strip failed to
    /// render would be the wrong trade.
    /// </summary>
    public SessionSnapshot FailComposing(string message)
    {
        SessionSnapshot snapshot;
        lock (_sync)
        {
            if (_state != SessionState.Composing)
            {
                return Build();
            }

            _state = SessionState.ReviewShots;
            _message = message;
            snapshot = Build();
        }

        _logger.LogError("Composing failed: {Message}", message);
        Publish(snapshot);
        return snapshot;
    }

    /// <summary>Abandon the session from any state and return to the attract screen.</summary>
    public SessionSnapshot Abort(string? reason = null)
    {
        SessionSnapshot snapshot;
        lock (_sync)
        {
            StopTimer();
            _photos.Clear();
            _order.Clear();
            _retakeSlot = null;
            _state = SessionState.Idle;
            _countdownEnds = null;
            _timeoutAt = null;
            _startedUtc = null;
            _message = reason;
            _stripUrl = null;
            _sessionFolder = null;
            snapshot = Build();
        }

        _logger.LogInformation("Session aborted. {Reason}", reason ?? "(no reason given)");
        Publish(snapshot);
        return snapshot;
    }

    // --- internals; all callers already hold the lock ---

    private void BeginCountdown()
    {
        _state = SessionState.Countdown;
        _countdownEnds = _time.GetUtcNow().AddSeconds(_options.CountdownSeconds);
        _timeoutAt = null;
        Schedule(TimeSpan.FromSeconds(_options.CountdownSeconds));
    }

    private void BeginCollecting()
    {
        _state = SessionState.Collecting;
        _countdownEnds = null;
        _timeoutAt = _time.GetUtcNow().AddSeconds(_options.NoPhotoTimeoutSeconds);
        Schedule(TimeSpan.FromSeconds(_options.NoPhotoTimeoutSeconds));
    }

    private void Schedule(TimeSpan due)
    {
        StopTimer();
        _timer = _time.CreateTimer(_ => OnTimer(), null, due, Timeout.InfiniteTimeSpan);
    }

    private void StopTimer()
    {
        _timer?.Dispose();
        _timer = null;
    }

    private void OnTimer()
    {
        SessionSnapshot snapshot;
        lock (_sync)
        {
            switch (_state)
            {
                case SessionState.Countdown:
                    // Countdown finished; now actually wait for the photo.
                    BeginCollecting();
                    break;

                case SessionState.Collecting:
                    StopTimer();
                    _state = SessionState.TimedOut;
                    _timeoutAt = null;
                    _message =
                        $"No photo after {_options.NoPhotoTimeoutSeconds}s. Check the camera " +
                        "is awake, EOS Utility is connected, and the remote is paired.";
                    _logger.LogWarning("Session timed out waiting for shot {Shot}.",
                        _photos.Count + 1);
                    break;

                default:
                    return;
            }

            snapshot = Build();
        }

        Publish(snapshot);
    }

    private SessionSnapshot Build() => new(
        _state,
        ShotCount,
        _order.Select(i => _photos[i]).ToList(),
        _order.ToList(),
        _countdownEnds,
        _timeoutAt,
        _startedUtc,
        _message,
        _stripUrl,
        _sessionFolder);

    private void Publish(SessionSnapshot snapshot) => Changed?.Invoke(this, snapshot);

    public void Dispose()
    {
        lock (_sync)
        {
            StopTimer();
        }
    }
}
