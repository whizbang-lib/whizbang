// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using Npgsql;
using NpgsqlTypes;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Archival;
using Whizbang.Core.Messaging;
using Whizbang.Core.Perspectives;
using Whizbang.Core.ValueObjects;
using Whizbang.Data.Postgres;

namespace Whizbang.Data.EFCore.Postgres.Tests;

/// <summary>
/// Restore requests and the policy table (#1738, WS-3 S9, migration 271). An AutoRestore background write to an archived
/// stream is dead-lettered (<see cref="MessageFailureReason.AwaitingRestore"/>) and queues the stream's restore request in
/// the same transaction. The request moves Pending, Restoring, then Restored or Failed, by deterministic state only:
/// <see cref="ArchiveRestoreRequestStep"/> claims it and restores the stream through <see cref="IArchiveStore"/>; any
/// restore of the stream completes it and makes its dead letters due for the recovery worker; a restore that does not
/// put the stream back fails it, and its dead letters are held for review.
/// </summary>
/// <docs>fundamentals/events/archive-store#restore-requests</docs>
/// <code-under-test>src/Whizbang.Data.Postgres/Migrations/271_ArchiveReadThroughAndAppendPolicy.sql</code-under-test>
/// <code-under-test>src/Whizbang.Data.Postgres/ArchiveRestoreRequestStep.cs</code-under-test>
[Category("Integration")]
[Category("Shard3")]
public class ArchiveRestoreRequestSqlTests : EFCoreTestBase {
  private const string EVENT_TYPE = "Probe.Restore.Happened, Probe.Restore";
  private const string STREAM_TYPE = "Probe.Restore.Happened";
  private const int AWAITING_RESTORE_REASON = 27;

  private static readonly DateTimeOffset _t0 = new(2026, 10, 30, 11, 0, 0, TimeSpan.Zero);

  // ===== The queue =====

  [Test]
  public async Task Queue_ARestoredRequestStartsOver_AFailedOneStaysFailedAsync() {
    await using var conn = await _openAsync();
    var restored = await _requestAsync(conn, "Restored", _t0);
    var failed = await _requestAsync(conn, "Failed", _t0);
    var pending = await _requestAsync(conn, "Pending", _t0);
    var restoring = await _requestAsync(conn, "Restoring", _t0);
    var fresh = await _archivedStreamAsync(conn);

    await _executeAsync(conn, "SELECT _wh_queue_archive_restores(@ids, @now)",
      ("ids", new[] { restored, failed, pending, restoring, fresh, fresh }), ("now", _t0.AddHours(1)));

    await Assert.That(await _requestAsync(conn, restored)).IsEqualTo("Pending|" + _iso(_t0.AddHours(1)))
      .Because("the stream was archived again after its last restore, so it needs another");
    await Assert.That(await _requestAsync(conn, failed)).IsEqualTo("Failed|" + _iso(_t0))
      .Because("a failed restore waits for a person; queuing it again would retry what failed without anyone looking");
    await Assert.That(await _requestAsync(conn, pending)).IsEqualTo("Pending|" + _iso(_t0))
      .Because("an open request keeps its place in the queue");
    await Assert.That(await _requestAsync(conn, restoring)).IsEqualTo("Restoring|" + _iso(_t0));
    await Assert.That(await _requestAsync(conn, fresh)).IsEqualTo("Pending|" + _iso(_t0.AddHours(1)))
      .Because("a stream named twice in one write gets one request");
  }

  // ===== The maintenance step's claim and failure =====

