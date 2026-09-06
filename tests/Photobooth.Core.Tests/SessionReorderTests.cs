using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using Photobooth.Core;

namespace Photobooth.Core.Tests;

file sealed class FakeTemplateProvider(int slots) : ITemplateProvider
{
    public StripTemplate Current { get; } = new(
        "fake",
        new TemplateCanvas(600, 1800),
        [.. Enumerable.Range(0, slots).Select(i => new TemplateSlot(0, i * 0.15, 1, 0.15))]);
}

/// <summary>
/// Rearranging which shot goes in which slot of the strip.
///
/// The property under test throughout is that the strip is a permutation of what
/// was captured: never short a photo, never holding one twice. A booth cannot
/// notice a duplicated pose until the strip is printed.
/// </summary>
public class SessionReorderTests
{
    private const int Shots = 6;
    private const int Countdown = 3;

    private static (SessionEngine Engine, FakeTimeProvider Time) Build(int shots = Shots)
    {
        var time = new FakeTimeProvider(new DateTimeOffset(2026, 9, 6, 12, 0, 0, TimeSpan.Zero));
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

    /// <summary>Run a whole session up to review, so there is something to reorder.</summary>
    private static SessionEngine Reviewing(int shots = Shots)
    {
        var (engine, time) = Build(shots);
        engine.Arm();
        for (var i = 1; i <= shots; i++)
        {
            time.Advance(TimeSpan.FromSeconds(Countdown));
            engine.SubmitPhoto(Photo(i));
        }

        Assert.Equal(SessionState.ReviewShots, engine.Snapshot.State);
        return engine;
    }

    private static string[] Names(SessionSnapshot s) => [.. s.Photos.Select(p => p.FileName)];

    [Fact]
    public void Shots_start_in_capture_order()
    {
        var snapshot = Reviewing().Snapshot;

        Assert.Equal(
            ["IMG_0001.JPG", "IMG_0002.JPG", "IMG_0003.JPG",
             "IMG_0004.JPG", "IMG_0005.JPG", "IMG_0006.JPG"],
            Names(snapshot));
        Assert.Equal([0, 1, 2, 3, 4, 5], snapshot.Order);
        Assert.False(snapshot.IsReordered);
    }

    [Fact]
    public void The_fourth_shot_can_be_dragged_to_the_front()
    {
        var engine = Reviewing();

        var result = engine.Reorder([3, 0, 1, 2, 4, 5]);

        Assert.True(result.Ok);
        Assert.Null(result.Error);
        Assert.Equal(
            ["IMG_0004.JPG", "IMG_0001.JPG", "IMG_0002.JPG",
             "IMG_0003.JPG", "IMG_0005.JPG", "IMG_0006.JPG"],
            Names(result.Snapshot));
    }

    /// <summary>
    /// The snapshot has to say which shot each slot now holds, or the console
    /// cannot label a thumbnail once it has moved.
    /// </summary>
    [Fact]
    public void The_snapshot_reports_where_each_shot_came_from()
    {
        var engine = Reviewing();

        var snapshot = engine.Reorder([3, 0, 1, 2, 4, 5]).Snapshot;

        Assert.Equal([3, 0, 1, 2, 4, 5], snapshot.Order);
        Assert.True(snapshot.IsReordered);
    }

    /// <summary>Reordering is relative to what the operator is looking at.</summary>
    [Fact]
    public void A_second_drag_applies_to_the_order_already_showing()
    {
        var engine = Reviewing();
        engine.Reorder([3, 0, 1, 2, 4, 5]);

        // Now showing 4,1,2,3,5,6. Move the last of those to the front.
        var snapshot = engine.Reorder([5, 0, 1, 2, 3, 4]).Snapshot;

        Assert.Equal(
            ["IMG_0006.JPG", "IMG_0004.JPG", "IMG_0001.JPG",
             "IMG_0002.JPG", "IMG_0003.JPG", "IMG_0005.JPG"],
            Names(snapshot));
    }

    [Fact]
    public void Reversing_the_whole_strip_works()
    {
        var engine = Reviewing();

        var snapshot = engine.Reorder([5, 4, 3, 2, 1, 0]).Snapshot;

        Assert.Equal(
            ["IMG_0006.JPG", "IMG_0005.JPG", "IMG_0004.JPG",
             "IMG_0003.JPG", "IMG_0002.JPG", "IMG_0001.JPG"],
            Names(snapshot));
    }

    [Fact]
    public void The_order_can_be_put_back()
    {
        var engine = Reviewing();
        engine.Reorder([5, 4, 3, 2, 1, 0]);

        var snapshot = engine.ResetOrder();

        Assert.Equal([0, 1, 2, 3, 4, 5], snapshot.Order);
        Assert.False(snapshot.IsReordered);
        Assert.Equal("IMG_0001.JPG", snapshot.Photos[0].FileName);
    }

    // --- what must be refused ------------------------------------------------

    /// <summary>
    /// The one that would actually reach a guest: a repeated position drops a
    /// photo and prints another twice.
    /// </summary>
    [Fact]
    public void A_repeated_position_is_refused()
    {
        var engine = Reviewing();

        var result = engine.Reorder([0, 0, 1, 2, 3, 4]);

        Assert.False(result.Ok);
        Assert.Contains("exactly once", result.Error);
        Assert.Equal(
            ["IMG_0001.JPG", "IMG_0002.JPG", "IMG_0003.JPG",
             "IMG_0004.JPG", "IMG_0005.JPG", "IMG_0006.JPG"],
            Names(engine.Snapshot));
    }

    [Theory]
    [InlineData(6)]
    [InlineData(-1)]
    public void A_position_outside_the_strip_is_refused(int rogue)
    {
        var engine = Reviewing();

        var result = engine.Reorder([rogue, 1, 2, 3, 4, 0]);

        Assert.False(result.Ok);
        Assert.False(engine.Snapshot.IsReordered);
    }

    [Fact]
    public void The_wrong_number_of_positions_is_refused()
    {
        var engine = Reviewing();

        var result = engine.Reorder([2, 0, 1]);

        Assert.False(result.Ok);
        Assert.Contains("Expected 6", result.Error);
        Assert.False(engine.Snapshot.IsReordered);
    }

    /// <summary>
    /// Not mid-session: the set is incomplete, and a shot still to come has no
    /// position to be given.
    /// </summary>
    [Fact]
    public void Reordering_is_refused_while_still_shooting()
    {
        var (engine, time) = Build();
        engine.Arm();
        time.Advance(TimeSpan.FromSeconds(Countdown));
        engine.SubmitPhoto(Photo(1));
        time.Advance(TimeSpan.FromSeconds(Countdown));
        engine.SubmitPhoto(Photo(2));

        var result = engine.Reorder([1, 0]);

        Assert.False(result.Ok);
        Assert.Contains("reviewing", result.Error);
    }

    /// <summary>
    /// And not once the strip is being built, which would compose from an order
    /// different to the one the operator approved.
    /// </summary>
    [Fact]
    public void Reordering_is_refused_once_composing_has_started()
    {
        var engine = Reviewing();
        engine.Accept();

        var result = engine.Reorder([3, 0, 1, 2, 4, 5]);

        Assert.False(result.Ok);
        Assert.Equal(SessionState.Composing, result.Snapshot.State);
    }

    // --- reordering and retaking together ------------------------------------

    /// <summary>
    /// The interesting collision. Retake still means the shot taken last, not
    /// whatever ended up last in the strip; otherwise dragging a photo to the end
    /// would quietly change which one Retake throws away.
    /// </summary>
    [Fact]
    public void Retake_still_drops_the_shot_taken_last_after_a_reorder()
    {
        var engine = Reviewing();
        engine.Reorder([5, 0, 1, 2, 3, 4]);   // shot 6 dragged to the front

        var snapshot = engine.RetakeLast();

        Assert.DoesNotContain("IMG_0006.JPG", Names(snapshot));
        Assert.Equal(Shots - 1, snapshot.CapturedCount);
    }

    /// <summary>
    /// And the replacement comes back into the slot its predecessor held, so a
    /// careful arrangement is not lost to one retake.
    /// </summary>
    [Fact]
    public void A_retaken_shot_returns_to_the_slot_it_came_out_of()
    {
        var (engine, time) = Build();
        engine.Arm();
        for (var i = 1; i <= Shots; i++)
        {
            time.Advance(TimeSpan.FromSeconds(Countdown));
            engine.SubmitPhoto(Photo(i));
        }

        engine.Reorder([5, 0, 1, 2, 3, 4]);   // shot 6 is now slot 1
        engine.RetakeLast();                  // which is the shot that gets retaken

        time.Advance(TimeSpan.FromSeconds(Countdown));
        engine.SubmitPhoto(Photo(99));

        var snapshot = engine.Snapshot;
        Assert.Equal(SessionState.ReviewShots, snapshot.State);
        Assert.Equal(
            ["IMG_0099.JPG", "IMG_0001.JPG", "IMG_0002.JPG",
             "IMG_0003.JPG", "IMG_0004.JPG", "IMG_0005.JPG"],
            Names(snapshot));
    }

    [Fact]
    public void A_new_session_starts_back_in_capture_order()
    {
        var engine = Reviewing();
        engine.Reorder([5, 4, 3, 2, 1, 0]);

        engine.Arm();

        Assert.Empty(engine.Snapshot.Photos);
        Assert.Empty(engine.Snapshot.Order);
        Assert.False(engine.Snapshot.IsReordered);
    }

    /// <summary>
    /// The strip is composed from the snapshot photo list, so this is the
    /// assertion that the reorder actually reaches the output rather than only
    /// the console.
    /// </summary>
    [Fact]
    public void The_accepted_order_is_the_one_composited()
    {
        var engine = Reviewing();
        engine.Reorder([3, 0, 1, 2, 4, 5]);

        var composing = engine.Accept();

        Assert.Equal(SessionState.Composing, composing.State);
        Assert.Equal("IMG_0004.JPG", composing.Photos[0].FileName);
        Assert.Equal(Shots, composing.Photos.Distinct().Count());
    }
}
