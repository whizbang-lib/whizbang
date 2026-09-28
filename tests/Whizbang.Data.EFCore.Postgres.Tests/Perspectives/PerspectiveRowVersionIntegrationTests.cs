using System.Linq.Expressions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Npgsql;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core;
using Whizbang.Core.Dispatch;
using Whizbang.Core.Lenses;
using Whizbang.Core.Messaging;
using Whizbang.Core.Observability;
using Whizbang.Core.Perspectives;
using Whizbang.Core.ValueObjects;
using Whizbang.Data.EFCore.Postgres.Collective;
using Whizbang.Data.EFCore.Postgres.Tests.Generated;
using Whizbang.Data.Postgres.Collective;

namespace Whizbang.Data.EFCore.Postgres.Tests.Perspectives;

/// <summary>
/// Issue #928 against real Postgres. A per-stream apply reads a row, folds its events in memory and
/// writes the whole row back; a collective apply writes the same rows through a set-based UPDATE.
/// When the collective committed between the per-stream read and write, the write put the stale
/// values back. The read now captures the row's version (its <c>xmin</c>, which every UPDATE moves,
/// whichever path issued it and whether or not it bumped the <c>version</c> column) and the write lands
/// only on that version; a moved row is refused with <see cref="PerspectiveRowConflictException"/> and
/// the runner re-reads and re-applies.
/// </summary>
/// <remarks>
/// The interleaving is forced, never timed: <see cref="InterleavingStore"/> runs the collective (on its
/// own context, committed) after the runner's read and before its write. Every case runs on both write
/// paths: the atomic single-statement path and the EF fallback the strategy uses when the atomic path
/// declines a row. Mutates the process-wide atomic-path provider, hence the shared serialization key.
/// </remarks>
/// <docs>fundamentals/perspectives/perspectives</docs>
[Category("Integration")]
[NotInParallel("EFCorePostgresTests")]
[Category("Shard4")]
public class PerspectiveRowVersionIntegrationTests : EFCoreTestBase {
  private const string TABLE = "wh_per_action_test";

  [After(Test)]
  public Task ClearPathOneProviderAsync() {
    BaseUpsertStrategy.PathOnePersistenceOptionsProvider = null;
    return Task.CompletedTask;
  }

  private static void _usePath(bool atomic) =>
    BaseUpsertStrategy.PathOnePersistenceOptionsProvider = atomic
      ? () => PerspectivePersistenceJsonContext.CreateOptions(
          MessageJsonContext.Default,
          global::Whizbang.Core.Generated.InfrastructureJsonContext.Default)
      : null;

  // ── The race, end to end through the generated runner ──────────────────────────────────────────

  [Test]
  [Arguments(true)]
  [Arguments(false)]
  public async Task CollectiveCommittedBetweenReadAndWrite_IsNotOverwritten_FinalRowReflectsBothAsync(bool atomicPath) {
    _usePath(atomicPath);
    var id = Guid.CreateVersion7();
    await _seedAsync(id, "member", 1);

    await using var context = CreateDbContext();
    var store = new InterleavingStore(new EFCorePostgresPerspectiveStore<ActionTestModel>(context, TABLE)) {
      BeforeWrite = attempt => attempt == 1 ? _commitCollectiveAsync(id, "collective") : Task.CompletedTask,
    };

    var result = await _runner(store).RunWithEventsAsync(id, TABLE, null,
      [_envelope(new ActionTestUpdatedEvent { StreamId = id, NewValue = 2 })], CancellationToken.None);

    var row = await _rowAsync(id);
    await Assert.That(row.Data.Name).IsEqualTo("collective")
      .Because("the collective committed after the per-stream read; the per-stream write must not put the stale name back");
    await Assert.That(row.Data.Value).IsEqualTo(2)
      .Because("the per-stream event is applied onto the row as the collective left it");
    await Assert.That(store.ReadsForApply).IsEqualTo(2);
    await Assert.That(store.WriteAttempts).IsEqualTo(2);
    await Assert.That(result.EventsProcessed).IsEqualTo(1);
  }