  [Test]
  public async Task Claim_TakesTheOldestOpenRequests_AndALeftoverRestoringOneAsync() {
    await using var conn = await _openAsync();
    var leftover = await _requestAsync(conn, "Restoring", _t0);
    var oldest = await _requestAsync(conn, "Pending", _t0.AddMinutes(1));
    var newest = await _requestAsync(conn, "Pending", _t0.AddMinutes(2));
    var failed = await _requestAsync(conn, "Failed", _t0.AddMinutes(-1));
    var done = await _requestAsync(conn, "Restored", _t0.AddMinutes(-1));

    var claimed = await _claimAsync(conn, limit: 2, now: _t0.AddHours(1));

    await Assert.That(claimed).IsEquivalentTo([leftover, oldest])
      .Because("oldest first, and a Restoring request a pass finds was left by a pass that stopped: a restore is idempotent");
    await Assert.That(await _requestAsync(conn, oldest)).IsEqualTo("Restoring|" + _iso(_t0.AddMinutes(1)));
    await Assert.That(await _scalarAsync(conn, "SELECT attempts FROM wh_archive_restore_requests WHERE stream_id = @s", ("s", leftover)))
      .IsEqualTo(1L);
    await Assert.That(await _requestAsync(conn, newest)).IsEqualTo("Pending|" + _iso(_t0.AddMinutes(2)));
    await Assert.That(await _claimAsync(conn, limit: 10, now: _t0.AddHours(1))).IsEquivalentTo([leftover, oldest, newest])
      .Because("a Failed or Restored request is never claimed");
    await Assert.That(await _requestAsync(conn, failed)).StartsWith("Failed|");
    await Assert.That(await _requestAsync(conn, done)).StartsWith("Restored|");
  }

  [Test]
  public async Task Fail_MarksTheRequestFailed_AndLeavesItsDeadLettersHeldForReviewAsync() {
    await using var conn = await _openAsync();
    var stream = await _autoRestoreStreamWithADeadLetterAsync(conn);
    _ = await _claimAsync(conn, limit: 10, now: _t0.AddHours(1));

    var failed = await _failAsync(conn, stream, "version_conflict");
    var again = await _failAsync(conn, stream, "something else");

    await Assert.That(failed).IsTrue();
    await Assert.That(again).IsFalse().Because("only an open request fails; the first failure's error is kept");
    await Assert.That(await _requestErrorAsync(conn, stream)).IsEqualTo("Failed|version_conflict");
    await Assert.That(await _deadLetterStateAsync(conn, stream)).IsEqualTo("2|0|<none>")
      .Because("held for review (2), no longer held by the request (0), not due: a person decides what to do with it");
  }

  // ===== Completion: any restore of the stream =====

  [Test]
  public async Task Restore_MarksTheRequestRestored_AndMakesItsDeadLettersDueAsync() {
    await using var conn = await _openAsync();
    var stream = await _autoRestoreStreamWithADeadLetterAsync(conn);
    await Assert.That(await _deadLetterStateAsync(conn, stream)).IsEqualTo("2|2|<none>")
      .Because("before the restore the dead letter is held by its request");
    // A stream of a type with no policy, which refuses.
    var refusing = await _archivedStreamAsync(conn, "Probe.Restore.Other, Probe.Restore");
    _ = await _emitOutboxAsync(conn, _t0.AddMinutes(2), refusing);

    var result = await _store().RestoreAsync(stream);

    await Assert.That(result.Status).IsEqualTo(ArchiveStatus.RESTORED);
    await Assert.That(await _requestErrorAsync(conn, stream)).IsEqualTo("Restored|<none>");
    await Assert.That(await _deadLetterStateAsync(conn, stream)).IsEqualTo("0|0|due")
      .Because("Pending, not held, due now: the recovery worker's next scan redrives it");
    await Assert.That(await _scalarAsync(conn,
        "SELECT count(*) FROM wh_dead_letters WHERE stream_id = @s AND failure_reason = 26 AND recovery_status = 0 AND next_recovery_at IS NULL",
        ("s", refusing)))
      .IsEqualTo(1L).Because("the control: another stream's refused dead letter is untouched by this restore");
  }

  [Test]
  public async Task Restore_ASecondRestoreOfAStreamAlreadyBack_CompletesItsRequestAsync() {
    await using var conn = await _openAsync();
    var stream = await _autoRestoreStreamWithADeadLetterAsync(conn);
    _ = await _store().RestoreAsync(stream);
    // A step that claimed the request before an inline restore put the stream back.
    await _executeAsync(conn, "UPDATE wh_archive_restore_requests SET state = 'Restoring' WHERE stream_id = @s", ("s", stream));

    var second = await _store().RestoreAsync(stream);

    await Assert.That(second.Status).IsEqualTo(ArchiveStatus.NOTHING_ARCHIVED);
    await Assert.That(await _requestErrorAsync(conn, stream)).IsEqualTo("Restored|<none>")
      .Because("the stream is back, so the second restore ends the request the way the first did");
  }

