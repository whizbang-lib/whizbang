// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

#pragma warning disable CA1859, CA1861 // handlers return the spec interface as a generated invoker expects; each test states its expected rows inline

using System.Linq.Expressions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Lenses;
using Whizbang.Core.Messaging;
using Whizbang.Core.Perspectives;
using Whizbang.Core.Serialization;
using Whizbang.Data.EFCore.Postgres.Collective;
using Whizbang.Data.EFCore.Postgres.Functions;
using Whizbang.Data.Postgres.Collective;

namespace Whizbang.Data.EFCore.Postgres.Tests.Collective;

/// <summary>
/// A collective applied in batches resumes after its last committed batch (#1969), on the EF Core driver: every retry path
/// (a busy apply lock, a failure between batches, a re-run of a finished apply, a fan-out whose second handler failed)
/// leaves every row holding the collective exactly once. The row's <c>version</c>, which every applied batch bumps, counts
/// how many times a row received it.
/// </summary>
/// <tests>src/Whizbang.Data.EFCore.Postgres/Collective/EFCoreCollectiveAdapter.cs</tests>
/// <tests>src/Whizbang.Data.Postgres/Collective/CollectiveApplyProgress.cs</tests>
[Category("Integration")]
[Category("CollectiveEvents")]
[Category("Shard3")]
public class CollectiveApplyProgressEFCoreTests : EFCoreTestBase {
  private const string TABLE = "wh_per_collective_progress";
  private const string TENANT = "t-progress";

  // The archive handler's entry key: handler type, method and model, by name (CollectiveApplyEntry.EntryKey).
  private const string ARCHIVE_KEY =
    "Whizbang.Data.EFCore.Postgres.Tests.Collective.CollectiveApplyProgressEFCoreTests+ArchivePerspective.Archive:Whizbang.Data.EFCore.Postgres.Tests.Collective.CollectiveApplyProgressEFCoreTests+JobModel";

  // Five rows in batches of two: three batches, the last one short.
  private static readonly CollectiveApplyOptions _batchesOfTwo = new() { BatchSize = 2 };

  private NpgsqlDataSource? _jsonSource;

  [Test]
  [Timeout(120000)]
  public async Task AnApplyRetriedAfterABusyLock_ResumesAfterTheLastCommittedBatch_SoEveryRowHoldsItOnceAsync(
      CancellationToken cancellationToken) {
    await _seedAsync(5);
    var eventId = Guid.NewGuid();
    var options = _batchesOfTwo with { LockWaitSeconds = 1, LockWaitRenewals = 0 };
    var lockKey = CollectiveApplyLockKey.Compute(TABLE, new TenantCollectiveScope(TENANT).ScopeIdentity);
    await using var holder = new NpgsqlConnection(ConnectionString);
    await holder.OpenAsync(cancellationToken);
    await using var holdTx = await holder.BeginTransactionAsync(cancellationToken);

    // After the first batch commits, another apply takes the lock this one needs for its second batch.
    await using (var ctx = _context()) {
      await Assert.That(async () => await new EFCoreCollectiveEventExecutor<JobModel>(options).ApplyAsync(
          _archiveEntry(), new ArchivePerspective(), _event(), new TenantCollectiveScopeResolver(), ctx, eventId,
          onBatchApplied: async ct => {
            await using var hold = holder.CreateCommand();
            hold.Transaction = holdTx;
            hold.CommandText = "SELECT pg_advisory_xact_lock(@k)";
            hold.Parameters.AddWithValue("k", lockKey);
            await hold.ExecuteNonQueryAsync(ct);
          },
          cancellationToken: cancellationToken))
        .Throws<CollectiveApplyLockBusyException>();
    }
    await holdTx.RollbackAsync(cancellationToken);

    int retried;
    await using (var ctx = _context()) {
      retried = await new EFCoreCollectiveEventExecutor<JobModel>(options).ApplyAsync(
        _archiveEntry(), new ArchivePerspective(), _event(), new TenantCollectiveScopeResolver(), ctx, eventId,
        cancellationToken: cancellationToken);
    }

    await Assert.That(retried).IsEqualTo(3).Because("the retry applies the three rows the first batch did not reach");
    await Assert.That(await _versionsAsync()).IsEquivalentTo([2, 2, 2, 2, 2])
      .Because("every row received the collective once: the retry resumed after the batch that had committed");
  }