  [Test]
  [Arguments(true)]
  [Arguments(false)]
  public async Task NoInterleaving_OneReadOneWrite_BehaviorUnchangedAsync(bool atomicPath) {
    _usePath(atomicPath);
    var id = Guid.CreateVersion7();
    await _seedAsync(id, "member", 1);
    var versionBefore = (await _rowAsync(id)).Version;

    await using var context = CreateDbContext();
    var store = new InterleavingStore(new EFCorePostgresPerspectiveStore<ActionTestModel>(context, TABLE));

    await _runner(store).RunWithEventsAsync(id, TABLE, null,
      [_envelope(new ActionTestUpdatedEvent { StreamId = id, NewValue = 2 })], CancellationToken.None);

    var row = await _rowAsync(id);
    await Assert.That(row.Data.Name).IsEqualTo("member");
    await Assert.That(row.Data.Value).IsEqualTo(2);
    await Assert.That(row.Version).IsEqualTo(versionBefore + 1);
    await Assert.That(store.ReadsForApply).IsEqualTo(1);
    await Assert.That(store.WriteAttempts).IsEqualTo(1);
  }

  [Test]
  [Arguments(true)]
  [Arguments(false)]
  public async Task CollectiveLandsBeforeEveryWrite_TheApplyGivesUpWithAConflict_AndNothingStaleLandsAsync(bool atomicPath) {
    _usePath(atomicPath);
    var id = Guid.CreateVersion7();
    await _seedAsync(id, "member", 1);

    await using var context = CreateDbContext();
    var store = new InterleavingStore(new EFCorePostgresPerspectiveStore<ActionTestModel>(context, TABLE)) {
      BeforeWrite = attempt => _commitCollectiveAsync(id, "collective-" + attempt),
    };

    var runner = _runner(store);
    var conflict = await Assert.That(async () => await runner.RunWithEventsAsync(id, TABLE, null,
        [_envelope(new ActionTestUpdatedEvent { StreamId = id, NewValue = 2 })], CancellationToken.None))
      .Throws<PerspectiveRowConflictException>();

    await Assert.That(conflict!.StreamId).IsEqualTo(id);
    await Assert.That(store.WriteAttempts).IsEqualTo(5);
    var row = await _rowAsync(id);
    await Assert.That(row.Data.Name).IsEqualTo("collective-5");
    await Assert.That(row.Data.Value).IsEqualTo(1)
      .Because("a refused write writes nothing: the event stays pending for the failure path to redeliver");
  }

  // ── The store contract, one read and one write at a time ───────────────────────────────────────

  [Test]
  public async Task ReadForApplyAsync_ReportsAbsence_ThenTheRowsVersionAndMetadata_AndSeesACollectiveAsync() {
    _usePath(true);
    var id = Guid.CreateVersion7();
    await using var context = CreateDbContext();
    var store = new EFCorePostgresPerspectiveStore<ActionTestModel>(context, TABLE);

    var absent = await store.ReadForApplyAsync(id);
    await Assert.That(absent.Version).IsEqualTo(PerspectiveRowVersion.Absent);
    await Assert.That(absent.Metadata).IsNull();

    var eventId = Guid.CreateVersion7().ToString("D");
    await store.UpsertAsync(id, new ActionTestModel { Id = id, Name = "member", Value = 1 }, new PerspectiveScope(), false,
      new PerspectiveMetadata { EventId = eventId, EventType = "Created", CommitSequence = 42, Timestamp = DateTime.UtcNow },
      PerspectiveRowVersion.Absent);

    var present = await store.ReadForApplyAsync(id);
    await Assert.That(present.Version.State).IsEqualTo(PerspectiveRowVersionState.Present);
    await Assert.That(present.Metadata!.EventId).IsEqualTo(eventId);
    await Assert.That(present.Metadata.EventType).IsEqualTo("Created");
    await Assert.That(present.Metadata.CommitSequence).IsEqualTo(42L);

    await _commitCollectiveAsync(id, "collective");
    var afterCollective = await store.ReadForApplyAsync(id);
    await Assert.That(afterCollective.Version).IsNotEqualTo(present.Version)
      .Because("the version is the row's own, so a collective that never touches the version column still moves it");
  }