  [Test]
  public async Task AwaitingRestore_AfterTheRestore_TheRecoveryServiceRedrivesTheEventIntoTheStreamAsync() {
    await using var ctx = CreateDbContext();
    await using var conn = await _openAsync();
    var stream = await _autoRestoreStreamWithADeadLetterAsync(conn);
    var recovery = new EFCoreDeadLetterRecoveryService<WorkCoordinationDbContext>(ctx);
    await Assert.That((await recovery.FetchDueAsync(100)).Where(e => e.StreamId == stream)).IsEmpty()
      .Because("held by its request, the dead letter is not offered before the stream is back");

    _ = await _store().RestoreAsync(stream);
    var due = (await recovery.FetchDueAsync(100)).Single(e => e.StreamId == stream);
    var recovered = await recovery.RecoverAsync(due.DeadLetterId);

    await Assert.That(due.FailureReason).IsEqualTo(MessageFailureReason.AwaitingRestore);
    await Assert.That(recovered).IsTrue();
    await Assert.That(await _hotVersionsAsync(conn, stream)).IsEquivalentTo([1L, 2L])
      .Because("the restored stream holds version 1 again, and the redriven event takes the next version");
  }

  [Test]
  public async Task Purge_RemovesTheStreamsRestoreRequest_WithItsLifecycleRowAsync() {
    await using var conn = await _openAsync();
    var purged = await _autoRestoreStreamWithADeadLetterAsync(conn);
    var kept = await _autoRestoreStreamWithADeadLetterAsync(conn);

    await _executeAsync(conn, "SELECT * FROM wh_purge_streams(ARRAY[@s]::uuid[], false, gen_random_uuid(), 0, 'test', 'purge')", ("s", purged));

    await Assert.That(await _scalarAsync(conn, "SELECT count(*) FROM wh_archive_restore_requests WHERE stream_id = @s", ("s", purged)))
      .IsEqualTo(0L).Because("a purged stream is never restored, so its request goes with its lifecycle row");
    await Assert.That(await _scalarAsync(conn, "SELECT count(*) FROM wh_archive_restore_requests WHERE stream_id = @s", ("s", kept)))
      .IsEqualTo(1L);
  }

  // ===== The policy table =====

  [Test]
  public async Task SyncPolicies_ReplacesTheStoredSetWhole_AndIsIdempotentAsync() {
    await using var conn = await _openAsync();
    await _syncAsync(conn, ["Shop.Order", "Shop.Cart"], ["AutoRestore", "Refuse"]);

    var unchanged = await _syncAsync(conn, ["Shop.Order", "Shop.Cart"], ["AutoRestore", "Refuse"]);
    var replaced = await _syncAsync(conn, ["Shop.Order", "Shop.Invoice"], ["Refuse", "AutoRestore"]);

    await Assert.That(unchanged).IsEqualTo(0).Because("the same set again writes nothing");
    await Assert.That(replaced).IsEqualTo(3).Because("Shop.Cart removed, Shop.Order changed, Shop.Invoice added");
    await Assert.That(await _policiesAsync(conn)).IsEquivalentTo(["Shop.Invoice|AutoRestore", "Shop.Order|Refuse"]);
    await Assert.That(await _syncAsync(conn, [], [])).IsEqualTo(2);
    await Assert.That(await _policiesAsync(conn)).IsEmpty().Because("a type no longer configured refuses again");
    await Assert.That(async () => await _syncAsync(conn, ["Shop.Order"], ["NewGeneration"])).Throws<PostgresException>()
      .Because("the table admits only the policies that ship; NewGeneration comes with #1734");
  }