  [Test]
  public async Task AnApplyRetriedAfterItFailedBetweenBatches_ResumesAfterTheLastCommittedBatchAsync() {
    await _seedAsync(5);
    var eventId = Guid.NewGuid();

    // The process stops right after the first batch commits.
    await using (var ctx = _context()) {
      await Assert.That(async () => await new EFCoreCollectiveEventExecutor<JobModel>(_batchesOfTwo).ApplyAsync(
          _archiveEntry(), new ArchivePerspective(), _event(), new TenantCollectiveScopeResolver(), ctx, eventId,
          onBatchApplied: _ => throw new InvalidOperationException("the host went down after the first batch")))
        .Throws<InvalidOperationException>();
    }

    int retried;
    await using (var ctx = _context()) {
      retried = await new EFCoreCollectiveEventExecutor<JobModel>(_batchesOfTwo).ApplyAsync(
        _archiveEntry(), new ArchivePerspective(), _event(), new TenantCollectiveScopeResolver(), ctx, eventId);
    }

    await Assert.That(retried).IsEqualTo(3);
    await Assert.That(await _versionsAsync()).IsEquivalentTo([2, 2, 2, 2, 2]);
  }

  [Test]
  public async Task AFinishedApply_AppliedAgain_ChangesNoRowAsync() {
    await _seedAsync(5);
    var eventId = Guid.NewGuid();
    await using var ctx = _context();
    _ = await new EFCoreCollectiveEventExecutor<JobModel>(_batchesOfTwo).ApplyAsync(
      _archiveEntry(), new ArchivePerspective(), _event(), new TenantCollectiveScopeResolver(), ctx, eventId);

    // Delivered again before its completion reached the queue: the finished apply is skipped.
    var again = await new EFCoreCollectiveEventExecutor<JobModel>(_batchesOfTwo).ApplyAsync(
      _archiveEntry(), new ArchivePerspective(), _event(), new TenantCollectiveScopeResolver(), ctx, eventId);

    await Assert.That(again).IsEqualTo(0).Because("a finished apply of this collective by this handler is not run again");
    await Assert.That(await _versionsAsync()).IsEquivalentTo([2, 2, 2, 2, 2]);
  }

  [Test]
  public async Task AFanOutRetriedAfterItsSecondHandlerFailed_DoesNotApplyTheFirstAgain_AndResumesTheSecondAsync() {
    await _seedAsync(5);
    var eventId = Guid.NewGuid();
    var reports = 0;

    // The first handler finishes in three batches; the process stops after the second handler's first batch.
    await using (var ctx = _context()) {
      await Assert.That(async () => await _fanOut().DispatchAsync(
          _event(), eventId, ctx,
          onBatchApplied: _ => ++reports == 4
            ? throw new InvalidOperationException("the host went down during the second handler")
            : ValueTask.CompletedTask))
        .Throws<InvalidOperationException>();
    }

    CollectiveDispatchResult retried;
    await using (var ctx = _context()) {
      retried = await _fanOut().DispatchAsync(_event(), eventId, ctx);
    }

    await Assert.That(retried.AffectedRowCount).IsEqualTo(3)
      .Because("the first handler had finished, and the second resumed after its committed batch");
    await Assert.That(await _versionsAsync()).IsEquivalentTo([3, 3, 3, 3, 3])
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

    await using var ctx = _context();
    var applied = await new EFCoreCollectiveEventExecutor<JobModel>(_batchesOfTwo).ApplyAsync(
      _archiveEntry(), new ArchivePerspective(), _event(), new TenantCollectiveScopeResolver(), ctx, eventId,
      onBatchApplied: async ct => {
        if (++reports == 1) {
          await using var other = new NpgsqlConnection(ConnectionString);
          await other.OpenAsync(ct);
          await using var move = other.CreateCommand();
          move.CommandText = $"UPDATE {TABLE} SET version = version + 1 WHERE id = ANY(@rows);"
            + " UPDATE wh_collective_apply_progress SET through_id = @through WHERE event_id = @eventId";
          move.Parameters.AddWithValue("rows", new[] { ids[2], ids[3] });
          move.Parameters.AddWithValue("through", ids[3]);
          move.Parameters.AddWithValue("eventId", eventId);
          await move.ExecuteNonQueryAsync(ct);
        }
      });

    await Assert.That(applied).IsEqualTo(4).Because("this applier reached the first two rows and the last two");
    await Assert.That(await _versionsAsync()).IsEquivalentTo([2, 2, 2, 2, 2, 2])
      .Because("the rows the other applier reached were not applied a second time");
  }