  [Test]
  public async Task ReadForApplyAsync_OnARowWithoutCommitSequence_LeavesItNullAsync() {
    _usePath(true);
    var id = Guid.CreateVersion7();
    await using var context = CreateDbContext();
    var store = new EFCorePostgresPerspectiveStore<ActionTestModel>(context, TABLE);
    await store.UpsertAsync(id, new ActionTestModel { Id = id, Name = "member" }, new PerspectiveScope(), false,
      new PerspectiveMetadata { EventId = "e", EventType = "t", Timestamp = DateTime.UtcNow }, PerspectiveRowVersion.Unchecked);

    var read = await store.ReadForApplyAsync(id);

    await Assert.That(read.Metadata!.CommitSequence).IsNull();
  }

  [Test]
  public async Task ReadForApplyAsync_InsideAnAmbientTransaction_ReadsWithinItAsync() {
    _usePath(true);
    var id = Guid.CreateVersion7();
    await using var context = CreateDbContext();
    var store = new EFCorePostgresPerspectiveStore<ActionTestModel>(context, TABLE);

    await using var transaction = await context.Database.BeginTransactionAsync();
    await store.UpsertAsync(id, new ActionTestModel { Id = id, Name = "uncommitted" }, new PerspectiveScope(), false,
      new PerspectiveMetadata { EventId = "e", EventType = "t", Timestamp = DateTime.UtcNow }, PerspectiveRowVersion.Absent);
    var read = await store.ReadForApplyAsync(id);
    await transaction.RollbackAsync();

    await Assert.That(read.Version.State).IsEqualTo(PerspectiveRowVersionState.Present)
      .Because("the read joins the caller's transaction, so it sees that transaction's own uncommitted write");
  }

  [Test]
  [Arguments(true)]
  [Arguments(false)]
  public async Task Upsert_WithAVersionReadBeforeACollective_IsRefused_AndTheCollectiveSurvivesAsync(bool atomicPath) {
    _usePath(atomicPath);
    var id = Guid.CreateVersion7();
    await _seedAsync(id, "member", 1);
    await using var context = CreateDbContext();
    var store = new EFCorePostgresPerspectiveStore<ActionTestModel>(context, TABLE);
    var read = await store.ReadForApplyAsync(id);

    await _commitCollectiveAsync(id, "collective");
    var conflict = await Assert.That(async () => await store.UpsertAsync(id,
        new ActionTestModel { Id = id, Name = "member", Value = 2 }, new PerspectiveScope(), false, _meta(), read.Version))
      .Throws<PerspectiveRowConflictException>();

    await Assert.That(conflict!.ExpectedVersion).IsEqualTo(read.Version);
    await Assert.That(conflict.ActualVersion.State).IsEqualTo(PerspectiveRowVersionState.Present);
    await Assert.That(conflict.ActualVersion).IsNotEqualTo(read.Version);
    await Assert.That(conflict.ModelType).IsEqualTo(typeof(ActionTestModel));
    var row = await _rowAsync(id);
    await Assert.That(row.Data.Name).IsEqualTo("collective");
    await Assert.That(row.Data.Value).IsEqualTo(1);
  }

