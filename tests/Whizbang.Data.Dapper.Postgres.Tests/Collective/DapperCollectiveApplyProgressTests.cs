// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

#pragma warning disable CA1859, CA1861 // handlers return the spec interface as a generated invoker expects; each test states its expected rows inline

using System.Linq.Expressions;
using Dapper;
using Microsoft.Extensions.DependencyInjection;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Messaging;
using Whizbang.Core.Perspectives;
using Whizbang.Data.Dapper.Postgres.Collective;
using Whizbang.Data.Postgres.Collective;

namespace Whizbang.Data.Dapper.Postgres.Tests.Collective;

/// <summary>
/// A collective applied in batches resumes after its last committed batch (#1969): every retry path (a busy apply lock, a
/// failure between batches, a re-run of a finished apply, a fan-out whose second handler failed) leaves every row holding
/// the collective exactly once. The row's <c>version</c>, which every applied batch bumps, counts how many times a row
/// received it.
/// </summary>
/// <tests>src/Whizbang.Data.Dapper.Postgres/Collective/DapperCollectiveEventApplier.cs</tests>
/// <tests>src/Whizbang.Data.Postgres/Collective/CollectiveApplyProgress.cs</tests>
[NotInParallel("PostgreSQL")]
public class DapperCollectiveApplyProgressTests : PostgresTestBase {
  private const string TABLE = "wh_per_collective_dapper_progress";
  private const string TENANT = "t-progress";
  private static readonly IReadOnlyDictionary<Type, string> _noSiblings = new Dictionary<Type, string>();

  // Five rows in batches of two: three batches, the last one short.
  private static readonly CollectiveApplyOptions _batchesOfTwo = new() { BatchSize = 2 };

  [Test]
  [Timeout(120000)]
  public async Task AnApplyRetriedAfterABusyLock_ResumesAfterTheLastCommittedBatch_SoEveryRowHoldsItOnceAsync(
      CancellationToken cancellationToken) {
    await _seedAsync(5);
    var eventId = Guid.NewGuid();
    var options = _batchesOfTwo with { LockWaitSeconds = 1, LockWaitRenewals = 0 };
    var lockKey = CollectiveApplyLockKey.Compute(TABLE, new TenantCollectiveScope(TENANT).ScopeIdentity);
    using var holder = await ConnectionFactory.CreateConnectionAsync(cancellationToken);
    await using var holdTx = await ((Npgsql.NpgsqlConnection)holder).BeginTransactionAsync(cancellationToken);

    // After the first batch commits, another apply takes the lock this one needs for its second batch.
    await Assert.That(async () => await _executor(options).ApplyAsync(
        _archiveEntry(), new ArchivePerspective(), _event(), new TenantCollectiveScopeResolver(), ConnectionFactory, eventId,
        onBatchApplied: async ct => {
          await using var hold = ((Npgsql.NpgsqlConnection)holder).CreateCommand();
          hold.Transaction = holdTx;
          hold.CommandText = "SELECT pg_advisory_xact_lock(@k)";
          hold.Parameters.AddWithValue("k", lockKey);
          await hold.ExecuteNonQueryAsync(ct);
        },
        cancellationToken: cancellationToken))
      .Throws<CollectiveApplyLockBusyException>();
    await holdTx.RollbackAsync(cancellationToken);

    var retried = await _executor(options).ApplyAsync(
      _archiveEntry(), new ArchivePerspective(), _event(), new TenantCollectiveScopeResolver(), ConnectionFactory, eventId,
      cancellationToken: cancellationToken);

    await Assert.That(retried).IsEqualTo(3).Because("the retry applies the three rows the first batch did not reach");
    await Assert.That(await _versionsAsync()).IsEquivalentTo(new long[] { 2, 2, 2, 2, 2 })
      .Because("every row received the collective once: the retry resumed after the batch that had committed");
  }