  [Test]
  public async Task AResumedApply_StampsTheRowsItReaches_WithTheTimeTheApplyStartedAsync() {
    await _seedAsync(5);
    var eventId = Guid.NewGuid();
    DateTime? started = null;
    await using (var ctx = _context()) {
      await Assert.That(async () => await new EFCoreCollectiveEventExecutor<JobModel>(_batchesOfTwo).ApplyAsync(
          _archiveEntry(), new ArchivePerspective(), _event(), new TenantCollectiveScopeResolver(), ctx, eventId,
          onBatchApplied: async ct => {
            // The time the first batch stamped, then the host goes down.
            started = await _scalarAsync<DateTime>($"SELECT max(updated_at) FROM {TABLE} WHERE version = 2", ct);
            throw new InvalidOperationException("the host went down after the first batch");
          }))
        .Throws<InvalidOperationException>();
    }
    await using (var ctx = _context()) {
      _ = await new EFCoreCollectiveEventExecutor<JobModel>(_batchesOfTwo).ApplyAsync(
        _archiveEntry(), new ArchivePerspective(), _event(), new TenantCollectiveScopeResolver(), ctx, eventId);
    }

    await Assert.That(await _scalarAsync<long>($"SELECT count(DISTINCT updated_at) FROM {TABLE}", CancellationToken.None)).IsEqualTo(1L);
    await Assert.That(await _scalarAsync<DateTime>($"SELECT max(updated_at) FROM {TABLE}", CancellationToken.None)).IsEqualTo(started!.Value)
      .Because("every row of one apply carries the time the apply started, the resumed rows included");
  }

  // A dead letter left the apply abandoned after its first batch; recovery re-queues the collective, and the apply resumes
  // after that batch, completes the rest once, and is no longer abandoned (#1970).
  [Test]
  public async Task ARecoveredDeadLetter_ResumesTheAbandonedApply_AndCompletesTheRestOnceAsync() {
    var ids = await _seedAsync(5);
    var eventId = Guid.NewGuid();
    await using var conn = new NpgsqlConnection(ConnectionString);
    await conn.OpenAsync();
    await using (var seed = conn.CreateCommand()) {
      seed.CommandText = $"UPDATE {TABLE} SET version = 2 WHERE id = ANY(@reached);"
        + " INSERT INTO wh_collective_apply_progress (event_id, entry_key, through_id, completed, abandoned, applied_at)"
        + " VALUES (@eventId, @key, @through, false, true, now()), (@eventId, '*', NULL, false, true, now())";
      seed.Parameters.AddWithValue("reached", new[] { ids[0], ids[1] });
      seed.Parameters.AddWithValue("eventId", eventId);
      seed.Parameters.AddWithValue("key", ARCHIVE_KEY);
      seed.Parameters.AddWithValue("through", ids[1]);
      await seed.ExecuteNonQueryAsync();
    }

    int applied;
    await using (var ctx = _context()) {
      applied = await new EFCoreCollectiveEventExecutor<JobModel>(_batchesOfTwo).ApplyAsync(
        _archiveEntry(), new ArchivePerspective(), _event(), new TenantCollectiveScopeResolver(), ctx, eventId);
    }

    await using var check = conn.CreateCommand();
    check.CommandText = "SELECT completed AND NOT abandoned FROM wh_collective_apply_progress WHERE event_id = @eventId AND entry_key = @key";
    check.Parameters.AddWithValue("eventId", eventId);
    check.Parameters.AddWithValue("key", ARCHIVE_KEY);
    await Assert.That(applied).IsEqualTo(3);
    await Assert.That(await _versionsAsync()).IsEquivalentTo([2, 2, 2, 2, 2]);
    await Assert.That((bool)(await check.ExecuteScalarAsync())!).IsTrue()
      .Because("the recovered apply finished, and is no longer abandoned");
  }

