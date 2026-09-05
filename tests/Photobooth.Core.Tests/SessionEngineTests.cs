using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using Photobooth.Core;

namespace Photobooth.Core.Tests;

public class SessionEngineTests
{
    private const int Shots = 3;
    private const int Countdown = 3;
    private const int Timeout = 20;

    private static (SessionEngine Engine, FakeTimeProvider Time) Build()
    {
        var time = new FakeTimeProvider(new DateTimeOffset(2026, 9, 5, 12, 0, 0, TimeSpan.Zero));
        var options = Options.Create(new SessionOptions
        {
            ShotCount = Shots,
            CountdownSeconds = Countdown,
            NoPhotoTimeoutSeconds = Timeout,
        });

        return (new SessionEngine(options, NullLogger<SessionEngine>.Instance, time), time);
    }

    private static CapturedPhoto Photo(string name) =>
        new($@"C:\watch\{name}", name, 250_000, DateTimeOffset.UtcNow);

    /// <summary>Countdown, then the wait for the photo to actually land.</summary>
    private static void AdvanceToCollecting(FakeTimeProvider time) =>
        time.Advance(TimeSpan.FromSeconds(Countdown));

    [Fact]
    public void Starts_idle()
    {
        var (engine, _) = Build();

        Assert.Equal(SessionState.Idle, engine.Snapshot.State);
        Assert.Empty(engine.Snapshot.Photos);
    }

    [Fact]
    public void Arming_starts_the_advisory_countdown()
    {
        var (engine, _) = Build();

        var snapshot = engine.Arm();

        Assert.Equal(SessionState.Countdown, snapshot.State);
        Assert.NotNull(snapshot.CountdownEndsUtc);
        Assert.NotNull(snapshot.StartedUtc);
        Assert.Equal(1, snapshot.CurrentShot);
    }

    [Fact]
    public void Countdown_gives_way_to_collecting()
    {
        var (engine, time) = Build();
        engine.Arm();

        AdvanceToCollecting(time);

        Assert.Equal(SessionState.Collecting, engine.Snapshot.State);
        Assert.NotNull(engine.Snapshot.TimeoutAtUtc);
    }

    [Fact]
    public void Photos_advance_through_the_shots_then_reach_review()
    {
        var (engine, time) = Build();
        engine.Arm();

        for (var i = 1; i <= Shots; i++)
        {
            AdvanceToCollecting(time);
            Assert.True(engine.SubmitPhoto(Photo($"IMG_000{i}.JPG")));
        }

        var snapshot = engine.Snapshot;
        Assert.Equal(SessionState.ReviewShots, snapshot.State);
        Assert.Equal(Shots, snapshot.CapturedCount);
    }

    [Fact]
    public void A_photo_arriving_during_the_countdown_is_kept()
    {
        // The countdown is advisory: the operator often presses on "1" rather than
        // after it. Discarding a real photo for being early would be worse than the
        // countdown drifting.
        var (engine, _) = Build();
        engine.Arm();

        Assert.Equal(SessionState.Countdown, engine.Snapshot.State);
        Assert.True(engine.SubmitPhoto(Photo("IMG_0001.JPG")));

        Assert.Equal(1, engine.Snapshot.CapturedCount);
    }

    [Fact]
    public void Photos_are_ignored_when_no_session_is_running()
    {
        var (engine, _) = Build();

        Assert.False(engine.SubmitPhoto(Photo("IMG_0001.JPG")));
        Assert.Empty(engine.Snapshot.Photos);
    }

    [Fact]
    public void Photos_are_ignored_after_the_session_is_accepted()
    {
        var (engine, time) = Build();
        engine.Arm();
        for (var i = 1; i <= Shots; i++)
        {
            AdvanceToCollecting(time);
            engine.SubmitPhoto(Photo($"IMG_000{i}.JPG"));
        }

        engine.Accept();

        Assert.False(engine.SubmitPhoto(Photo("IMG_0099.JPG")));
        Assert.Equal(Shots, engine.Snapshot.CapturedCount);
    }

    [Fact]
    public void Retake_discards_exactly_one_shot()
    {
        var (engine, time) = Build();
        engine.Arm();
        AdvanceToCollecting(time);
        engine.SubmitPhoto(Photo("IMG_0001.JPG"));
        AdvanceToCollecting(time);
        engine.SubmitPhoto(Photo("IMG_0002.JPG"));

        var snapshot = engine.RetakeLast();

        Assert.Equal(SessionState.Countdown, snapshot.State);
        Assert.Equal(1, snapshot.CapturedCount);
        Assert.Equal("IMG_0001.JPG", snapshot.Photos.Single().FileName);
    }