  [Test]
  public async Task AnApplyRetriedAfterItFailedBetweenBatches_ResumesAfterTheLastCommittedBatchAsync() {
    await _seedAsync(5);
    var eventId = Guid.NewGuid();

    // The process stops right after the first batch commits.
    await Assert.That(async () => await _executor(_batchesOfTwo).ApplyAsync(
        _archiveEntry(), new ArchivePerspective(), _event(), new TenantCollectiveScopeResolver(), ConnectionFactory, eventId,
        onBatchApplied: _ => throw new InvalidOperationException("the host went down after the first batch")))
      .Throws<InvalidOperationException>();

    var retried = await _executor(_batchesOfTwo).ApplyAsync(
      _archiveEntry(), new ArchivePerspective(), _event(), new TenantCollectiveScopeResolver(), ConnectionFactory, eventId);

    await Assert.That(retried).IsEqualTo(3);
    await Assert.That(await _versionsAsync()).IsEquivalentTo(new long[] { 2, 2, 2, 2, 2 });
  }

  [Test]
  public async Task AFinishedApply_AppliedAgain_ChangesNoRowAsync() {
    await _seedAsync(5);
    var eventId = Guid.NewGuid();
    _ = await _executor(_batchesOfTwo).ApplyAsync(
      _archiveEntry(), new ArchivePerspective(), _event(), new TenantCollectiveScopeResolver(), ConnectionFactory, eventId);

    // Delivered again before its completion reached the queue: the finished apply is skipped.
    var again = await _executor(_batchesOfTwo).ApplyAsync(
      _archiveEntry(), new ArchivePerspective(), _event(), new TenantCollectiveScopeResolver(), ConnectionFactory, eventId);

    await Assert.That(again).IsEqualTo(0).Because("a finished apply of this collective by this handler is not run again");
    await Assert.That(await _versionsAsync()).IsEquivalentTo(new long[] { 2, 2, 2, 2, 2 });
  }

  [Test]
  public async Task AFanOutRetriedAfterItsSecondHandlerFailed_DoesNotApplyTheFirstAgain_AndResumesTheSecondAsync() {
    await _seedAsync(5);
    var eventId = Guid.NewGuid();
    var reports = 0;

    // The first handler finishes in three batches; the process stops after the second handler's first batch.
    await Assert.That(async () => await _fanOut().DispatchAsync(
        _event(), eventId, ConnectionFactory,
        onBatchApplied: _ => ++reports == 4
          ? throw new InvalidOperationException("the host went down during the second handler")
          : ValueTask.CompletedTask))
      .Throws<InvalidOperationException>();

    var retried = await _fanOut().DispatchAsync(_event(), eventId, ConnectionFactory);

    await Assert.That(retried.AffectedRowCount).IsEqualTo(3)
      .Because("the first handler had finished, and the second resumed after its committed batch");
    await Assert.That(await _versionsAsync()).IsEquivalentTo(new long[] { 3, 3, 3, 3, 3 })
      .Because("each of the two handlers reached every row once");
  }

  // Two appliers that both believe they own the collective: the other one applied the next batch and moved the record
  // while this one was between batches. This one's next batch expected the record where it left it, so it applies nothing
  // and reads the record again.
  [Test]
  public async Task ABatch_WhoseRecordAnotherApplierMoved_AppliesNothing_AndGoesOnFromWhereTheOtherGotAsync() {
    var ids = await _seedAsync(6);
    var eventId = Guid.NewGuid();
    var reports = 0;

    var applied = await _executor(_batchesOfTwo).ApplyAsync(
      _archiveEntry(), new ArchivePerspective(), _event(), new TenantCollectiveScopeResolver(), ConnectionFactory, eventId,
      onBatchApplied: async ct => {
        if (++reports == 1) {
          using var other = await ConnectionFactory.CreateConnectionAsync(ct);
          await other.ExecuteAsync(
            $"UPDATE {TABLE} SET version = version + 1 WHERE id = ANY(@rows);"
            + " UPDATE wh_collective_apply_progress SET through_id = @through WHERE event_id = @eventId",
            new { rows = new[] { ids[2], ids[3] }, through = ids[3], eventId });
        }
      });

    await Assert.That(applied).IsEqualTo(4).Because("this applier reached the first two rows and the last two");
    await Assert.That(await _versionsAsync()).IsEquivalentTo(new long[] { 2, 2, 2, 2, 2, 2 })
      .Because("the rows the other applier reached were not applied a second time");
  }