  [Test]
  [Arguments(true)]
  [Arguments(false)]
  public async Task Upsert_ExpectingNoRow_WhenARowAppeared_IsRefusedAsync(bool atomicPath) {
    _usePath(atomicPath);
    var id = Guid.CreateVersion7();
    await _seedAsync(id, "first-writer", 1);
    await using var context = CreateDbContext();
    var store = new EFCorePostgresPerspectiveStore<ActionTestModel>(context, TABLE);

    var conflict = await Assert.That(async () => await store.UpsertAsync(id,
        new ActionTestModel { Id = id, Name = "second-writer", Value = 9 }, new PerspectiveScope(), false, _meta(),
        PerspectiveRowVersion.Absent))
      .Throws<PerspectiveRowConflictException>();

    await Assert.That(conflict!.ExpectedVersion).IsEqualTo(PerspectiveRowVersion.Absent);
    await Assert.That(conflict.ActualVersion.State).IsEqualTo(PerspectiveRowVersionState.Present);
    await Assert.That((await _rowAsync(id)).Data.Name).IsEqualTo("first-writer");
  }

  [Test]
  [Arguments(true)]
  [Arguments(false)]
  public async Task Upsert_ExpectingARow_WhenItWasDeleted_IsRefused_AndDoesNotResurrectItAsync(bool atomicPath) {
    _usePath(atomicPath);
    var id = Guid.CreateVersion7();
    await _seedAsync(id, "member", 1);
    await using var context = CreateDbContext();
    var store = new EFCorePostgresPerspectiveStore<ActionTestModel>(context, TABLE);
    var read = await store.ReadForApplyAsync(id);
    await using (var other = CreateDbContext()) {
      await new EFCorePostgresPerspectiveStore<ActionTestModel>(other, TABLE).PurgeAsync(id);
    }

    var conflict = await Assert.That(async () => await store.UpsertAsync(id,
        new ActionTestModel { Id = id, Name = "member", Value = 2 }, new PerspectiveScope(), false, _meta(), read.Version))
      .Throws<PerspectiveRowConflictException>();

    await Assert.That(conflict!.ActualVersion).IsEqualTo(PerspectiveRowVersion.Absent);
    await using var verify = CreateDbContext();
    await Assert.That(await verify.Set<PerspectiveRow<ActionTestModel>>().AnyAsync(r => r.Id == id)).IsFalse();
  }

  [Test]
  [Arguments(true)]
  [Arguments(false)]
  public async Task Upsert_OnTheCurrentVersion_ThatTheCommitSequenceGuardRefuses_IsSkippedQuietlyAsync(bool atomicPath) {
    _usePath(atomicPath);
    var id = Guid.CreateVersion7();
    await using var context = CreateDbContext();
    var store = new EFCorePostgresPerspectiveStore<ActionTestModel>(context, TABLE);
    await store.UpsertAsync(id, new ActionTestModel { Id = id, Name = "newer", Value = 10 }, new PerspectiveScope(), false,
      _meta(commitSequence: 10), PerspectiveRowVersion.Absent);
    var read = await store.ReadForApplyAsync(id);

    await store.UpsertAsync(id, new ActionTestModel { Id = id, Name = "older", Value = 5 }, new PerspectiveScope(), false,
      _meta(commitSequence: 5), read.Version);

    var row = await _rowAsync(id);
    await Assert.That(row.Data.Name).IsEqualTo("newer")
      .Because("the row did not move, so this is the existing stale-sequence refusal, not a conflict: it stays silent");
    await Assert.That((await store.ReadForApplyAsync(id)).Version).IsEqualTo(read.Version);
  }

  [Test]
  public async Task Upsert_OnTheCurrentVersion_ForAVersionedTarget_WithAnOlderEvent_IsSkippedQuietlyAsync() {
    _usePath(true);
    var id = Guid.CreateVersion7();
    await using var context = CreateDbContext();
    var store = new EFCorePostgresPerspectiveStore<VersionedItem>(context, "wh_per_versioned_item");
    await store.UpsertAsync(id, new VersionedItem { Id = id, State = "Completed", Value = 2 }, new PerspectiveScope(), false,
      _meta(eventId: "019f0001-0000-7aaa-8000-000000000001"), PerspectiveRowVersion.Absent);
    var read = await store.ReadForApplyAsync(id);

    await store.UpsertAsync(id, new VersionedItem { Id = id, State = "Running", Value = 1 }, new PerspectiveScope(), false,
      _meta(eventId: "019f0000-0000-7aaa-8000-000000000001"), read.Version);

    await using var verify = CreateDbContext();
    var row = await verify.Set<PerspectiveRow<VersionedItem>>().AsNoTracking().SingleAsync(r => r.Id == id);
    await Assert.That(row.Data.State).IsEqualTo("Completed");
  }