  [Test]
  public async Task SyncPolicies_ThroughTheCoordinator_StoresTheConfiguredSetAsync() {
    await using var ctx = CreateDbContext();
    var coordinator = new EFCoreWorkCoordinator<WorkCoordinationDbContext>(ctx, Whizbang.Core.Serialization.JsonContextRegistry.CreateCombinedOptions());

    await ((IWorkCoordinator)coordinator).SyncStreamTypePoliciesAsync([
      new StreamTypePolicy("Shop.Order", AppendToArchivedPolicy.AutoRestore),
      new StreamTypePolicy("Shop.Cart", AppendToArchivedPolicy.Refuse),
    ]);

    await using var conn = await _openAsync();
    await Assert.That(await _policiesAsync(conn)).IsEquivalentTo(["Shop.Cart|Refuse", "Shop.Order|AutoRestore"]);
  }

  // ===== The maintenance step =====

  [Test]
  public async Task Step_RestoresEachQueuedStream_AndItsDeadLettersBecomeDueAsync() {
    await using var conn = await _openAsync();
    var first = await _autoRestoreStreamWithADeadLetterAsync(conn);
    var second = await _autoRestoreStreamWithADeadLetterAsync(conn);

    await _runStepAsync(_store());

    foreach (var stream in new[] { first, second }) {
      await Assert.That(await _requestErrorAsync(conn, stream)).IsEqualTo("Restored|<none>");
      await Assert.That(await _hotVersionsAsync(conn, stream)).IsEquivalentTo([1L]);
      await Assert.That(await _deadLetterStateAsync(conn, stream)).IsEqualTo("0|0|due");
    }
  }

  [Test]
  public async Task Step_AnArchiveThatIsGone_FailsTheRequest_WithTheStoresStatusAsync() {
    await using var conn = await _openAsync();
    var stream = await _autoRestoreStreamWithADeadLetterAsync(conn);
    _ = await _store().DeleteAsync(stream);

    await _runStepAsync(_store());

    await Assert.That(await _requestErrorAsync(conn, stream)).IsEqualTo("Failed|nothing_archived")
      .Because("the tombstone stays Archived with nothing to restore from; the error is the store's status, a state, not a message");
    await Assert.That(await _deadLetterStateAsync(conn, stream)).IsEqualTo("2|0|<none>");
  }

  [Test]
  public async Task Step_ARestoreThatThrows_FailsThatRequest_AndStillRestoresTheNextAsync() {
    await using var conn = await _openAsync();
    var throwing = await _autoRestoreStreamWithADeadLetterAsync(conn);
    var fine = await _autoRestoreStreamWithADeadLetterAsync(conn);
    var store = new ThrowingForOneStreamArchiveStore(_store(), throwing);

    await _runStepAsync(store);

    await Assert.That(await _requestErrorAsync(conn, throwing)).IsEqualTo("Failed|InvalidOperationException: the blob container is unreachable");
    await Assert.That(await _requestErrorAsync(conn, fine)).IsEqualTo("Restored|<none>")
      .Because("one stream's failure never holds back another's restore");
  }

  [Test]
  public async Task Step_CanceledDuringARestore_StopsTheCycle_AndLeavesTheRequestForTheNextOneAsync() {
    await using var conn = await _openAsync();
    var stream = await _autoRestoreStreamWithADeadLetterAsync(conn);
    using var stopping = new CancellationTokenSource();
    var store = new CancelingArchiveStore(stopping);

    await Assert.That(async () => await _runStepAsync(store, stopping.Token)).Throws<OperationCanceledException>()
      .Because("a host stopping mid-restore stops the cycle; it is not a restore that failed");

    await Assert.That(store.Restores).IsEqualTo(1);
    await Assert.That(await _requestErrorAsync(conn, stream)).IsEqualTo("Restoring|<none>")
      .Because("the request is not failed: a later pass claims a leftover Restoring request again");
  }