  [Test]
  public async Task AResumedApply_StampsTheRowsItReaches_WithTheTimeTheApplyStartedAsync() {
    await _seedAsync(5);
    var eventId = Guid.NewGuid();
    await Assert.That(async () => await _executor(_batchesOfTwo).ApplyAsync(
        _archiveEntry(), new ArchivePerspective(), _event(), new TenantCollectiveScopeResolver(), ConnectionFactory, eventId,
        onBatchApplied: _ => throw new InvalidOperationException("the host went down after the first batch")))
      .Throws<InvalidOperationException>();

    _ = await _executor(_batchesOfTwo).ApplyAsync(
      _archiveEntry(), new ArchivePerspective(), _event(), new TenantCollectiveScopeResolver(), ConnectionFactory, eventId);

    using var conn = await ConnectionFactory.CreateConnectionAsync();
    var stamps = (await conn.QueryAsync<DateTime>($"SELECT DISTINCT updated_at FROM {TABLE}")).ToList();
    await Assert.That(stamps.Count).IsEqualTo(1)
      .Because("every row of one apply carries the apply's one timestamp, the resumed rows included");
  }

  // An apply with no event id has nothing to resume by: each one records under an id of its own and applies in full.
  [Test]
  public async Task AnApplyWithNoEventId_IsNeverSkipped_ByAnEarlierOneAsync() {
    await _seedAsync(3);

    var first = await _executor(_batchesOfTwo).ApplyAsync(
      _archiveEntry(), new ArchivePerspective(), _event(), new TenantCollectiveScopeResolver(), ConnectionFactory, Guid.Empty);
    var second = await _executor(_batchesOfTwo).ApplyAsync(
      _archiveEntry(), new ArchivePerspective(), _event(), new TenantCollectiveScopeResolver(), ConnectionFactory, Guid.Empty);

    await Assert.That((first, second)).IsEqualTo((3, 3));
    await Assert.That(await _versionsAsync()).IsEquivalentTo(new long[] { 3, 3, 3 });
  }

  // A cohort with no row finishes in its first batch, with nothing reached.
  [Test]
  public async Task AnApplyThatReachesNoRow_IsRecordedFinished_AndIsSkippedWhenAppliedAgainAsync() {
    await _seedAsync(0);
    var eventId = Guid.NewGuid();

    var first = await _executor(_batchesOfTwo).ApplyAsync(
      _archiveEntry(), new ArchivePerspective(), _event(), new TenantCollectiveScopeResolver(), ConnectionFactory, eventId);
    var again = await _executor(_batchesOfTwo).ApplyAsync(
      _archiveEntry(), new ArchivePerspective(), _event(), new TenantCollectiveScopeResolver(), ConnectionFactory, eventId);

    using var conn = await ConnectionFactory.CreateConnectionAsync();
    var record = await conn.QuerySingleAsync<(Guid? Through, bool Completed)>(
      "SELECT through_id, completed FROM wh_collective_apply_progress WHERE event_id = @eventId", new { eventId });
    await Assert.That((first, again)).IsEqualTo((0, 0));
    await Assert.That(record.Through).IsNull().Because("no row was reached");
    await Assert.That(record.Completed).IsTrue();
  }

  private static DapperCollectiveEventExecutor<JobModel> _executor(CollectiveApplyOptions options) =>
    new(TABLE, _noSiblings, options);

  private static CollectiveDispatcher _fanOut() {
    var services = new ServiceCollection();
    services.AddSingleton(new ArchivePerspective());
    services.AddSingleton(new ReviewPerspective());
    return new CollectiveDispatcher(
      services.BuildServiceProvider(), [_archiveEntry(), _reviewEntry()], [new TenantCollectiveScopeResolver()],
      [_executor(_batchesOfTwo)]);
  }