  [Test]
  public async Task Upsert_OnTheCurrentVersion_WithForceUpdateScope_RewritesTheScopeAsync() {
    _usePath(true);
    var id = Guid.CreateVersion7();
    await using var context = CreateDbContext();
    var store = new EFCorePostgresPerspectiveStore<ActionTestModel>(context, TABLE);
    await store.UpsertAsync(id, new ActionTestModel { Id = id, Name = "member" }, new PerspectiveScope { TenantId = "tenant-old" },
      false, _meta(), PerspectiveRowVersion.Absent);
    var read = await store.ReadForApplyAsync(id);

    await store.UpsertAsync(id, new ActionTestModel { Id = id, Name = "member" }, new PerspectiveScope { TenantId = "tenant-new" },
      true, _meta(), read.Version);
    await store.UpsertAsync(id, new ActionTestModel { Id = id, Name = "member" }, new PerspectiveScope { TenantId = "tenant-ignored" },
      false, _meta(), (await store.ReadForApplyAsync(id)).Version);

    var row = await _rowAsync(id);
    await Assert.That(row.Scope.TenantId).IsEqualTo("tenant-new")
      .Because("the conditional write rewrites scope only when asked to, exactly like the unconditional one");
  }

  [Test]
  public async Task Upsert_WithACheckedVersion_OnANonPostgresProvider_IsNotCheckedAsync() {
    var options = new DbContextOptionsBuilder<InMemoryRowContext>()
      .UseInMemoryDatabase($"row-version-{Guid.NewGuid():N}")
      .Options;
    await using var context = new InMemoryRowContext(options);
    var store = new EFCorePostgresPerspectiveStore<ActionTestModel>(context, TABLE);
    var id = Guid.CreateVersion7();

    var read = await store.ReadForApplyAsync(id);
    await store.UpsertAsync(id, new ActionTestModel { Id = id, Name = "in-memory" }, new PerspectiveScope(), false, _meta(),
      PerspectiveRowVersion.Of(12345));

    await Assert.That(read).IsSameReferenceAs(PerspectiveApplyRead.Unchecked)
      .Because("a provider without row versions reports unchecked, so the runner keeps its old read order");
    var row = await context.Set<PerspectiveRow<ActionTestModel>>().AsNoTracking().SingleAsync(r => r.Id == id);
    await Assert.That(row.Data.Name).IsEqualTo("in-memory")
      .Because("there is no version to compare against, so a version passed in is ignored rather than failing the write");
  }

  [Test]
  public async Task Upsert_OnTheFallbackPath_InsideAnAmbientTransaction_ChecksWithinItAsync() {
    _usePath(false);
    var id = Guid.CreateVersion7();
    await _seedAsync(id, "member", 1);
    await using var context = CreateDbContext();
    var store = new EFCorePostgresPerspectiveStore<ActionTestModel>(context, TABLE);

    await using (var transaction = await context.Database.BeginTransactionAsync()) {
      var read = await store.ReadForApplyAsync(id);
      await store.UpsertAsync(id, new ActionTestModel { Id = id, Name = "member", Value = 2 }, new PerspectiveScope(), false,
        _meta(), read.Version);
      await Assert.That(async () => await store.UpsertAsync(id, new ActionTestModel { Id = id, Name = "member", Value = 3 },
          new PerspectiveScope(), false, _meta(), read.Version))
        .Throws<PerspectiveRowConflictException>()
        .Because("the first write moved the row inside the transaction, so the version read before it is stale");
      await transaction.CommitAsync();
    }

    await Assert.That((await _rowAsync(id)).Data.Value).IsEqualTo(2);
  }

