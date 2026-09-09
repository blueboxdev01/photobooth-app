using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using Photobooth.Core;

namespace Photobooth.Core.Tests;

file sealed class FakeTemplateProvider(int slots) : ITemplateProvider
{
    public StripTemplate Current { get; } = new(
        "fake",
        new TemplateCanvas(1800, 1200),
        [.. Enumerable.Range(0, slots).Select(i => new TemplateSlot(0, i * 0.2, 1, 0.18))]);
}

/// <summary>
/// Retaking one shot out of the middle of a strip.
///
/// The property that matters: every shot the operator did <b>not</b> pick comes
/// through untouched and in the same position. Getting that wrong silently
/// rearranges a guest's photos, which nobody notices until the strip is printed.
/// </summary>
public class SessionRetakeTests
{
    private const int Shots = 4;
    private const int Countdown = 3;

    private static (SessionEngine Engine, FakeTimeProvider Time) Build(int shots = Shots)
    {
        var time = new FakeTimeProvider(new DateTimeOffset(2026, 9, 9, 12, 0, 0, TimeSpan.Zero));
        var options = Options.Create(new SessionSettings
        {
            CountdownSeconds = Countdown,
            NoPhotoTimeoutSeconds = 20,
        });

        return (
            new SessionEngine(
                options, new FakeTemplateProvider(shots),
                NullLogger<SessionEngine>.Instance, time),
            time);
    }

    private static CapturedPhoto Photo(int n) =>
        new($@"C:\watch\IMG_{n:0000}.JPG", $"IMG_{n:0000}.JPG", 250_000, DateTimeOffset.UtcNow);

    private static string[] Names(SessionSnapshot s) => [.. s.Photos.Select(p => p.FileName)];

    /// <summary>A finished session waiting in review, ready to have a shot redone.</summary>
    private static (SessionEngine Engine, FakeTimeProvider Time) Reviewing(int shots = Shots)
    {
        var (engine, time) = Build(shots);
        engine.Arm();
        for (var i = 1; i <= shots; i++)
        {
            time.Advance(TimeSpan.FromSeconds(Countdown));
            engine.SubmitPhoto(Photo(i));
        }

        Assert.Equal(SessionState.ReviewShots, engine.Snapshot.State);
        return (engine, time);
    }

    /// <summary>Take the replacement shot that a retake is waiting for.</summary>
    private static void Reshoot(SessionEngine engine, FakeTimeProvider time, int n)
    {
        time.Advance(TimeSpan.FromSeconds(Countdown));
        engine.SubmitPhoto(Photo(n));
    }

    // --- the point of the feature -------------------------------------------

    [Fact]
    public void Retaking_the_middle_of_a_strip_leaves_the_others_alone()
    {
        var (engine, time) = Reviewing();

        var result = engine.Retake(1);          // shot 2 of 4
        Assert.True(result.Ok);
        Assert.Null(result.Error);

        // Only that one is gone; the rest keep their order.
        Assert.Equal(
            ["IMG_0001.JPG", "IMG_0003.JPG", "IMG_0004.JPG"],
            Names(engine.Snapshot));

        Reshoot(engine, time, 99);

        Assert.Equal(
            ["IMG_0001.JPG", "IMG_0099.JPG", "IMG_0003.JPG", "IMG_0004.JPG"],
            Names(engine.Snapshot));
        Assert.Equal(SessionState.ReviewShots, engine.Snapshot.State);
    }

    /// <summary>
    /// The re-indexing, isolated. _order holds indices into _photos, so removing
    /// anything but the newest capture leaves every higher index pointing one
    /// photo too far along.
    /// </summary>
    [Fact]
    public void Retaking_the_first_shot_does_not_shuffle_the_rest()
    {
        var (engine, time) = Reviewing();

        engine.Retake(0);
        Assert.Equal(["IMG_0002.JPG", "IMG_0003.JPG", "IMG_0004.JPG"], Names(engine.Snapshot));

        Reshoot(engine, time, 99);

        Assert.Equal(
            ["IMG_0099.JPG", "IMG_0002.JPG", "IMG_0003.JPG", "IMG_0004.JPG"],
            Names(engine.Snapshot));
    }

    [Fact]
    public void Every_slot_can_be_retaken_and_lands_back_in_place()
    {
        for (var slot = 0; slot < Shots; slot++)
        {
            var (engine, time) = Reviewing();
            engine.Retake(slot);
            Reshoot(engine, time, 99);

            var expected = Enumerable.Range(1, Shots)
                .Select(n => $"IMG_{n:0000}.JPG")
                .ToArray();
            expected[slot] = "IMG_0099.JPG";

            Assert.Equal(expected, Names(engine.Snapshot));
        }
    }

