using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using Photobooth.Core;
using Photobooth.Delivery;

namespace Photobooth.Delivery.Tests;

/// <summary>
/// The delivery queue, against a fake publisher.
///
/// Two properties matter more than the rest, because both fail silently in a way
/// nobody notices until a guest is standing there: a session must never be lost
/// (the photos are on disk and the record says what happened to them), and one
/// broken session must never stop the others being published.
/// </summary>
public sealed class UploadQueueTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(), $"pb-queue-{Guid.NewGuid():N}");

    private readonly FakeTimeProvider _time =
        new(new DateTimeOffset(2026, 9, 8, 19, 0, 0, TimeSpan.Zero));

    private readonly SessionArchive _archive;

    public UploadQueueTests()
    {
        _archive = new SessionArchive(
            Options.Create(new ArchiveOptions { Folder = _root }),
            NullLogger<SessionArchive>.Instance);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* best effort */ }
    }

    private UploadQueue Queue(FakePublisher publisher, DriveOptions? options = null) =>
        new(_archive, publisher,
            Options.Create(options ?? new DriveOptions
            {
                Enabled = true,
                BaseBackoffSeconds = 10,
                MaxBackoffSeconds = 300,
                MaxAttempts = 3,
            }),
            NullLogger<UploadQueue>.Instance, _time);

    /// <summary>A session on disk, as ComposeAsync would have left it.</summary>
    private SessionRecord Archived(string token = "tok123456")
    {
        var photo = Path.Combine(_root, $"src-{token}.jpg");
        Directory.CreateDirectory(_root);
        File.WriteAllBytes(photo, [0xFF, 0xD8, 0xFF, 0xD9]);

        var strip = Path.Combine(_root, $"strip-{token}.jpg");
        File.WriteAllBytes(strip, [0xFF, 0xD8, 0xFF, 0xD9]);

        var template = new StripTemplate(
            "fake", new TemplateCanvas(600, 1800),
            [new TemplateSlot(0, 0, 1, 0.3), new TemplateSlot(0, 0.35, 1, 0.3)]);

        var captures = new[]
        {
            new CapturedPhoto(photo, "IMG_0001.JPG", 4, _time.GetUtcNow()),
            new CapturedPhoto(photo, "IMG_0002.JPG", 4, _time.GetUtcNow()),
        };

        // Distinct timestamps keep folder names unique within one test.
        var record = _archive.Save(token, template, captures, strip, _time.GetUtcNow());
        _time.Advance(TimeSpan.FromMinutes(1));
        return record;
    }

    private SessionRecord Reload(string folderName) =>
        _archive.All().Single(r => r.FolderName == folderName);

    // --- the happy path ------------------------------------------------------

    [Fact]
    public async Task A_queued_session_is_published_and_records_its_link()
    {
        var publisher = new FakePublisher();
        var queue = Queue(publisher);
        var record = queue.Enqueue(Archived());

        await queue.RunOnceAsync();

        var after = Reload(record.FolderName);
        Assert.Equal(UploadStates.Uploaded, after.UploadState);
        Assert.Equal($"drive-{record.FolderName}", after.DriveFolderId);
        Assert.Equal($"https://drive.example/drive-{record.FolderName}", after.DriveUrl);
        Assert.Null(after.UploadError);
    }

    [Fact]
    public async Task A_published_session_is_not_published_again()
    {
        var publisher = new FakePublisher();
        var queue = Queue(publisher);
        queue.Enqueue(Archived());

        await queue.RunOnceAsync();
        await queue.RunOnceAsync();
        await queue.RunOnceAsync();

        Assert.Single(publisher.Calls);
    }

    /// <summary>Two guests must not be able to reach each other's photos.</summary>
    [Fact]
    public async Task Each_session_gets_its_own_link()
    {
        var publisher = new FakePublisher();
        var queue = Queue(publisher);
        var first = queue.Enqueue(Archived("aaa111"));
        var second = queue.Enqueue(Archived("bbb222"));

        await queue.RunOnceAsync();

        var a = Reload(first.FolderName);
        var b = Reload(second.FolderName);
        Assert.Equal(UploadStates.Uploaded, a.UploadState);
        Assert.Equal(UploadStates.Uploaded, b.UploadState);
        Assert.NotEqual(a.DriveUrl, b.DriveUrl);
    }

    // --- switched off --------------------------------------------------------

    /// <summary>
    /// The field-test build ships with delivery off. It must upload nothing, and
    /// must not quietly build a backlog that publishes itself the day someone
    /// turns it on.
    /// </summary>
    [Fact]
    public async Task Nothing_is_uploaded_or_queued_while_delivery_is_off()
    {
        var publisher = new FakePublisher { Enabled = false };
        var queue = Queue(publisher);
        var record = queue.Enqueue(Archived());

        await queue.RunOnceAsync();

        Assert.Empty(publisher.Calls);
        Assert.Equal(UploadStates.NotAttempted, Reload(record.FolderName).UploadState);
    }

    // --- retrying ------------------------------------------------------------

    /// <summary>A venue's wifi drops and comes back; nobody should have to notice.</summary>
    [Fact]
    public async Task A_transient_failure_is_retried_after_a_wait()
    {
        var publisher = new FakePublisher()
            .Script(PublishResult.Fail(PublishFailure.Transient, "network down"));
        var queue = Queue(publisher);
        var record = queue.Enqueue(Archived());

        await queue.RunOnceAsync();

        var waiting = Reload(record.FolderName);
        Assert.Equal(UploadStates.Pending, waiting.UploadState);
        Assert.Equal(1, waiting.UploadAttempts);
        Assert.Equal("network down", waiting.UploadError);

        // Straight away is too soon: the backoff has to actually hold it back.
        await queue.RunOnceAsync();
        Assert.Single(publisher.Calls);

        _time.Advance(TimeSpan.FromSeconds(11));
        await queue.RunOnceAsync();

        Assert.Equal(2, publisher.Calls.Count);
        Assert.Equal(UploadStates.Uploaded, Reload(record.FolderName).UploadState);
    }

    [Fact]
    public async Task The_wait_grows_with_each_failure()
    {
        var publisher = new FakePublisher();
        publisher.Default = PublishResult.Fail(PublishFailure.Transient, "still down");
        var queue = Queue(publisher, new DriveOptions
        {
            Enabled = true, BaseBackoffSeconds = 10, MaxBackoffSeconds = 300, MaxAttempts = 9,
        });
        queue.Enqueue(Archived());

        await queue.RunOnceAsync();                        // attempt 1, wait 10s

        _time.Advance(TimeSpan.FromSeconds(11));
        await queue.RunOnceAsync();                        // attempt 2, wait 20s

        _time.Advance(TimeSpan.FromSeconds(11));           // not enough for 20s
        await queue.RunOnceAsync();
        Assert.Equal(2, publisher.Calls.Count);

        _time.Advance(TimeSpan.FromSeconds(10));           // now past it
        await queue.RunOnceAsync();
        Assert.Equal(3, publisher.Calls.Count);
    }

    [Fact]
    public async Task A_session_is_parked_after_too_many_attempts()
    {
        var publisher = new FakePublisher();
        publisher.Default = PublishResult.Fail(PublishFailure.Transient, "never works");
        var queue = Queue(publisher);   // MaxAttempts = 3
        var record = queue.Enqueue(Archived());

        for (var i = 0; i < 5; i++)
        {
            await queue.RunOnceAsync();
            _time.Advance(TimeSpan.FromMinutes(10));
        }

        var after = Reload(record.FolderName);
        Assert.Equal(UploadStates.Failed, after.UploadState);
        Assert.Equal(3, after.UploadAttempts);
        Assert.Equal(3, publisher.Calls.Count);
        Assert.Equal("never works", after.UploadError);
    }

    /// <summary>Retrying cannot mint a token or free up space, so it does not try.</summary>
    [Theory]
    [InlineData(PublishFailure.NeedsAuthorisation)]
    [InlineData(PublishFailure.QuotaExhausted)]
    [InlineData(PublishFailure.Permanent)]
    public async Task Failures_retrying_cannot_fix_are_not_retried(PublishFailure failure)
    {
        var publisher = new FakePublisher();
        publisher.Default = PublishResult.Fail(failure, "no point trying again");
        var queue = Queue(publisher);
        var record = queue.Enqueue(Archived());

        await queue.RunOnceAsync();
        _time.Advance(TimeSpan.FromHours(1));
        await queue.RunOnceAsync();

        Assert.Single(publisher.Calls);
        Assert.Equal(UploadStates.Failed, Reload(record.FolderName).UploadState);
    }

    /// <summary>A publisher that throws must be handled, not allowed to kill the pass.</summary>
    [Fact]
    public async Task A_publisher_that_throws_is_treated_as_a_transient_failure()
    {
        var publisher = new FakePublisher { Throws = new HttpRequestException("boom") };
        var queue = Queue(publisher);
        var record = queue.Enqueue(Archived());

        await queue.RunOnceAsync();

        var after = Reload(record.FolderName);
        Assert.Equal(UploadStates.Pending, after.UploadState);
        Assert.Contains("boom", after.UploadError);
    }

    /// <summary>
    /// The one that matters most at an event: one guest's session going wrong
    /// must not hold up everyone behind them.
    /// </summary>
    [Fact]
    public async Task One_broken_session_does_not_block_the_others()
    {
        var publisher = new FakePublisher();
        var queue = Queue(publisher);

        var broken = queue.Enqueue(Archived("bad111"));
        var fine = queue.Enqueue(Archived("good22"));

        // Fail whichever one the queue reaches first, succeed for the other.
        publisher.Script(PublishResult.Fail(PublishFailure.Permanent, "broken"));

        await queue.RunOnceAsync();

        var states = new[] { broken, fine }
            .Select(r => Reload(r.FolderName).UploadState)
            .ToArray();

        Assert.Contains(UploadStates.Failed, states);
        Assert.Contains(UploadStates.Uploaded, states);
    }

    /// <summary>
    /// A folder renamed or moved between being listed and being published. There
    /// is nowhere to write an outcome, since the record lives in the folder that
    /// vanished -- so the pass must survive it and pick the session up again if
    /// it reappears, rather than throwing and taking the whole pass down.
    /// </summary>
    [Fact]
    public async Task A_session_whose_folder_has_gone_is_skipped_and_recovers()
    {
        var publisher = new FakePublisher();
        var queue = Queue(publisher);
        var record = queue.Enqueue(Archived());

        var folder = _archive.FolderFor(record);
        var stash = folder + "-moved";
        Directory.Move(folder, stash);

        await queue.RunOnceAsync();
        Assert.Empty(publisher.Calls);

        Directory.Move(stash, folder);
        await queue.RunOnceAsync();

        Assert.Single(publisher.Calls);
        Assert.Equal(UploadStates.Uploaded, Reload(record.FolderName).UploadState);
    }

    /// <summary>And it must not take the sessions behind it down with it.</summary>
    [Fact]
    public async Task A_missing_folder_does_not_stop_the_rest_of_the_pass()
    {
        var publisher = new FakePublisher();
        var queue = Queue(publisher);
        var missing = queue.Enqueue(Archived("aaa111"));
        var fine = queue.Enqueue(Archived("good22"));

        Directory.Move(_archive.FolderFor(missing), _archive.FolderFor(missing) + "-moved");

        await queue.RunOnceAsync();

        Assert.Equal(UploadStates.Uploaded, Reload(fine.FolderName).UploadState);
    }

    // --- surviving a restart -------------------------------------------------

    /// <summary>
    /// The reason the queue lives in session.json. A brand-new queue, as if the
    /// app had just started, must find the work waiting on disk with no handover.
    /// </summary>
    [Fact]
    public async Task A_new_queue_picks_up_work_left_by_the_last_one()
    {
        var first = Queue(new FakePublisher());
        var record = first.Enqueue(Archived());

        var publisher = new FakePublisher();
        var restarted = Queue(publisher);
        await restarted.RunOnceAsync();

        Assert.Single(publisher.Calls);
        Assert.Equal(UploadStates.Uploaded, Reload(record.FolderName).UploadState);
    }

    /// <summary>
    /// The attempt count is on disk so a crash loop cannot retry forever, while
    /// the backoff is not, so a restart is a reason to try again straight away.
    /// </summary>
    [Fact]
    public async Task Attempts_survive_a_restart_but_the_backoff_does_not()
    {
        var publisher = new FakePublisher();
        publisher.Default = PublishResult.Fail(PublishFailure.Transient, "down");
        var first = Queue(publisher);
        var record = first.Enqueue(Archived());

        await first.RunOnceAsync();
        Assert.Equal(1, Reload(record.FolderName).UploadAttempts);

        var restarted = Queue(publisher);
        await restarted.RunOnceAsync();          // no waiting, despite the backoff

        Assert.Equal(2, publisher.Calls.Count);
        Assert.Equal(2, Reload(record.FolderName).UploadAttempts);
    }

    // --- doing it by hand ----------------------------------------------------

    [Fact]
    public async Task A_failed_session_can_be_republished()
    {
        var publisher = new FakePublisher();
        publisher.Default = PublishResult.Fail(PublishFailure.Permanent, "gave up");
        var queue = Queue(publisher);
        var record = queue.Enqueue(Archived());

        await queue.RunOnceAsync();
        Assert.Equal(UploadStates.Failed, Reload(record.FolderName).UploadState);

        publisher.Default = PublishResult.Success("folder-id", "https://drive.example/folder-id");
        var requeued = queue.Republish(record.FolderName);

        Assert.NotNull(requeued);
        Assert.Equal(UploadStates.Pending, requeued.UploadState);
        Assert.Equal(0, requeued.UploadAttempts);

        await queue.RunOnceAsync();
        Assert.Equal(UploadStates.Uploaded, Reload(record.FolderName).UploadState);
    }

    /// <summary>Sessions captured with delivery off are published on request.</summary>
    [Fact]
    public async Task A_session_captured_while_delivery_was_off_can_be_published_later()
    {
        var off = Queue(new FakePublisher { Enabled = false });
        var record = off.Enqueue(Archived());
        Assert.Equal(UploadStates.NotAttempted, Reload(record.FolderName).UploadState);

        var publisher = new FakePublisher();
        var on = Queue(publisher);
        on.Republish(record.FolderName);
        await on.RunOnceAsync();

        Assert.Equal(UploadStates.Uploaded, Reload(record.FolderName).UploadState);
    }

    [Fact]
    public void Republishing_something_that_does_not_exist_returns_null()
    {
        var queue = Queue(new FakePublisher());
        Assert.Null(queue.Republish("2026-01-01_0000_nope"));
    }

    // --- the link arrives before the upload finishes -------------------------

    /// <summary>
    /// A real session is around 25 MB and the raws are nearly all of it. The link
    /// is usable once the folder and strip exist, so it is written down and
    /// announced then -- otherwise the QR appears after the guest has walked off,
    /// which is the same as no QR at all.
    /// </summary>
    [Fact]
    public async Task The_link_is_recorded_as_soon_as_it_works_not_when_the_upload_ends()
    {
        var publisher = new FakePublisher();
        // Fails after announcing the link, so what is on disk can only have come
        // from the announcement rather than from a successful finish.
        publisher.Default = PublishResult.Fail(PublishFailure.Transient, "dropped mid-upload");

        var queue = Queue(publisher);
        var record = queue.Enqueue(Archived());

        await queue.RunOnceAsync();

        var after = Reload(record.FolderName);
        Assert.Equal(UploadStates.Pending, after.UploadState);
        Assert.NotNull(after.DriveUrl);
        Assert.Equal($"drive-{record.FolderName}", after.DriveFolderId);
    }

    /// <summary>The screens are told, or the guest display never shows the code.</summary>
    [Fact]
    public async Task The_screens_are_told_the_moment_the_link_works()
    {
        var publisher = new FakePublisher();
        publisher.Default = PublishResult.Fail(PublishFailure.Transient, "dropped mid-upload");

        var queue = Queue(publisher);
        var announced = new List<SessionRecord>();
        queue.Updated += (_, r) => announced.Add(r);

        queue.Enqueue(Archived());
        await queue.RunOnceAsync();

        Assert.Contains(announced, r => r.DriveUrl is not null
                                        && r.UploadState == UploadStates.Pending);
    }

    /// <summary>
    /// And the folder it earned must survive the failure, or the retry creates a
    /// second folder and the QR already handed out points at the abandoned one.
    /// </summary>
    [Fact]
    public async Task A_retry_resumes_into_the_folder_the_link_already_points_at()
    {
        var publisher = new FakePublisher();
        publisher.Default = PublishResult.Fail(PublishFailure.Transient, "dropped mid-upload");

        var queue = Queue(publisher);
        var record = queue.Enqueue(Archived());

        await queue.RunOnceAsync();
        var afterFailure = Reload(record.FolderName);
        var link = afterFailure.DriveUrl;

        publisher.Default = PublishResult.Success("folder-id", "unused");
        _time.Advance(TimeSpan.FromSeconds(30));
        await queue.RunOnceAsync();

        var done = Reload(record.FolderName);
        Assert.Equal(UploadStates.Uploaded, done.UploadState);
        Assert.Equal(afterFailure.DriveFolderId, done.DriveFolderId);
        Assert.Equal(link, done.DriveUrl);
    }

    /// <summary>The same, for a session that gives up rather than retrying.</summary>
    [Fact]
    public async Task A_parked_session_keeps_the_link_it_had_handed_out()
    {
        var publisher = new FakePublisher();
        publisher.Default = PublishResult.Fail(PublishFailure.Permanent, "gave up");

        var queue = Queue(publisher);
        var record = queue.Enqueue(Archived());

        await queue.RunOnceAsync();

        var after = Reload(record.FolderName);
        Assert.Equal(UploadStates.Failed, after.UploadState);
        Assert.NotNull(after.DriveFolderId);
    }

    // --- the queue starts work at once ---------------------------------------

    /// <summary>
    /// The one that was costing a guest fourteen seconds. Enqueue used to write
    /// Pending to disk and nothing else, leaving the loop asleep in its idle
    /// delay -- so the booth composed a strip and then did nothing at all until
    /// the poll came round. The QR took 17s to appear, of which about 4s was
    /// actual work.
    ///
    /// Driven through the real hosted-service loop rather than RunOnceAsync, so
    /// it is the loop's own waiting that is under test.
    /// </summary>
    [Fact]
    public async Task A_finished_session_is_picked_up_without_waiting_for_the_poll()
    {
        var publisher = new FakePublisher();
        var queue = Queue(publisher, new DriveOptions
        {
            Enabled = true,
            // Far longer than the test will wait: if the wake-up does not work,
            // this cannot pass by the poll coming round instead.
            IdlePollSeconds = 600,
            BaseBackoffSeconds = 10,
            MaxBackoffSeconds = 300,
            MaxAttempts = 3,
        });

        await queue.StartAsync(CancellationToken.None);
        try
        {
            var record = queue.Enqueue(Archived());

            var published = await WaitFor(
                () => Reload(record.FolderName).UploadState == UploadStates.Uploaded);

            Assert.True(published, "the session was still waiting for the idle poll");
        }
        finally
        {
            await queue.StopAsync(CancellationToken.None);
        }
    }

    /// <summary>
    /// And every session after the first, which is where this went wrong the
    /// first time it was fixed: racing the wake-up against the idle delay with
    /// WhenAny left the loser running, so each expired poll parked an abandoned
    /// waiter on the semaphore that swallowed the next wake-up. Guest one was
    /// instant and everybody after them waited fifteen seconds.
    /// </summary>
    [Fact]
    public async Task Every_session_after_the_first_is_picked_up_at_once_too()
    {
        var publisher = new FakePublisher();
        var queue = Queue(publisher, new DriveOptions
        {
            Enabled = true, IdlePollSeconds = 600,
            BaseBackoffSeconds = 10, MaxBackoffSeconds = 300, MaxAttempts = 3,
        });

        await queue.StartAsync(CancellationToken.None);
        try
        {
            var first = queue.Enqueue(Archived("aaa111"));
            Assert.True(
                await WaitFor(() => Reload(first.FolderName).UploadState == UploadStates.Uploaded),
                $"the first session never published: enqueued as {first.UploadState}, "
                + $"now {Reload(first.FolderName).UploadState}, "
                + $"publisher saw {publisher.Calls.Count} call(s)");

            // Let the idle poll actually expire. That is the precondition for the
            // bug: an expired poll is what leaves an abandoned waiter behind, and
            // a test where the poll never fires cannot see it.
            _time.Advance(TimeSpan.FromSeconds(601));
            await Task.Delay(150);

            var second = queue.Enqueue(Archived("bbb222"));
            Assert.True(
                await WaitFor(() => Reload(second.FolderName).UploadState == UploadStates.Uploaded),
                "the second session was still waiting for the idle poll");
        }
        finally
        {
            await queue.StopAsync(CancellationToken.None);
        }
    }

    /// <summary>Re-publishing by hand should not wait for the poll either.</summary>
    [Fact]
    public async Task Republishing_is_picked_up_without_waiting_for_the_poll()
    {
        var publisher = new FakePublisher();
        var queue = Queue(publisher, new DriveOptions
        {
            Enabled = true, IdlePollSeconds = 600,
            BaseBackoffSeconds = 10, MaxBackoffSeconds = 300, MaxAttempts = 3,
        });

        // Archived while delivery was off, so nothing is pending to begin with.
        var off = Queue(new FakePublisher { Enabled = false });
        var record = off.Enqueue(Archived());

        await queue.StartAsync(CancellationToken.None);
        try
        {
            queue.Republish(record.FolderName);

            var published = await WaitFor(
                () => Reload(record.FolderName).UploadState == UploadStates.Uploaded);

            Assert.True(published, "the re-publish was still waiting for the idle poll");
        }
        finally
        {
            await queue.StopAsync(CancellationToken.None);
        }
    }

    /// <summary>Real time, because the loop being tested does its own waiting.</summary>
    private static async Task<bool> WaitFor(Func<bool> done, int timeoutMs = 5000)
    {
        var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
        while (DateTime.UtcNow < deadline)
        {
            if (done())
            {
                return true;
            }

            await Task.Delay(25);
        }

        return false;
    }

    // --- what the console shows ---------------------------------------------

    [Fact]
    public async Task Status_counts_what_is_waiting_and_what_gave_up()
    {
        var publisher = new FakePublisher();
        var queue = Queue(publisher);
        queue.Enqueue(Archived("aaa111"));
        var doomed = queue.Enqueue(Archived("bbb222"));

        publisher.Default = PublishResult.Fail(PublishFailure.Permanent, "no");
        await queue.RunOnceAsync();

        var status = queue.Status();
        Assert.True(status.Enabled);
        Assert.Equal(2, status.Failed);
        Assert.Equal(0, status.Pending);
        Assert.Equal("no", status.LastError);
        Assert.Null(status.LastSuccessUtc);
        Assert.NotNull(doomed);
    }

    [Fact]
    public async Task Status_clears_the_error_once_something_succeeds()
    {
        var publisher = new FakePublisher()
            .Script(PublishResult.Fail(PublishFailure.Transient, "blip"));
        var queue = Queue(publisher);
        queue.Enqueue(Archived());

        await queue.RunOnceAsync();
        Assert.Equal("blip", queue.Status().LastError);

        _time.Advance(TimeSpan.FromSeconds(30));
        await queue.RunOnceAsync();

        var status = queue.Status();
        Assert.Null(status.LastError);
        Assert.NotNull(status.LastSuccessUtc);
    }

    [Fact]
    public void Status_reports_when_nobody_has_signed_in()
    {
        var queue = Queue(new FakePublisher { Authorised = false });
        Assert.False(queue.Status().Authorised);
    }
}