  // ── Helpers ────────────────────────────────────────────────────────────────────────────────────

  private static PerspectiveMetadata _meta(long? commitSequence = null, string? eventId = null) => new() {
    EventType = "ActionTestUpdatedEvent",
    EventId = eventId ?? Guid.CreateVersion7().ToString("D"),
    Timestamp = DateTime.UtcNow,
    CommitSequence = commitSequence,
  };

  private async Task _seedAsync(Guid id, string name, int value) {
    await using var context = CreateDbContext();
    var store = new EFCorePostgresPerspectiveStore<ActionTestModel>(context, TABLE);
    await _runner(store).RunWithEventsAsync(id, TABLE, null,
      [_envelope(new ActionTestCreatedEvent { StreamId = id, Name = name, Value = value })], CancellationToken.None);
  }

  private async Task<PerspectiveRow<ActionTestModel>> _rowAsync(Guid id) {
    await using var context = CreateDbContext();
    return await context.Set<PerspectiveRow<ActionTestModel>>().AsNoTracking().SingleAsync(r => r.Id == id);
  }

  /// <summary>
  /// The real collective write path, committed on its own context. The hook plan deliberately does not
  /// bump the version column: the guard must not depend on a collective remembering to. The cohort is
  /// selected by the document's own id: the predicate compiler binds a row-id comparison as text, which the
  /// uuid column refuses (a separate defect, out of scope here).
  /// </summary>
  private async Task _commitCollectiveAsync(Guid id, string name) {
    await using var context = CreateDbContext();
    var plan = new CollectiveApplyHookPlan<ActionTestModel>(
      [], new HashSet<string>(StringComparer.Ordinal), [], BumpVersion: false, [], ReplaceWhere: null);
    await EFCoreCollectiveAdapter<ActionTestModel>.ExecuteAsync(
      context, new RenameSpec(s => s.SetProperty(m => m.Name, name)), r => r.Data.Id == id, plan,
      CollectiveApplyOptions.Default, "tenant:row-version", Guid.CreateVersion7());
  }

  /// <summary>A non-Postgres context: the store must degrade to unchecked rather than issue Postgres SQL.</summary>
  private sealed class InMemoryRowContext(DbContextOptions<InMemoryRowContext> options) : DbContext(options) {
    protected override void OnModelCreating(ModelBuilder modelBuilder) {
      base.OnModelCreating(modelBuilder);
      modelBuilder.Entity<PerspectiveRow<ActionTestModel>>(entity => {
        entity.ToTable(TABLE);
        entity.HasKey(e => e.Id);
        entity.OwnsOne(e => e.Data, d => d.WithOwner());
        entity.OwnsOne(e => e.Metadata, m => m.WithOwner());
        entity.Property(e => e.Scope)
          .HasConversion(
            v => System.Text.Json.JsonSerializer.Serialize(v, System.Text.Json.JsonSerializerOptions.Default),
            v => System.Text.Json.JsonSerializer.Deserialize<PerspectiveScope>(v, System.Text.Json.JsonSerializerOptions.Default)!);
      });
    }
  }

  private sealed record RenameSpec(
      Expression<Action<ICollectiveSetters<ActionTestModel>>> Setters,
      Expression<Func<PerspectiveRow<ActionTestModel>, bool>>? Where = null) : ICollectiveSpec<ActionTestModel>;

  private static IPerspectiveRunner _runner(IPerspectiveStore<ActionTestModel> store) {
    var services = new ServiceCollection();
    services.TryAddWhizbangDefaults();
    services.AddTransient<ActionTestPerspective>();
    services.AddLogging();
    var provider = services.BuildServiceProvider();
    var runnerType = typeof(PerspectiveRowVersionIntegrationTests).Assembly.GetTypes()
      .Single(t => t.Name == "ActionTestPerspectiveRunner");
    var logger = typeof(LoggerFactoryExtensions).GetMethods()
      .Single(m => m.Name == "CreateLogger" && m.IsGenericMethod)
      .MakeGenericMethod(runnerType)
      .Invoke(null, [provider.GetRequiredService<ILoggerFactory>()])!;
    return (IPerspectiveRunner)runnerType.GetConstructors().Single().Invoke([
      provider, logger, new InMemoryEventStore(), store, provider.GetRequiredService<IServiceScopeFactory>(),
      null, null, null, null, null]);
  }

