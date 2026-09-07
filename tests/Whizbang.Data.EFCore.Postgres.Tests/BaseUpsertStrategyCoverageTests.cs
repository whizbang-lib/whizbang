using System.Reflection;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core;
using Whizbang.Core.Lenses;
using Whizbang.Core.Perspectives;
using Whizbang.Core.Perspectives.Hooks;
using Whizbang.Core.ValueObjects;
using Whizbang.Data.EFCore.Postgres;
using Whizbang.Data.Postgres;

namespace Whizbang.Data.EFCore.Postgres.Tests;

/// <summary>
/// Top-level model + perspective stub used only by <see cref="BaseUpsertStrategyCoverageIntegrationTests"/>'s
/// atomic-path physical-field coverage. Dedicated (never referenced by any other test) so its
/// <see cref="PerspectiveTtlRegistry"/> registration can't leak into unrelated suites — mirrors the existing
/// ProductPhysicalModel.cs pattern: MessageJsonContextGenerator only emits JsonTypeInfo for a model that some
/// <see cref="IPerspectiveFor{TModel, TEvent1}"/> stub references.
/// </summary>
public class UpsertCoverageWidgetModel {
  [StreamId]
  public required Guid Id { get; init; }
  public string Name { get; init; } = string.Empty;
}

/// <summary>Sample event for <see cref="UpsertCoverageWidgetPerspective"/>. Purely a discovery hook.</summary>
public record UpsertCoverageWidgetCreatedEvent : IEvent {
  [StreamId]
  public required Guid Id { get; init; }
  public required string Name { get; init; }
}

/// <summary>
/// Stub perspective binding <see cref="UpsertCoverageWidgetModel"/> to the discovery surface so
/// MessageJsonContextGenerator emits JsonTypeInfo for it — Path 1 needs that JsonTypeInfo to serialize the
/// row's <c>data</c> JSONB column. The perspective itself never actually runs in test setup.
/// </summary>
public class UpsertCoverageWidgetPerspective : IPerspectiveFor<UpsertCoverageWidgetModel, UpsertCoverageWidgetCreatedEvent> {
  public UpsertCoverageWidgetModel Apply(UpsertCoverageWidgetModel currentData, UpsertCoverageWidgetCreatedEvent @event) =>
    new() { Id = @event.Id, Name = @event.Name };

  public Task Update(UpsertCoverageWidgetCreatedEvent @event, CancellationToken cancellationToken = default) =>
    Task.CompletedTask;
}

/// <summary>
/// Pure-logic coverage for <see cref="BaseUpsertStrategy"/> branches that need only fakes: the per-event
/// SetProperty hook path, and the duplicate-key exception classifier's non-matching fall-through. No database.
/// </summary>
/// <code-under-test>src/Whizbang.Data.EFCore.Postgres/BaseUpsertStrategy.cs</code-under-test>
[Category("Shard1")]
public class BaseUpsertStrategyCoverageTests {

  private sealed class HookableModel {
    public string Status { get; set; } = string.Empty;
  }

  private sealed class HookableDbContext(DbContextOptions<HookableDbContext> options) : DbContext(options) {
    protected override void OnModelCreating(ModelBuilder modelBuilder) {
      base.OnModelCreating(modelBuilder);
      modelBuilder.Entity<PerspectiveRow<HookableModel>>(entity => {
        entity.ToTable("wh_per_hookable_model");
        entity.HasKey(e => e.Id);
        entity.Property<DateTime?>("sys_created_at").HasColumnName("sys_created_at");
        entity.Property<DateTime?>("sys_updated_at").HasColumnName("sys_updated_at");
        entity.OwnsOne(e => e.Data, d => d.WithOwner());
        entity.OwnsOne(e => e.Metadata, m => {
          m.WithOwner();
          m.Property(x => x.EventType).IsRequired();
          m.Property(x => x.EventId).IsRequired();
          m.Property(x => x.Timestamp).IsRequired();
        });
        entity.Property(e => e.Scope)
          .HasConversion(
            v => JsonSerializer.Serialize(v, JsonSerializerOptions.Default),
            v => JsonSerializer.Deserialize<PerspectiveScope>(v, JsonSerializerOptions.Default)!);
      });
    }
  }

  private sealed class RecordingSetPropertyHook(Action<IApplyHookBuilder<HookableModel>, ApplyHookContext> body)
      : IApplyHook<HookableModel> {
    public void Configure(IApplyHookBuilder<HookableModel> builder, ApplyHookContext context) => body(builder, context);
  }