  [Test]
  public async Task Step_AsTheDriverRegistersIt_WritesTheRequestTimesFromTheHostsClockAsync() {
    await using var conn = await _openAsync();
    var stream = await _autoRestoreStreamWithADeadLetterAsync(conn);
    _ = await _store().DeleteAsync(stream);
    var hostNow = _t0.AddDays(3);
    var services = new ServiceCollection();
    services.AddDbContext<PostgresTestDbContext>(o => o.UseInMemoryDatabase("archive-restore-step-clock"));
    services.AddSingleton<TimeProvider>(new FakeTimeProvider(hostNow));
    _ = new WhizbangPerspectiveBuilder(services).WithEFCore<PostgresTestDbContext>().WithDriver.Postgres;
    await using var registered = services.BuildServiceProvider();
    using var scope = registered.CreateScope();
    var step = scope.ServiceProvider.GetServices<Whizbang.Core.Workers.IMaintenanceStep>().OfType<ArchiveRestoreRequestStep>().Single();

    await step.RunAsync(_runServices(_store()), CancellationToken.None);

    await Assert.That(await _requestErrorAsync(conn, stream)).IsEqualTo("Failed|nothing_archived");
    await Assert.That(await _scalarAsync(conn,
        "SELECT extract(epoch FROM updated_at)::bigint FROM wh_archive_restore_requests WHERE stream_id = @s", ("s", stream)))
      .IsEqualTo(hostNow.ToUnixTimeSeconds()).Because("the registration hands the step the host's clock, not the system one");
  }

  [Test]
  public async Task FromServices_WithAHostClockAndLogger_WritesTheRequestTimesFromThatClock_AndLogsTheOutcomeToThatLoggerAsync() {
    await using var conn = await _openAsync();
    var stream = await _autoRestoreStreamWithADeadLetterAsync(conn);
    _ = await _store().DeleteAsync(stream);
    var hostNow = _t0.AddDays(5);
    var logger = new CapturingStepLogger();
    var services = new ServiceCollection();
    services.AddSingleton<TimeProvider>(new FakeTimeProvider(hostNow));
    services.AddSingleton<Microsoft.Extensions.Logging.ILogger<ArchiveRestoreRequestStep>>(logger);
    await using var host = services.BuildServiceProvider();

    await ArchiveRestoreRequestStep.FromServices(host).RunAsync(_runServices(_store()), CancellationToken.None);

    await Assert.That(await _requestErrorAsync(conn, stream)).IsEqualTo("Failed|nothing_archived");
    await Assert.That(await _updatedAtAsync(conn, stream)).IsEqualTo(hostNow.ToUnixTimeSeconds())
      .Because("the host's clock, not the system one, times the request");
    await Assert.That(logger.Messages).Count().IsEqualTo(1);
    await Assert.That(logger.Messages[0]).Contains(stream.ToString()).And.Contains("nothing_archived")
      .Because("the failed restore is reported in the host's own log");
  }

  [Test]
  public async Task FromServices_WithNoClockOrLogger_RunsOnTheSystemClock_WithoutLoggingAsync() {
    await using var conn = await _openAsync();
    var stream = await _autoRestoreStreamWithADeadLetterAsync(conn);
    _ = await _store().DeleteAsync(stream);
    await using var bare = new ServiceCollection().BuildServiceProvider();
    var before = DateTimeOffset.UtcNow.ToUnixTimeSeconds();

    await ArchiveRestoreRequestStep.FromServices(bare).RunAsync(_runServices(_store()), CancellationToken.None);

    var after = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
    await Assert.That(await _requestErrorAsync(conn, stream)).IsEqualTo("Failed|nothing_archived")
      .Because("a host without logging still gets a step that runs; its outcome is discarded, not a failure");
    var updatedAt = await _updatedAtAsync(conn, stream);
    await Assert.That(updatedAt).IsGreaterThanOrEqualTo(before)
      .Because("with no clock registered the step reads the system clock, which lies between the two reads around the run");
    await Assert.That(updatedAt).IsLessThanOrEqualTo(after);
  }

  [Test]
  public async Task FromServices_RefusesNoServicesAsync() {
    await Assert.That(() => ArchiveRestoreRequestStep.FromServices(null!)).Throws<ArgumentNullException>();
  }

  // =================================================================================================

  // Records each message the step logs.
  private sealed class CapturingStepLogger : Microsoft.Extensions.Logging.ILogger<ArchiveRestoreRequestStep> {
    public List<string> Messages { get; } = [];
    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
    public bool IsEnabled(Microsoft.Extensions.Logging.LogLevel logLevel) => true;
    public void Log<TState>(Microsoft.Extensions.Logging.LogLevel logLevel, Microsoft.Extensions.Logging.EventId eventId, TState state,
        Exception? exception, Func<TState, Exception?, string> formatter) => Messages.Add(formatter(state, exception));
  }