  // An apply with no event id has nothing to resume by: each one records under an id of its own and applies in full.
  [Test]
  public async Task AnApplyWithNoEventId_IsNeverSkipped_ByAnEarlierOneAsync() {
    await _seedAsync(3);
    await using var ctx = _context();

    var first = await new EFCoreCollectiveEventExecutor<JobModel>(_batchesOfTwo).ApplyAsync(
      _archiveEntry(), new ArchivePerspective(), _event(), new TenantCollectiveScopeResolver(), ctx, Guid.Empty);
    var second = await new EFCoreCollectiveEventExecutor<JobModel>(_batchesOfTwo).ApplyAsync(
      _archiveEntry(), new ArchivePerspective(), _event(), new TenantCollectiveScopeResolver(), ctx, Guid.Empty);

    await Assert.That((first, second)).IsEqualTo((3, 3));
    await Assert.That(await _versionsAsync()).IsEquivalentTo([3, 3, 3]);
  }

  [After(Test)]
  public async Task DisposeJsonSourceAsync() {
    if (_jsonSource is not null) {
      await _jsonSource.DisposeAsync();
      _jsonSource = null;
    }
  }

  private static CollectiveDispatcher _fanOut() {
    var services = new ServiceCollection();
    services.AddSingleton(new ArchivePerspective());
    services.AddSingleton(new ReviewPerspective());
    return new CollectiveDispatcher(
      services.BuildServiceProvider(), [_archiveEntry(), _reviewEntry()], [new TenantCollectiveScopeResolver()],
      [new EFCoreCollectiveEventExecutor<JobModel>(_batchesOfTwo)]);
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

  private JobDbContext _context() {
    if (_jsonSource is null) {
      var builder = new NpgsqlDataSourceBuilder(ConnectionString);
      builder.ConfigureJsonOptions(JsonContextRegistry.CreateCombinedOptions());
      builder.EnableDynamicJson();
      _jsonSource = builder.Build();
    }
    var options = new DbContextOptionsBuilder<JobDbContext>()
      .UseNpgsql(_jsonSource, npg => npg.UseWhizbangFunctions())
      .ConfigureWarnings(w => w.Ignore(Microsoft.EntityFrameworkCore.Diagnostics.CoreEventId.ManyServiceProvidersCreatedWarning))
      .Options;
    return new JobDbContext(options);
  }

  // The ids in the order Postgres sorts them (the order of their text), which is the order the batches take them in.
  private async Task<Guid[]> _seedAsync(int rows) {
    var ids = Enumerable.Range(0, rows).Select(_ => Guid.NewGuid())
      .OrderBy(id => id.ToString("D"), StringComparer.Ordinal).ToArray();
    await using var conn = new NpgsqlConnection(ConnectionString);
    await conn.OpenAsync();
    await using (var create = conn.CreateCommand()) {
      create.CommandText = $"""
        CREATE TABLE {TABLE} (
          id UUID PRIMARY KEY, data JSONB NOT NULL, metadata JSONB NOT NULL, scope JSONB NOT NULL,
          created_at TIMESTAMPTZ NOT NULL DEFAULT now(), updated_at TIMESTAMPTZ NOT NULL DEFAULT now(),
          version INTEGER NOT NULL DEFAULT 1);
        """;
      await create.ExecuteNonQueryAsync();
    }
    foreach (var id in ids) {
      await using var insert = conn.CreateCommand();
      insert.CommandText = $"INSERT INTO {TABLE} (id, data, metadata, scope) VALUES (@id, '{{\"Status\": \"Active\"}}'::jsonb, '{{}}'::jsonb, @scope::jsonb)";
      insert.Parameters.AddWithValue("id", id);
      insert.Parameters.AddWithValue("scope", $"{{\"t\": \"{TENANT}\"}}");
      await insert.ExecuteNonQueryAsync();
    }
    return ids;
  }

  private async Task<T> _scalarAsync<T>(string sql, CancellationToken cancellationToken) {
    await using var conn = new NpgsqlConnection(ConnectionString);
    await conn.OpenAsync(cancellationToken);
    await using var cmd = conn.CreateCommand();
    cmd.CommandText = sql;
    return (T)(await cmd.ExecuteScalarAsync(cancellationToken))!;
  }

  private async Task<int[]> _versionsAsync() {
    await using var conn = new NpgsqlConnection(ConnectionString);
    await conn.OpenAsync();
    await using var cmd = conn.CreateCommand();
    cmd.CommandText = $"SELECT version FROM {TABLE} ORDER BY id";
    var versions = new List<int>();
    await using var reader = await cmd.ExecuteReaderAsync();
    while (await reader.ReadAsync()) {
      versions.Add(reader.GetInt32(0));
    }
    return [.. versions];
  }

  internal sealed class JobModel {
    public string Status { get; set; } = string.Empty;
  }

  internal sealed class ArchivePerspective {
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Sonar", "S1172:Unused method parameters should be removed", Justification = "The executor discovers a collective handler by its signature; the event parameter is part of that contract.")]
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Minor Code Smell", "S2325:Methods and properties that don't access instance data should be static", Justification = "Invoked through the instance invoker the generator emits; a static member does not compile there.")]
    public ICollectiveSpec<JobModel> Archive(ArchiveEvent e) => new Spec(s => s.SetProperty(j => j.Status, "Archived"));
  }

  internal sealed class ReviewPerspective {
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Sonar", "S1172:Unused method parameters should be removed", Justification = "The executor discovers a collective handler by its signature; the event parameter is part of that contract.")]
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Minor Code Smell", "S2325:Methods and properties that don't access instance data should be static", Justification = "Invoked through the instance invoker the generator emits; a static member does not compile there.")]
    public ICollectiveSpec<JobModel> Review(ArchiveEvent e) => new Spec(s => s.SetProperty(j => j.Status, "Reviewed"));
  }

  private sealed record Spec(Expression<Action<ICollectiveSetters<JobModel>>> Setters) : ICollectiveSpec<JobModel>;

  internal sealed record ArchiveEvent : ICollectiveEvent {
    public required CollectiveScope Scope { get; init; }
  }

  private sealed class JobDbContext(DbContextOptions<JobDbContext> options) : DbContext(options) {
    protected override void OnModelCreating(ModelBuilder modelBuilder) {
      base.OnModelCreating(modelBuilder);
      modelBuilder.Entity<PerspectiveRow<JobModel>>(e => {
        e.ToTable(TABLE);
        e.HasKey(x => x.Id);
        e.Property(x => x.Id).HasColumnName("id");
        e.ComplexProperty(x => x.Data, d => d.ToJson("data"));
        e.Property(x => x.Metadata).HasColumnName("metadata").HasColumnType("jsonb");
        e.Property(x => x.Scope).HasColumnName("scope").HasColumnType("jsonb");
        e.Property(x => x.CreatedAt).HasColumnName("created_at");
        e.Property(x => x.UpdatedAt).HasColumnName("updated_at");
        e.Property(x => x.Version).HasColumnName("version");
      });
    }
  }
}