    [Fact]
    public void Two_shots_can_be_retaken_one_after_the_other()
    {
        var (engine, time) = Reviewing();

        engine.Retake(1);
        Reshoot(engine, time, 98);

        engine.Retake(3);
        Reshoot(engine, time, 99);

        Assert.Equal(
            ["IMG_0001.JPG", "IMG_0098.JPG", "IMG_0003.JPG", "IMG_0099.JPG"],
            Names(engine.Snapshot));
    }

    /// <summary>The same shot being bad twice is not a special case.</summary>
    [Fact]
    public void The_same_slot_can_be_retaken_twice()
    {
        var (engine, time) = Reviewing();

        engine.Retake(2);
        Reshoot(engine, time, 98);
        engine.Retake(2);
        Reshoot(engine, time, 99);

        Assert.Equal(
            ["IMG_0001.JPG", "IMG_0002.JPG", "IMG_0099.JPG", "IMG_0004.JPG"],
            Names(engine.Snapshot));
    }

    // --- how it talks to the screens ----------------------------------------

    /// <summary>
    /// Both screens announce the pose being redone. Without this a retake of
    /// photo two is announced as "photo 4 of 4", which is worse than silence.
    /// </summary>
    [Fact]
    public void The_snapshot_names_the_pose_being_retaken()
    {
        var (engine, time) = Reviewing();

        engine.Retake(1);

        var during = engine.Snapshot;
        Assert.Equal(1, during.RetakingSlot);
        Assert.Equal(2, during.CurrentShot);

        Reshoot(engine, time, 99);

        Assert.Null(engine.Snapshot.RetakingSlot);
    }

    // --- what must be refused ------------------------------------------------

    [Theory]
    [InlineData(-1)]
    [InlineData(4)]
    [InlineData(99)]
    public void A_slot_that_is_not_there_is_refused(int slot)
    {
        var (engine, _) = Reviewing();

        var result = engine.Retake(slot);

        Assert.False(result.Ok);
        Assert.NotNull(result.Error);
        Assert.Equal(SessionState.ReviewShots, engine.Snapshot.State);
        Assert.Equal(Shots, engine.Snapshot.Photos.Count);
    }

    [Fact]
    public void Retaking_is_refused_when_no_session_is_running()
    {
        var (engine, _) = Build();

        var result = engine.Retake(0);

        Assert.False(result.Ok);
        Assert.Equal(SessionState.Idle, result.Snapshot.State);
    }

    [Fact]
    public void Retaking_is_refused_once_the_session_is_finished()
    {
        var (engine, _) = Reviewing();
        engine.Accept();
        engine.CompleteComposing("/strip.jpg", "folder");

        var result = engine.Retake(0);

        Assert.False(result.Ok);
        Assert.Equal(SessionState.Done, result.Snapshot.State);
    }

    // --- the old behaviour still holds --------------------------------------

    /// <summary>
    /// Retake still means the shot taken most recently, not whatever is last on
    /// screen -- the guarantee the reordering work depends on.
    /// </summary>
    [Fact]
    public void RetakeLast_still_drops_the_newest_capture_after_a_reorder()
    {
        var (engine, time) = Reviewing();
        engine.Reorder([3, 0, 1, 2]);           // shot 4 dragged to the front

        engine.RetakeLast();

        Assert.DoesNotContain("IMG_0004.JPG", Names(engine.Snapshot));

        Reshoot(engine, time, 99);
        Assert.Equal(
            ["IMG_0099.JPG", "IMG_0001.JPG", "IMG_0002.JPG", "IMG_0003.JPG"],
            Names(engine.Snapshot));
    }

    /// <summary>Retaking by slot has to survive the strip being rearranged too.</summary>
    [Fact]
    public void Retaking_a_slot_works_on_a_reordered_strip()
    {
        var (engine, time) = Reviewing();
        engine.Reorder([3, 0, 1, 2]);           // showing 4, 1, 2, 3

        engine.Retake(2);                       // which is shot 2
        Assert.Equal(
            ["IMG_0004.JPG", "IMG_0001.JPG", "IMG_0003.JPG"],
            Names(engine.Snapshot));

        Reshoot(engine, time, 99);

        Assert.Equal(
            ["IMG_0004.JPG", "IMG_0001.JPG", "IMG_0099.JPG", "IMG_0003.JPG"],
            Names(engine.Snapshot));
    }

    [Fact]
    public void A_retaken_session_still_composites_what_is_on_screen()
    {
        var (engine, time) = Reviewing();
        engine.Retake(0);
        Reshoot(engine, time, 99);

        var composing = engine.Accept();

        Assert.Equal(SessionState.Composing, composing.State);
        Assert.Equal("IMG_0099.JPG", composing.Photos[0].FileName);
        Assert.Equal(Shots, composing.Photos.Distinct().Count());
    }
}