  private static MessageEnvelope<IEvent> _envelope(IEvent payload) => new() {
    MessageId = MessageId.New(),
    Payload = payload,
    DispatchContext = new MessageDispatchContext { Mode = DispatchModes.Local, Source = MessageSource.Local },
    Hops = [],
  };

  /// <summary>
  /// Forwards to the real store and runs <see cref="BeforeWrite"/> after the runner's read and before
  /// its write: the deterministic seam that commits a collective into exactly the window the issue
  /// describes.
  /// </summary>
  private sealed class InterleavingStore(IPerspectiveStore<ActionTestModel> inner) : IPerspectiveStore<ActionTestModel> {
    public Func<int, Task>? BeforeWrite { get; init; }
    public int ReadsForApply { get; private set; }
    public int WriteAttempts { get; private set; }

    public Task<PerspectiveApplyRead> ReadForApplyAsync(Guid streamId, CancellationToken cancellationToken = default) {
      ReadsForApply++;
      return inner.ReadForApplyAsync(streamId, cancellationToken);
    }

    public Task<ActionTestModel?> GetByStreamIdAsync(Guid streamId, CancellationToken cancellationToken = default) =>
      inner.GetByStreamIdAsync(streamId, cancellationToken);

    public Task<PerspectiveMetadata?> GetMetadataByStreamIdAsync(Guid streamId, CancellationToken cancellationToken = default) =>
      inner.GetMetadataByStreamIdAsync(streamId, cancellationToken);

    public async Task UpsertAsync(Guid streamId, ActionTestModel model, PerspectiveScope scope, bool forceUpdateScope,
        PerspectiveMetadata metadata, PerspectiveRowVersion expectedVersion, CancellationToken cancellationToken = default) {
      WriteAttempts++;
      if (BeforeWrite is not null) {
        await BeforeWrite(WriteAttempts);
      }
      await inner.UpsertAsync(streamId, model, scope, forceUpdateScope, metadata, expectedVersion, cancellationToken);
    }

    public Task UpsertAsync(Guid streamId, ActionTestModel model, CancellationToken cancellationToken = default) =>
      inner.UpsertAsync(streamId, model, cancellationToken);

    public Task UpsertWithPhysicalFieldsAsync(Guid streamId, ActionTestModel model, IDictionary<string, object?> physicalFieldValues,
        PerspectiveScope? scope = null, CancellationToken cancellationToken = default) =>
      inner.UpsertWithPhysicalFieldsAsync(streamId, model, physicalFieldValues, scope, cancellationToken);

    public Task<ActionTestModel?> GetByPartitionKeyAsync<TPartitionKey>(TPartitionKey partitionKey, CancellationToken cancellationToken = default)
      where TPartitionKey : notnull => inner.GetByPartitionKeyAsync(partitionKey, cancellationToken);

    public Task UpsertByPartitionKeyAsync<TPartitionKey>(TPartitionKey partitionKey, ActionTestModel model, CancellationToken cancellationToken = default)
      where TPartitionKey : notnull => inner.UpsertByPartitionKeyAsync(partitionKey, model, cancellationToken);

    public Task FlushAsync(CancellationToken cancellationToken = default) => inner.FlushAsync(cancellationToken);
    public Task PurgeAsync(Guid streamId, CancellationToken cancellationToken = default) => inner.PurgeAsync(streamId, cancellationToken);
    public Task PurgeByPartitionKeyAsync<TPartitionKey>(TPartitionKey partitionKey, CancellationToken cancellationToken = default)
      where TPartitionKey : notnull => inner.PurgeByPartitionKeyAsync(partitionKey, cancellationToken);
  }
}