  private static async Task<long> _updatedAtAsync(NpgsqlConnection conn, Guid stream) =>
    await _scalarAsync(conn, "SELECT floor(extract(epoch FROM updated_at))::bigint FROM wh_archive_restore_requests WHERE stream_id = @s", ("s", stream));

  // Cancels the cycle's token while a restore is running, as a host stopping mid-restore does, and observes it.
  private sealed class CancelingArchiveStore(CancellationTokenSource stopping) : IArchiveStore {
    public int Restores { get; private set; }
    public Task<ArchiveWriteResult> WriteAsync(ArchiveBatch batch, CancellationToken cancellationToken = default) =>
      throw new NotSupportedException();
    public IAsyncEnumerable<ArchivedEvent> ReadAsync(Guid streamId, long fromVersion = 0, CancellationToken cancellationToken = default) =>
      throw new NotSupportedException();
    public async Task<ArchiveRestoreResult> RestoreAsync(Guid streamId, CancellationToken cancellationToken = default) {
      Restores++;
      await stopping.CancelAsync();
      cancellationToken.ThrowIfCancellationRequested();
      return new ArchiveRestoreResult(ArchiveStatus.RESTORED, 0, 0);
    }
    public Task<long> DeleteAsync(Guid streamId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
  }

  private sealed class ThrowingForOneStreamArchiveStore(IArchiveStore inner, Guid failing) : IArchiveStore {
    public Task<ArchiveWriteResult> WriteAsync(ArchiveBatch batch, CancellationToken cancellationToken = default) =>
      inner.WriteAsync(batch, cancellationToken);
    public IAsyncEnumerable<ArchivedEvent> ReadAsync(Guid streamId, long fromVersion = 0, CancellationToken cancellationToken = default) =>
      inner.ReadAsync(streamId, fromVersion, cancellationToken);
    public Task<ArchiveRestoreResult> RestoreAsync(Guid streamId, CancellationToken cancellationToken = default) =>
      streamId == failing
        ? throw new InvalidOperationException("the blob container is unreachable")
        : inner.RestoreAsync(streamId, cancellationToken);
    public Task<long> DeleteAsync(Guid streamId, CancellationToken cancellationToken = default) =>
      inner.DeleteAsync(streamId, cancellationToken);
  }

  private PostgresTableArchiveStore _store() => new(NpgsqlDataSource.Create(ConnectionString).OpenConnectionAsync);

  // One maintenance cycle of the step, handed the connection and the store as the maintenance worker's scope hands them.
  private async Task _runStepAsync(IArchiveStore store, CancellationToken cancellationToken = default) {
    await using var provider = _runServices(store);
    var step = new ArchiveRestoreRequestStep(new FakeTimeProvider(_t0.AddHours(2)), NullLogger<ArchiveRestoreRequestStep>.Instance);
    await step.RunAsync(provider, cancellationToken);
  }

  // The services a maintenance cycle's scope hands a step: the archive connection and the store.
  private ServiceProvider _runServices(IArchiveStore store) {
    var services = new ServiceCollection();
    services.AddSingleton(new PostgresArchiveConnection(NpgsqlDataSource.Create(ConnectionString).OpenConnectionAsync, Schema: null));
    services.AddSingleton(store);
    return services.BuildServiceProvider();
  }

  private async Task<NpgsqlConnection> _openAsync() {
    var conn = new NpgsqlConnection(ConnectionString);
    await conn.OpenAsync();
    return conn;
  }

  private static string _iso(DateTimeOffset at) => at.ToUnixTimeSeconds().ToString(System.Globalization.CultureInfo.InvariantCulture);

  // A stream with one event, archived whole. Its type is the type of that first event.
  private async Task<Guid> _archivedStreamAsync(NpgsqlConnection conn, string eventType = EVENT_TYPE) {
    var stream = (Guid)TrackedGuid.New();
    _ = await _emitOutboxAsync(conn, _t0, stream, [(Guid)TrackedGuid.New()], eventType);
    var result = await _store().WriteAsync(ArchiveBatch.WholeStream(stream));
    await Assert.That(result.Status).IsEqualTo(ArchiveStatus.ARCHIVED);
    return stream;
  }