  // A registered per-event SetProperty hook (e.g. status classification, redaction, a derived field) that
  // stops reaching the loaded model would fail silently: the row keeps looking like whatever the caller
  // passed in, the hook is still registered and reports nothing wrong, and every downstream reader trusts a
  // field that was never actually stamped.
  [Test]
  public async Task Upsert_WithRegisteredSetPropertyHook_MutatesTheModelBeforePersistingAsync() {
    var previousRegistry = PerEventApplyHooks.Registry;
    try {
      PerEventApplyHooks.Registry = WhizbangApplyHooks.CreatePerEventWithDefaults()
        .Register<HookableModel>(new RecordingSetPropertyHook((b, _) => b.SetProperty(m => m.Status, "HookApplied")));

      var options = new DbContextOptionsBuilder<HookableDbContext>()
        .UseInMemoryDatabase($"hookable-{Guid.NewGuid()}")
        .Options;
      await using var context = new HookableDbContext(options);
      var strategy = new InMemoryUpsertStrategy();
      var testId = Guid.NewGuid();

      await strategy.UpsertPerspectiveRowAsync(
        context,
        "wh_per_hookable_model",
        testId,
        new HookableModel { Status = "Original" },
        new PerspectiveMetadata { EventType = "Test", EventId = Guid.NewGuid().ToString(), Timestamp = DateTime.UtcNow },
        new PerspectiveScope());

      var row = await context.Set<PerspectiveRow<HookableModel>>().FirstOrDefaultAsync(r => r.Id == testId);

      await Assert.That(row).IsNotNull();
      await Assert.That(row!.Data.Status).IsEqualTo("HookApplied")
        .Because("a registered per-event SetProperty hook must mutate the loaded model before it is persisted");
    } finally {
      PerEventApplyHooks.Registry = previousRegistry;
    }
  }

  // If the exception classifier ever returned true for a DbUpdateException whose cause is NOT a Postgres
  // 23505 duplicate key, the slice-19 retry loop would silently swallow up to three retries of a failure that
  // could never succeed (a different constraint, a validation error), burning round-trips and masking the
  // real cause behind "duplicate key retry" telemetry instead of surfacing it to the caller's failure channel.
  [Test]
  public async Task IsDuplicateKeyException_WithNonPostgresInnerExceptionChain_ReturnsFalseAsync() {
    var method = typeof(BaseUpsertStrategy).GetMethod("_isDuplicateKeyException", BindingFlags.NonPublic | BindingFlags.Static);
    await Assert.That(method).IsNotNull()
      .Because("this test targets BaseUpsertStrategy's private duplicate-key classifier by exact name");

    var innermost = new InvalidOperationException("root cause, not a duplicate key");
    var middle = new InvalidOperationException("wrapping", innermost);
    var dbUpdateException = new DbUpdateException("save failed", middle);

    var result = (bool)method!.Invoke(null, [dbUpdateException])!;

    await Assert.That(result).IsFalse()
      .Because("walking a multi-level inner-exception chain that never contains a Postgres 23505 must fall "
             + "through to false rather than being mistaken for a recoverable TOCTOU race");
  }
}

/// <summary>
/// Live-Postgres coverage for <see cref="BaseUpsertStrategy"/> branches that need a real Npgsql connection:
/// the atomic path's table-name and physical-field guards, its ambient-transaction plumbing, its TTL
/// expires_at parameter, its graceful fallback on an unbindable CLR type, and the physical-fields overload's
/// forceUpdateScope forwarding.
/// </summary>
/// <remarks>
/// Mutates the process-wide <see cref="BaseUpsertStrategy.PathOnePersistenceOptionsProvider"/> and
/// <see cref="PerspectiveTtlRegistry"/>, so it joins the "EFCorePostgresTests" group with every other suite
/// that flips those statics.
/// </remarks>
/// <code-under-test>src/Whizbang.Data.EFCore.Postgres/BaseUpsertStrategy.cs</code-under-test>
[NotInParallel("EFCorePostgresTests")]
[Category("Shard1")]
public class BaseUpsertStrategyCoverageIntegrationTests : EFCoreTestBase {
  private const string WIDGET_TABLE = "wh_per_upsert_coverage_widget";

  [After(Test)]
  public Task ResetProcessWideStateAsync() {
    BaseUpsertStrategy.PathOnePersistenceOptionsProvider = null;
    PerspectiveTtlRegistry.Register(typeof(UpsertCoverageWidgetModel), -1);
    return Task.CompletedTask;
  }