    [Fact]
    public void Retake_from_review_reopens_the_last_pose()
    {
        var (engine, time) = Build();
        engine.Arm();
        for (var i = 1; i <= Shots; i++)
        {
            AdvanceToCollecting(time);
            engine.SubmitPhoto(Photo($"IMG_000{i}.JPG"));
        }

        Assert.Equal(SessionState.ReviewShots, engine.Snapshot.State);

        var snapshot = engine.RetakeLast();

        Assert.Equal(SessionState.Countdown, snapshot.State);
        Assert.Equal(Shots - 1, snapshot.CapturedCount);
        Assert.Equal(Shots, snapshot.CurrentShot);
    }

    [Fact]
    public void Retake_does_nothing_when_idle()
    {
        var (engine, _) = Build();

        var snapshot = engine.RetakeLast();

        Assert.Equal(SessionState.Idle, snapshot.State);
        Assert.Empty(snapshot.Photos);
    }

    [Fact]
    public void No_photo_within_the_window_times_out()
    {
        var (engine, time) = Build();
        engine.Arm();
        AdvanceToCollecting(time);

        time.Advance(TimeSpan.FromSeconds(Timeout));

        var snapshot = engine.Snapshot;
        Assert.Equal(SessionState.TimedOut, snapshot.State);
        Assert.NotNull(snapshot.Message);
    }

    [Fact]
    public void Timing_out_keeps_the_shots_already_captured()
    {
        var (engine, time) = Build();
        engine.Arm();
        AdvanceToCollecting(time);
        engine.SubmitPhoto(Photo("IMG_0001.JPG"));
        AdvanceToCollecting(time);

        time.Advance(TimeSpan.FromSeconds(Timeout));

        Assert.Equal(SessionState.TimedOut, engine.Snapshot.State);
        Assert.Equal(1, engine.Snapshot.CapturedCount);
    }

    [Fact]
    public void A_late_photo_is_still_accepted_after_a_timeout()
    {
        // A stalled transfer that eventually completes should rescue the session
        // rather than be thrown away.
        var (engine, time) = Build();
        engine.Arm();
        AdvanceToCollecting(time);
        time.Advance(TimeSpan.FromSeconds(Timeout));
        Assert.Equal(SessionState.TimedOut, engine.Snapshot.State);

        Assert.True(engine.SubmitPhoto(Photo("IMG_0001.JPG")));

        Assert.Equal(1, engine.Snapshot.CapturedCount);
        Assert.Equal(SessionState.Countdown, engine.Snapshot.State);
    }

    [Fact]
    public void Resume_returns_to_waiting_without_losing_shots()
    {
        var (engine, time) = Build();
        engine.Arm();
        AdvanceToCollecting(time);
        engine.SubmitPhoto(Photo("IMG_0001.JPG"));
        AdvanceToCollecting(time);
        time.Advance(TimeSpan.FromSeconds(Timeout));

        var snapshot = engine.Resume();

        Assert.Equal(SessionState.Collecting, snapshot.State);
        Assert.Equal(1, snapshot.CapturedCount);
    }

    [Fact]
    public void Accept_is_rejected_before_every_shot_is_in()
    {
        var (engine, time) = Build();
        engine.Arm();
        AdvanceToCollecting(time);
        engine.SubmitPhoto(Photo("IMG_0001.JPG"));

        var snapshot = engine.Accept();

        Assert.NotEqual(SessionState.Done, snapshot.State);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(Shots)]
    public void Abort_returns_to_idle_and_clears_photos_from_any_state(int photosTaken)
    {
        var (engine, time) = Build();
        engine.Arm();
        for (var i = 1; i <= photosTaken; i++)
        {
            AdvanceToCollecting(time);
            engine.SubmitPhoto(Photo($"IMG_000{i}.JPG"));
        }

        var snapshot = engine.Abort("operator cancelled");

        Assert.Equal(SessionState.Idle, snapshot.State);
        Assert.Empty(snapshot.Photos);
        Assert.Equal("operator cancelled", snapshot.Message);
    }

    [Fact]
    public void Aborting_stops_the_timer_so_no_stray_timeout_fires()
    {
        var (engine, time) = Build();
        engine.Arm();
        AdvanceToCollecting(time);
        engine.Abort();

        time.Advance(TimeSpan.FromSeconds(Timeout * 2));

        Assert.Equal(SessionState.Idle, engine.Snapshot.State);
    }

    [Fact]
    public void Arming_again_clears_the_previous_guests_photos()
    {
        var (engine, time) = Build();
        engine.Arm();
        AdvanceToCollecting(time);
        engine.SubmitPhoto(Photo("IMG_0001.JPG"));

        var snapshot = engine.Arm();

        Assert.Empty(snapshot.Photos);
        Assert.Equal(SessionState.Countdown, snapshot.State);
    }

    [Fact]
    public void Every_transition_is_published()
    {
        var (engine, time) = Build();
        var seen = new List<SessionState>();
        engine.Changed += (_, s) => seen.Add(s.State);

        engine.Arm();
        AdvanceToCollecting(time);
        engine.SubmitPhoto(Photo("IMG_0001.JPG"));

        Assert.Equal(
            [SessionState.Countdown, SessionState.Collecting, SessionState.Countdown],
            seen);
    }
}