  // An archived stream with a request row in the given state, written directly.
  private async Task<Guid> _requestAsync(NpgsqlConnection conn, string state, DateTimeOffset requestedAt) {
    var stream = await _archivedStreamAsync(conn);
    await _executeAsync(conn,
      "INSERT INTO wh_archive_restore_requests (stream_id, state, requested_at, updated_at) VALUES (@s, @st, @at, @at)",
      ("s", stream), ("st", state), ("at", requestedAt));
    return stream;
  }

  // A stream whose type restores on append, archived whole, with one background append dead-lettered awaiting it.
  private async Task<Guid> _autoRestoreStreamWithADeadLetterAsync(NpgsqlConnection conn) {
    await _syncAsync(conn, [STREAM_TYPE], ["AutoRestore"]);
    var stream = await _archivedStreamAsync(conn);
    _ = await _emitOutboxAsync(conn, _t0.AddMinutes(1), stream);
    await Assert.That(await _requestErrorAsync(conn, stream)).IsEqualTo("Pending|<none>");
    return stream;
  }

  private static async Task<string> _requestAsync(NpgsqlConnection conn, Guid stream) {
    await using var cmd = conn.CreateCommand();
    cmd.CommandText = "SELECT state || '|' || extract(epoch FROM requested_at)::bigint FROM wh_archive_restore_requests WHERE stream_id = @s";
    cmd.Parameters.AddWithValue("s", stream);
    return (string)(await cmd.ExecuteScalarAsync())!;
  }

  private static async Task<string> _requestErrorAsync(NpgsqlConnection conn, Guid stream) {
    await using var cmd = conn.CreateCommand();
    cmd.CommandText = "SELECT state || '|' || COALESCE(error, '<none>') FROM wh_archive_restore_requests WHERE stream_id = @s";
    cmd.Parameters.AddWithValue("s", stream);
    return (string)(await cmd.ExecuteScalarAsync())!;
  }

  // The stream's AwaitingRestore dead letter as recovery_status|operator_disposition|due-ness.
  private static async Task<string> _deadLetterStateAsync(NpgsqlConnection conn, Guid stream) {
    await using var cmd = conn.CreateCommand();
    cmd.CommandText =
      "SELECT recovery_status || '|' || operator_disposition || '|' || CASE WHEN next_recovery_at IS NULL THEN '<none>' " +
      "  WHEN next_recovery_at <= NOW() THEN 'due' ELSE 'later' END " +
      $"FROM wh_dead_letters WHERE stream_id = @s AND failure_reason = {AWAITING_RESTORE_REASON}";
    cmd.Parameters.AddWithValue("s", stream);
    return (string)(await cmd.ExecuteScalarAsync())!;
  }

  private static async Task<List<Guid>> _claimAsync(NpgsqlConnection conn, int limit, DateTimeOffset now) {
    await using var cmd = conn.CreateCommand();
    cmd.CommandText = "SELECT stream_id FROM wh_claim_archive_restore_requests(@limit, @now)";
    cmd.Parameters.AddWithValue(nameof(limit), limit);
    cmd.Parameters.AddWithValue(nameof(now), now);
    var claimed = new List<Guid>();
    await using var reader = await cmd.ExecuteReaderAsync();
    while (await reader.ReadAsync()) {
      claimed.Add(reader.GetGuid(0));
    }
    return claimed;
  }

  private static async Task<bool> _failAsync(NpgsqlConnection conn, Guid stream, string error) {
    await using var cmd = conn.CreateCommand();
    cmd.CommandText = "SELECT wh_fail_archive_restore_request(@s, @e, @now)";
    cmd.Parameters.AddWithValue("s", stream);
    cmd.Parameters.AddWithValue("e", error);
    cmd.Parameters.AddWithValue("now", _t0.AddHours(1));
    return (bool)(await cmd.ExecuteScalarAsync())!;
  }

