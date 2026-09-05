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
    private readonly TimeProvider _time;
    private readonly ILogger<SessionEngine> _logger;

    private readonly Lock _sync = new();
    private readonly List<CapturedPhoto> _photos = [];

    private ITimer? _timer;
    private SessionState _state = SessionState.Idle;
    private DateTimeOffset? _countdownEnds;
    private DateTimeOffset? _timeoutAt;
    private DateTimeOffset? _startedUtc;
    private string? _message;

    public SessionEngine(
        IOptions<SessionSettings> options,
        ILogger<SessionEngine> logger,
        TimeProvider? timeProvider = null)
    {
        _options = options.Value;
        _logger = logger;
        _time = timeProvider ?? TimeProvider.System;
    }

    /// <summary>Raised after every state change, outside the lock.</summary>
    public event EventHandler<SessionSnapshot>? Changed;

    public int ShotCount => _options.ShotCount;

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
            _startedUtc = _time.GetUtcNow();
            _message = null;
            BeginCountdown();
            snapshot = Build();
        }

        _logger.LogInformation("Session armed for {Count} shots.", _options.ShotCount);
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
            _message = null;

            if (_photos.Count >= _options.ShotCount)
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
                _photos.RemoveAt(_photos.Count - 1);
                _logger.LogInformation("Retaking; dropped {File}.", dropped.FileName);
            }

            _message = null;
            BeginCountdown();
            snapshot = Build();
        }

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

    /// <summary>Accept the shots and finish. Composing and upload arrive in M4/M7.</summary>
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
            _state = SessionState.Done;
            _countdownEnds = null;
            _timeoutAt = null;
            snapshot = Build();
        }

        _logger.LogInformation("Session accepted.");
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
            _state = SessionState.Idle;
            _countdownEnds = null;
            _timeoutAt = null;
            _startedUtc = null;
            _message = reason;
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
        _options.ShotCount,
        _photos.ToList(),
        _countdownEnds,
        _timeoutAt,
        _startedUtc,
        _message);

    private void Publish(SessionSnapshot snapshot) => Changed?.Invoke(this, snapshot);

    public void Dispose()
    {
        lock (_sync)
        {
            StopTimer();
        }
    }
}