  private static ArchiveEvent _event() => new() { Scope = new TenantCollectiveScope(TENANT) };

  private static CollectiveApplyEntry _archiveEntry() => new(
    ModelType: typeof(JobModel), EventType: typeof(ArchiveEvent), HandlerType: typeof(ArchivePerspective),
    MethodName: nameof(ArchivePerspective.Archive), ScopeHandling: CollectiveScopeHandling.Framework,
    SpecKind: CollectiveSpecKind.Linq, Invoker: static (h, e, _) => ((ArchivePerspective)h).Archive((ArchiveEvent)e));

  private static CollectiveApplyEntry _reviewEntry() => new(
    ModelType: typeof(JobModel), EventType: typeof(ArchiveEvent), HandlerType: typeof(ReviewPerspective),
    MethodName: nameof(ReviewPerspective.Review), ScopeHandling: CollectiveScopeHandling.Framework,
    SpecKind: CollectiveSpecKind.Linq, Invoker: static (h, e, _) => ((ReviewPerspective)h).Review((ArchiveEvent)e));

  // The ids in the order Postgres sorts them (the order of their text), which is the order the batches take them in.
  private async Task<Guid[]> _seedAsync(int rows) {
    var ids = Enumerable.Range(0, rows).Select(_ => Guid.NewGuid())
      .OrderBy(id => id.ToString("D"), StringComparer.Ordinal).ToArray();
    using var conn = await ConnectionFactory.CreateConnectionAsync();
    await conn.ExecuteAsync($@"
      CREATE TABLE IF NOT EXISTS {TABLE} (
        id uuid PRIMARY KEY, data jsonb NOT NULL, metadata jsonb, scope jsonb NOT NULL,
        created_at timestamptz NOT NULL DEFAULT now(), updated_at timestamptz NOT NULL DEFAULT now(),
        version bigint NOT NULL DEFAULT 1);
      TRUNCATE {TABLE};");
    foreach (var id in ids) {
      await conn.ExecuteAsync(
        $"INSERT INTO {TABLE} (id, data, scope) VALUES (@id, '{{\"Status\": \"Active\"}}'::jsonb, @scope::jsonb)",
        new { id, scope = $"{{\"t\": \"{TENANT}\"}}" });
    }
    return ids;
  }

  private async Task<long[]> _versionsAsync() {
    using var conn = await ConnectionFactory.CreateConnectionAsync();
    return [.. await conn.QueryAsync<long>($"SELECT version FROM {TABLE} ORDER BY id")];
  }

  private sealed class JobModel {
    public string Status { get; set; } = "";
  }

  private sealed class ArchivePerspective {
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Sonar", "S1172:Unused method parameters should be removed", Justification = "The executor discovers a collective handler by its signature; the event parameter is part of that contract.")]
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Minor Code Smell", "S2325:Methods and properties that don't access instance data should be static", Justification = "Invoked through the instance invoker the generator emits; a static member does not compile there.")]
    public ICollectiveSpec<JobModel> Archive(ArchiveEvent e) => new Spec(s => s.SetProperty(j => j.Status, "Archived"));
  }

  private sealed class ReviewPerspective {
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Sonar", "S1172:Unused method parameters should be removed", Justification = "The executor discovers a collective handler by its signature; the event parameter is part of that contract.")]
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Minor Code Smell", "S2325:Methods and properties that don't access instance data should be static", Justification = "Invoked through the instance invoker the generator emits; a static member does not compile there.")]
    public ICollectiveSpec<JobModel> Review(ArchiveEvent e) => new Spec(s => s.SetProperty(j => j.Status, "Reviewed"));
  }

  private sealed record Spec(Expression<Action<ICollectiveSetters<JobModel>>> Setters) : ICollectiveSpec<JobModel>;

  private sealed record ArchiveEvent : ICollectiveEvent {
    public required CollectiveScope Scope { get; init; }
  }
}