  private static void _enablePathOne() =>
    BaseUpsertStrategy.PathOnePersistenceOptionsProvider = () =>
      Generated.PerspectivePersistenceJsonContext.CreateOptions(
        Generated.MessageJsonContext.Default,
        global::Whizbang.Core.Generated.InfrastructureJsonContext.Default);

  // Raw DDL rather than EnsureCreatedAsync(): EFCoreTestBase already created this test's database via its
  // own raw-SQL schema script, so EnsureCreatedAsync (a database-existence check, not a per-table one) would
  // no-op and never create this table.
  private async Task _createWidgetTableAsync() {
    await using var conn = new NpgsqlConnection(ConnectionString);
    await conn.OpenAsync();
    await using var cmd = new NpgsqlCommand($@"
      DROP TABLE IF EXISTS {WIDGET_TABLE};
      CREATE TABLE {WIDGET_TABLE} (
        id UUID NOT NULL PRIMARY KEY,
        data JSONB NOT NULL, metadata JSONB NOT NULL, scope JSONB NOT NULL,
        created_at TIMESTAMPTZ NOT NULL, updated_at TIMESTAMPTZ NOT NULL,
        sys_created_at TIMESTAMPTZ, sys_updated_at TIMESTAMPTZ,
        expires_at TIMESTAMPTZ, version INTEGER NOT NULL,
        upsert_coverage_bad_key TEXT, ref_id UUID);", conn);
    await cmd.ExecuteNonQueryAsync();
  }

  private UpsertCoverageWidgetDbContext _createWidgetDbContext() =>
    new(new DbContextOptionsBuilder<UpsertCoverageWidgetDbContext>().UseNpgsql(ConnectionString).Options);

  private sealed class UpsertCoverageWidgetDbContext(DbContextOptions<UpsertCoverageWidgetDbContext> options) : DbContext(options) {
    protected override void ConfigureConventions(ModelConfigurationBuilder configurationBuilder) {
      base.ConfigureConventions(configurationBuilder);
      configurationBuilder.UseTrackedGuidConversion();
    }

    protected override void OnModelCreating(ModelBuilder modelBuilder) {
      base.OnModelCreating(modelBuilder);
      modelBuilder.Entity<PerspectiveRow<UpsertCoverageWidgetModel>>(entity => {
        entity.ToTable(WIDGET_TABLE);
        entity.HasKey(e => e.Id);
        entity.Property(e => e.Id).HasColumnName("id");
        entity.ComplexProperty(e => e.Data, d => d.ToJson("data"));
        entity.ComplexProperty(e => e.Metadata, m => m.ToJson("metadata"));
        entity.ComplexProperty(e => e.Scope, s => {
          s.ToJson("scope");
          s.ComplexCollection(p => p.Extensions, ex => ex.HasJsonPropertyName("ex"));
        });
        entity.Property(e => e.CreatedAt).HasColumnName("created_at").IsRequired();
        entity.Property(e => e.UpdatedAt).HasColumnName("updated_at").IsRequired();
        entity.Property(e => e.Version).HasColumnName("version").IsRequired();
        entity.Property<DateTime?>("expires_at").HasColumnName("expires_at");
        entity.Property<DateTime?>("sys_created_at").HasColumnName("sys_created_at");
        entity.Property<DateTime?>("sys_updated_at").HasColumnName("sys_updated_at");
        // Deliberately mismatched EF property name vs. column name: the DICTIONARY KEY / atomic-path
        // identifier check operates on the EF shadow-property name, never the physical column.
        entity.Property<string?>("bad-key").HasColumnName("upsert_coverage_bad_key");
        entity.Property<TrackedGuid>("ref_id").HasColumnName("ref_id");
      });
    }
  }

  // If the physical-fields overload stopped forwarding forceUpdateScope through to the shared core, an
  // IScopeEvent-driven re-scoping applied through this overload would silently keep serving the OLD
  // tenant/user scope after a legitimate change — an authorization-boundary bug disguised as a successful
  // write.
  [Test]
  public async Task UpsertPerspectiveRowWithPhysicalFieldsAsync_WithForceUpdateScopeTrue_UpdatesScopeColumnAsync() {
    var strategy = new PostgresUpsertStrategy();
    var testId = Guid.CreateVersion7();
    var metadata = new PerspectiveMetadata {
      EventType = "OrderRescoped",
      EventId = Guid.NewGuid().ToString(),
      Timestamp = DateTime.UtcNow
    };
    var originalScope = new PerspectiveScope { TenantId = "tenant-phys-force-old" };
    var newScope = new PerspectiveScope { TenantId = "tenant-phys-force-new" };

    await using (var context = CreateDbContext()) {
      await strategy.UpsertPerspectiveRowAsync(
        context, "wh_per_order", testId,
        new Order { OrderId = new TestOrderId(testId), Amount = 10m, Status = "Created" },
        metadata, originalScope);
    }

    await using (var context = CreateDbContext()) {
      await strategy.UpsertPerspectiveRowWithPhysicalFieldsAsync(
        context, "wh_per_order", testId,
        new Order { OrderId = new TestOrderId(testId), Amount = 20m, Status = "Rescoped" },
        metadata, newScope,
        new Dictionary<string, object?>(),
        forceUpdateScope: true);
    }

    await using var conn = new NpgsqlConnection(ConnectionString);
    await conn.OpenAsync();
    var tenantId = await conn.QuerySingleAsync<string>(
      "SELECT scope->>'t' FROM wh_per_order WHERE id = @id",
      new { id = testId });

    await Assert.That(tenantId).IsEqualTo("tenant-phys-force-new")
      .Because("forceUpdateScope on the physical-fields overload must reach the shared core exactly like the plain overload's does");
  }

  // If the atomic path's empty/null table-name guard were removed, an empty string would ride straight into
  // the raw INSERT INTO SQL text as the target table, producing either a hard SQL syntax error or (if a
  // future refactor supplied a fallback default) a write to the wrong table — instead the row must still
  // land via the EF-mapped fallback, which never consults this string.
  [Test]
  public async Task Upsert_WithAnEmptyTableName_DeclinesTheAtomicPathAndStillPersistsAsync() {
    _enablePathOne();
    await using var context = CreateDbContext();
    var strategy = new PostgresUpsertStrategy();
    var testId = Guid.CreateVersion7();

    await strategy.UpsertPerspectiveRowAsync(
      context, string.Empty, testId,
      new Order { OrderId = new TestOrderId(testId), Amount = 15m, Status = "EmptyTableNameGuard" },
      new PerspectiveMetadata { EventType = "OrderCreated", EventId = Guid.NewGuid().ToString(), Timestamp = DateTime.UtcNow },
      new PerspectiveScope());

    await using var readContext = CreateDbContext();
    var row = await readContext.Set<PerspectiveRow<Order>>().AsNoTracking().FirstOrDefaultAsync(r => r.Id == testId);

    await Assert.That(row).IsNotNull()
      .Because("declining the atomic path for an empty table name must not drop the write");
    await Assert.That(row!.Data.Status).IsEqualTo("EmptyTableNameGuard");
  }

  // If the atomic path stopped honoring an ambient EF transaction, issuing its raw command on the same
  // physical connection while a transaction is open would throw (every ADO.NET command on a transacted
  // connection must declare that transaction) — so a caller who wrapped several writes together for
  // atomicity would see the whole operation fail instead of joining the transaction as intended.
  [Test]
  public async Task Upsert_InsideAnAmbientTransaction_JoinsItAndPersistsOnCommitAsync() {
    _enablePathOne();
    await using var context = CreateDbContext();
    var strategy = new PostgresUpsertStrategy();
    var testId = Guid.CreateVersion7();

    await using var transaction = await context.Database.BeginTransactionAsync();
    await strategy.UpsertPerspectiveRowAsync(
      context, "wh_per_order", testId,
      new Order { OrderId = new TestOrderId(testId), Amount = 30m, Status = "AmbientTx" },
      new PerspectiveMetadata { EventType = "OrderCreated", EventId = Guid.NewGuid().ToString(), Timestamp = DateTime.UtcNow },
      new PerspectiveScope());
    await transaction.CommitAsync();

    await using var readContext = CreateDbContext();
    var row = await readContext.Set<PerspectiveRow<Order>>().AsNoTracking().FirstOrDefaultAsync(r => r.Id == testId);

    await Assert.That(row).IsNotNull()
      .Because("the atomic upsert must join the caller's ambient transaction rather than fail or write outside it");
    await Assert.That(row!.Version).IsEqualTo(1);
  }

  // If this guard were removed, a caller-supplied physical-field column name containing characters outside
  // the unquoted-identifier alphabet would be interpolated directly into the raw atomic-UPSERT SQL text — the
  // exact SQL-injection-shaped exposure Sonar S2077 flags on this code path — instead of falling back to the
  // EF-mapped write, which never places the caller's string into SQL.
  [Test]
  public async Task UpsertPerspectiveRowWithPhysicalFieldsAsync_WithAMalformedPhysicalFieldKey_DeclinesTheAtomicPathAndStillPersistsAsync() {
    _enablePathOne();
    await _createWidgetTableAsync();
    await using var context = _createWidgetDbContext();
    var strategy = new PostgresUpsertStrategy();
    var testId = Guid.CreateVersion7();

    await strategy.UpsertPerspectiveRowWithPhysicalFieldsAsync(
      context, WIDGET_TABLE, testId,
      new UpsertCoverageWidgetModel { Id = testId, Name = "Guarded" },
      new PerspectiveMetadata { EventType = "WidgetCreated", EventId = Guid.NewGuid().ToString(), Timestamp = DateTime.UtcNow },
      new PerspectiveScope(),
      new Dictionary<string, object?> { ["bad-key"] = "irrelevant" });

    await using var readContext = _createWidgetDbContext();
    var row = await readContext.Set<PerspectiveRow<UpsertCoverageWidgetModel>>().AsNoTracking().FirstOrDefaultAsync(r => r.Id == testId);

    await Assert.That(row).IsNotNull()
      .Because("declining the atomic path for a malformed physical-field key must not drop the write");
    await Assert.That(row!.Data.Name).IsEqualTo("Guarded");
  }

  // If the atomic path stopped binding the expires_at parameter for a TTL-registered model, a TtlRow
  // perspective upserted through the (now-default) atomic path would never get its sliding expiry stamped —
  // the row would live forever even though the lens-visibility filter and reaper both expect it to age out,
  // defeating the perspective's retention contract while the write itself still "succeeds".
  [Test]
  public async Task Upsert_ForATtlRegisteredModel_StampsExpiresAtViaTheAtomicPathAsync() {
    PerspectiveTtlRegistry.Register(typeof(UpsertCoverageWidgetModel), 3600);
    _enablePathOne();
    await _createWidgetTableAsync();
    await using var context = _createWidgetDbContext();
    var strategy = new PostgresUpsertStrategy();
    var testId = Guid.CreateVersion7();

    await strategy.UpsertPerspectiveRowAsync(
      context, WIDGET_TABLE, testId,
      new UpsertCoverageWidgetModel { Id = testId, Name = "Ttl" },
      new PerspectiveMetadata { EventType = "WidgetCreated", EventId = Guid.NewGuid().ToString(), Timestamp = DateTime.UtcNow },
      new PerspectiveScope());

    await using var conn = new NpgsqlConnection(ConnectionString);
    await conn.OpenAsync();
    var expiresAt = await conn.QuerySingleAsync<DateTime>(
      $"SELECT expires_at FROM {WIDGET_TABLE} WHERE id = @id",
      new { id = testId });

    var expected = DateTime.UtcNow.AddSeconds(3600);
    await Assert.That(Math.Abs((expiresAt - expected).TotalSeconds)).IsLessThanOrEqualTo(60)
      .Because("expires_at = now + the registered TTL must ride through the atomic path's own INSERT, not only the legacy fallback");
  }

  // If this catch were removed, a physical-field value whose CLR type the atomic path's raw parameter
  // binding cannot map (no NpgsqlDbType, no native handler registered for it) would blow up the whole
  // upsert instead of deferring to the EF-mapped write, whose own value converter knows how to store it —
  // turning a routine CLR/column mismatch into an apply failure that stalls the perspective's checkpoint.
  [Test]
  public async Task UpsertPerspectiveRowWithPhysicalFieldsAsync_WithAnUnbindableClrType_FallsBackToTheLegacyPathAndPersistsAsync() {
    _enablePathOne();
    await _createWidgetTableAsync();
    await using var context = _createWidgetDbContext();
    var strategy = new PostgresUpsertStrategy();
    var testId = Guid.CreateVersion7();
    var refId = TrackedGuid.NewMedo();

    await strategy.UpsertPerspectiveRowWithPhysicalFieldsAsync(
      context, WIDGET_TABLE, testId,
      new UpsertCoverageWidgetModel { Id = testId, Name = "Fallback" },
      new PerspectiveMetadata { EventType = "WidgetCreated", EventId = Guid.NewGuid().ToString(), Timestamp = DateTime.UtcNow },
      new PerspectiveScope(),
      new Dictionary<string, object?> { ["ref_id"] = refId });

    await using var conn = new NpgsqlConnection(ConnectionString);
    await conn.OpenAsync();
    var storedRefId = await conn.ExecuteScalarAsync<Guid>(
      $"SELECT ref_id FROM {WIDGET_TABLE} WHERE id = @id",
      new { id = testId });

    await Assert.That(storedRefId).IsEqualTo(refId.Value)
      .Because("the legacy fallback's own value converter must still land the value the atomic path's raw binding rejected");
  }
}