  private static async Task<int> _syncAsync(NpgsqlConnection conn, string[] types, string[] policies) {
    await using var cmd = conn.CreateCommand();
    cmd.CommandText = "SELECT wh_sync_stream_type_policies(@t, @p)";
    cmd.Parameters.AddWithValue("t", NpgsqlDbType.Array | NpgsqlDbType.Text, types);
    cmd.Parameters.AddWithValue("p", NpgsqlDbType.Array | NpgsqlDbType.Text, policies);
    return (int)(await cmd.ExecuteScalarAsync())!;
  }

  private static async Task<List<string>> _policiesAsync(NpgsqlConnection conn) {
    await using var cmd = conn.CreateCommand();
    cmd.CommandText = "SELECT stream_type || '|' || append_to_archived FROM wh_stream_type_policies ORDER BY 1";
    var rows = new List<string>();
    await using var reader = await cmd.ExecuteReaderAsync();
    while (await reader.ReadAsync()) {
      rows.Add(reader.GetString(0));
    }
    return rows;
  }

  private static Task<Guid[]> _emitOutboxAsync(NpgsqlConnection conn, DateTimeOffset now, Guid stream) =>
    _emitOutboxAsync(conn, now, stream, [(Guid)TrackedGuid.New()]);

  // Queues one event per id on the stream, then runs the outbox emit chain over them, as store_outbox_messages does.
  private static async Task<Guid[]> _emitOutboxAsync(
      NpgsqlConnection conn, DateTimeOffset now, Guid stream, Guid[] ids, string eventType = EVENT_TYPE) {
    var instanceId = (Guid)TrackedGuid.New();
    foreach (var id in ids) {
      await _executeAsync(conn, """
        INSERT INTO wh_outbox
          (message_id, destination, message_type, envelope_type, event_data, metadata, scope,
           status, attempts, instance_id, lease_expiry, partition_number, stream_id, is_event, flags, created_at)
        VALUES
          (@id, 'restore-topic', @type, 'env', jsonb_build_object('p', jsonb_build_object('n', 1)),
           '{}'::jsonb, NULL, 1, 0, @inst, @lease, 0, @sid, true, 0, @now)
        """,
        ("id", id), ("type", eventType), ("inst", instanceId), ("lease", now.AddMinutes(5)), ("sid", stream), ("now", now));
    }
    await using var emit = conn.CreateCommand();
    emit.CommandText = "SELECT _emit_event_store_chain(@ids, @inst, @lease, @now, 10000)";
    emit.Parameters.AddWithValue(nameof(ids), NpgsqlDbType.Array | NpgsqlDbType.Uuid, ids);
    emit.Parameters.AddWithValue("inst", instanceId);
    emit.Parameters.AddWithValue("lease", now.AddMinutes(5));
    emit.Parameters.AddWithValue(nameof(now), now);
    _ = await emit.ExecuteScalarAsync();
    return ids;
  }

  private static async Task<List<long>> _hotVersionsAsync(NpgsqlConnection conn, Guid stream) {
    await using var cmd = conn.CreateCommand();
    cmd.CommandText = "SELECT version FROM wh_event_store WHERE stream_id = @s ORDER BY version";
    cmd.Parameters.AddWithValue("s", stream);
    var rows = new List<long>();
    await using var reader = await cmd.ExecuteReaderAsync();
    while (await reader.ReadAsync()) {
      rows.Add(reader.GetInt64(0));
    }
    return rows;
  }

  private static async Task<long> _scalarAsync(NpgsqlConnection conn, string sql, params (string Name, object Value)[] parameters) {
    await using var cmd = conn.CreateCommand();
    cmd.CommandText = sql;
    foreach (var (name, value) in parameters) {
      cmd.Parameters.AddWithValue(name, value);
    }
    return Convert.ToInt64(await cmd.ExecuteScalarAsync(), System.Globalization.CultureInfo.InvariantCulture);
  }

  private static async Task _executeAsync(NpgsqlConnection conn, string sql, params (string Name, object Value)[] parameters) {
    await using var cmd = conn.CreateCommand();
    cmd.CommandText = sql;
    foreach (var (name, value) in parameters) {
      cmd.Parameters.AddWithValue(name, value);
    }
    _ = await cmd.ExecuteNonQueryAsync();
  }
}
