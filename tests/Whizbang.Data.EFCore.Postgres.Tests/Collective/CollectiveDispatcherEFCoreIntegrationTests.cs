#pragma warning disable CA1707
#pragma warning disable CA1859 // tests assert against the interface return type

using System.Linq.Expressions;
using System.Text.Json;
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
using Whizbang.Testing.Containers;

namespace Whizbang.Data.EFCore.Postgres.Tests.Collective;

/// <summary>
/// End-to-end integration tests for the scope-only
/// <see cref="CollectiveDispatcher"/> against a real Postgres
/// testcontainer. The dispatched event carries ONLY a scope; the SQL
/// UPDATE's <c>WHERE</c> is exactly the resolver's <c>ScopeFilter</c>
/// — no captured matched-id set, no audit-pointer column write.
/// </summary>
/// <remarks>
/// <para>
/// This file pins the central invariants of the scope-level
/// determinism design:
/// </para>
/// <list type="bullet">
///   <item><description>The resolver's scope filter is the SOLE predicate (tenant B rows are entirely untouched on a tenant-A event).</description></item>
///   <item><description>Predicate re-evaluates against the current projection state at apply time — rows materialized between original "what the producer saw" and apply time ARE included (the canonical late-arrival case the 11-stream replay scenario protects).</description></item>
/// </list>
/// </remarks>
/// <docs>fundamentals/messaging/collective-events</docs>
[Category("Integration")]
[Category("CollectiveEvents")]
[NotInParallel("PostgreSQL")]
public class CollectiveDispatcherEFCoreIntegrationTests : IAsyncDisposable {
  static CollectiveDispatcherEFCoreIntegrationTests() {
    AppContext.SetSwitch("Npgsql.EnableLegacyTimestampBehavior", false);
  }

  private string? _testDatabaseName;
  private NpgsqlDataSource? _dataSource;
  private _jobDbContext? _ctx;
  private string _connectionString = null!;

  // ── Scope-only WHERE: tenant filter restricts to scope ────────────────

  [Test]
  public async Task DispatchAsync_TenantScoped_AffectsAllRowsInScopeOnlyAsync() {
    // Seed: two tenants × two jobs each = four rows.
    var jobA1 = Guid.NewGuid();
    var jobA2 = Guid.NewGuid();
    var jobB1 = Guid.NewGuid();
    var jobB2 = Guid.NewGuid();

    await _seedJobAsync(jobA1, tenantId: "t-A", status: "Active");
    await _seedJobAsync(jobA2, tenantId: "t-A", status: "Active");
    await _seedJobAsync(jobB1, tenantId: "t-B", status: "Active");
    await _seedJobAsync(jobB2, tenantId: "t-B", status: "Active");

    var result = await _buildDispatcher().DispatchAsync(
      evt: new _archiveJobsCollectiveEvent {
        Scope = new TenantCollectiveScope("t-A"),
        OccurredAt = DateTimeOffset.UtcNow,
      },
      collectiveEventId: Guid.NewGuid(),
      dbContextOrSession: _ctx!,
      cancellationToken: default);

    await Assert.That(result.HandlerCount).IsEqualTo(1);
    await Assert.That(result.AffectedRowCount).IsEqualTo(2)
      .Because("Tenant A has two rows; both must be archived. The scope predicate is the SOLE filter — the event carries no subset enumeration.");

    await Assert.That(await _readStatusAsync(jobA1)).IsEqualTo("Archived");
    await Assert.That(await _readStatusAsync(jobA2)).IsEqualTo("Archived");
    await Assert.That(await _readStatusAsync(jobB1)).IsEqualTo("Active")
      .Because("Tenant B rows must be entirely untouched — the resolver's scope filter restricts by row.Scope.TenantId.");
    await Assert.That(await _readStatusAsync(jobB2)).IsEqualTo("Active");
  }

  // ── Predicate re-evaluates at apply time (no snapshot) ────────────────

  [Test]
  public async Task DispatchAsync_RowsMaterializedAfterEventEmitted_AreIncludedAsync() {
    // Models the canonical scope-determinism case: a stream's CREATE
    // event was emitted earlier in the event sequence but didn't
    // materialize in the projection until AFTER the producer "would have
    // seen" the matched set. Replay re-orders to log order; the
    // collective event applies AFTER the create event; the late
    // materialization is included.
    //
    // We simulate the apply-time projection state by seeding the
    // late-materialized rows BEFORE calling DispatchAsync. The
    // collective event has no captured set — it sees the projection
    // state at dispatch time, period.

    // "Original" set: rows the producer saw at write time.
    var earlyA = Guid.NewGuid();
    var earlyB = Guid.NewGuid();
    await _seedJobAsync(earlyA, tenantId: "t-late", status: "Active");
    await _seedJobAsync(earlyB, tenantId: "t-late", status: "Active");

    // "Late-arriving" rows: these would have been missed by a snapshot-
    // determinism model (their stream events were emitted earlier in the
    // log but their projection materialization came later in real time).
    // In the scope-determinism model, replay puts events in log order,
    // so by the time the collective applies, these rows EXIST and the
    // predicate covers them.
    var late1 = Guid.NewGuid();
    var late2 = Guid.NewGuid();
    var late3 = Guid.NewGuid();
    await _seedJobAsync(late1, tenantId: "t-late", status: "Active");
    await _seedJobAsync(late2, tenantId: "t-late", status: "Active");
    await _seedJobAsync(late3, tenantId: "t-late", status: "Active");

    var result = await _buildDispatcher().DispatchAsync(
      evt: new _archiveJobsCollectiveEvent {
        Scope = new TenantCollectiveScope("t-late"),
        OccurredAt = DateTimeOffset.UtcNow,
      },
      collectiveEventId: Guid.NewGuid(),
      dbContextOrSession: _ctx!,
      cancellationToken: default);

    await Assert.That(result.AffectedRowCount).IsEqualTo(5)
      .Because("Scope-level determinism: the predicate evaluates against current projection state. The five rows visible at apply time are all affected, including the three that materialized after the original write-time enumeration. This IS the 11-stream replay invariant in miniature.");

    foreach (var id in new[] { earlyA, earlyB, late1, late2, late3 }) {
      await Assert.That(await _readStatusAsync(id)).IsEqualTo("Archived")
        .Because("Every tenant-A row at apply time gets archived, regardless of when it materialized relative to the producer's wall-clock view.");
    }
  }

  // ── Scope with no rows: 0 affected, no error ──────────────────────────

  [Test]
  public async Task DispatchAsync_ScopeWithNoRows_AffectsZeroRowsAsync() {
    var result = await _buildDispatcher().DispatchAsync(
      evt: new _archiveJobsCollectiveEvent {
        Scope = new TenantCollectiveScope("t-empty"),
        OccurredAt = DateTimeOffset.UtcNow,
      },
      collectiveEventId: Guid.NewGuid(),
      dbContextOrSession: _ctx!,
      cancellationToken: default);

    await Assert.That(result.HandlerCount).IsEqualTo(1)
      .Because("The handler still fires — zero affected rows is a valid outcome, not an absent subscriber.");
    await Assert.That(result.AffectedRowCount).IsEqualTo(0);
  }

  // ── Per-model Where projection: Framework refines within scope ────────

  [Test]
  public async Task DispatchAsync_FrameworkWithHandlerWhere_RefinesWithinScopeAsync() {
    // The handler projects the cohort onto its own column (Status == "Draft") via spec.Where. Under the
    // Framework default, the resolver's tenant envelope is AND-ed with it: only tenant-A Draft rows mutate.
    var draftA = Guid.NewGuid();
    var approvedA = Guid.NewGuid();
    var draftB = Guid.NewGuid();

    await _seedJobAsync(draftA, tenantId: "t-A", status: "Draft");
    await _seedJobAsync(approvedA, tenantId: "t-A", status: "Approved");
    await _seedJobAsync(draftB, tenantId: "t-B", status: "Draft");

    var result = await _buildDraftDispatcher(CollectiveScopeHandling.Framework).DispatchAsync(
      evt: new _archiveJobsCollectiveEvent {
        Scope = new TenantCollectiveScope("t-A"),
        OccurredAt = DateTimeOffset.UtcNow,
      },
      collectiveEventId: Guid.NewGuid(),
      dbContextOrSession: _ctx!,
      cancellationToken: default);

    await Assert.That(result.AffectedRowCount).IsEqualTo(1)
      .Because("Framework AND-composes the tenant envelope with the handler's Status=='Draft' projection — only tenant-A's single Draft row qualifies.");
    await Assert.That(await _readStatusAsync(draftA)).IsEqualTo("Archived");
    await Assert.That(await _readStatusAsync(approvedA)).IsEqualTo("Approved")
      .Because("The handler Where refines within the scope — an Approved row in the same tenant falls out.");
    await Assert.That(await _readStatusAsync(draftB)).IsEqualTo("Draft")
      .Because("The scope envelope still binds — a Draft row in a different tenant is excluded.");
  }

  // ── Per-model Where projection: Custom owns the whole WHERE ────────────

  [Test]
  public async Task DispatchAsync_CustomHandlerWhere_IgnoresResolverScopeAsync() {
    // Under Custom, the resolver scope filter is not applied at all — the handler's Where is the entire
    // predicate. So a Draft row in tenant B is mutated even though the event's scope names tenant A.
    var draftA = Guid.NewGuid();
    var draftB = Guid.NewGuid();
    var approvedB = Guid.NewGuid();

    await _seedJobAsync(draftA, tenantId: "t-A", status: "Draft");
    await _seedJobAsync(draftB, tenantId: "t-B", status: "Draft");
    await _seedJobAsync(approvedB, tenantId: "t-B", status: "Approved");

    var result = await _buildDraftDispatcher(CollectiveScopeHandling.Custom).DispatchAsync(
      evt: new _archiveJobsCollectiveEvent {
        Scope = new TenantCollectiveScope("t-A"),
        OccurredAt = DateTimeOffset.UtcNow,
      },
      collectiveEventId: Guid.NewGuid(),
      dbContextOrSession: _ctx!,
      cancellationToken: default);

    await Assert.That(result.AffectedRowCount).IsEqualTo(2)
      .Because("Custom ignores the tenant envelope: every Draft row, regardless of tenant, matches the handler's sole predicate.");
    await Assert.That(await _readStatusAsync(draftA)).IsEqualTo("Archived");
    await Assert.That(await _readStatusAsync(draftB)).IsEqualTo("Archived")
      .Because("A Draft row in tenant B is mutated even though the event scope names tenant A — Custom dropped the scope envelope.");
    await Assert.That(await _readStatusAsync(approvedB)).IsEqualTo("Approved")
      .Because("The handler Where (Status=='Draft') still excludes the Approved row.");
  }

  // ── Setup / teardown / DbContext ──────────────────────────────────────

  [Before(Test)]
  public async Task SetupAsync() {
    await SharedPostgresContainer.InitializeAsync();
    _testDatabaseName = $"test_collective_{Guid.NewGuid():N}";

    await using var admin = new NpgsqlConnection(SharedPostgresContainer.ConnectionString);
    await admin.OpenAsync();
    await admin.ExecuteAsync($"CREATE DATABASE {_testDatabaseName}");

    var builder = new NpgsqlConnectionStringBuilder(SharedPostgresContainer.ConnectionString) {
      Database = _testDatabaseName,
      Timezone = "UTC",
      IncludeErrorDetail = true,
    };
    _connectionString = builder.ConnectionString;

    var dataSourceBuilder = new NpgsqlDataSourceBuilder(_connectionString);
    var jsonOptions = JsonContextRegistry.CreateCombinedOptions();
    dataSourceBuilder.ConfigureJsonOptions(jsonOptions);
    dataSourceBuilder.EnableDynamicJson();
    _dataSource = dataSourceBuilder.Build();

    _ctx = _newContext();
    await _initSchemaAsync();
  }

  [After(Test)]
  public async Task TeardownAsync() {
    if (_ctx is not null) {
      await _ctx.DisposeAsync();
      _ctx = null;
    }
    if (_dataSource is not null) {
      await _dataSource.DisposeAsync();
      _dataSource = null;
    }
    if (_testDatabaseName is not null) {
      try {
        await using var admin = new NpgsqlConnection(SharedPostgresContainer.ConnectionString);
        await admin.OpenAsync();
        await admin.ExecuteAsync($"""
          SELECT pg_terminate_backend(pid) FROM pg_stat_activity
          WHERE datname = '{_testDatabaseName}' AND pid <> pg_backend_pid();
          """);
        await admin.ExecuteAsync($"DROP DATABASE IF EXISTS {_testDatabaseName}");
      } catch {
        // best-effort cleanup
      }
      _testDatabaseName = null;
    }
  }

  public async ValueTask DisposeAsync() {
    await TeardownAsync();
    GC.SuppressFinalize(this);
  }

  internal sealed class _jobModel {
    public string Status { get; set; } = string.Empty;
    public DateTimeOffset? ArchivedAt { get; set; }
  }

  private sealed class _jobDbContext(DbContextOptions<_jobDbContext> options) : DbContext(options) {
    public DbSet<PerspectiveRow<_jobModel>> Jobs => Set<PerspectiveRow<_jobModel>>();

    protected override void OnModelCreating(ModelBuilder modelBuilder) {
      base.OnModelCreating(modelBuilder);
      modelBuilder.Entity<PerspectiveRow<_jobModel>>(e => {
        e.ToTable("wh_per_collective_job");
        e.HasKey(x => x.Id);
        e.Property(x => x.Id).HasColumnName("id");
        e.Property(x => x.Data).HasColumnName("data").HasColumnType("jsonb");
        e.Property(x => x.Metadata).HasColumnName("metadata").HasColumnType("jsonb");
        e.Property(x => x.Scope).HasColumnName("scope").HasColumnType("jsonb");
        e.Property(x => x.CreatedAt).HasColumnName("created_at");
        e.Property(x => x.UpdatedAt).HasColumnName("updated_at");
        e.Property(x => x.Version).HasColumnName("version");
      });
    }
  }

  private _jobDbContext _newContext() {
    var optionsBuilder = new DbContextOptionsBuilder<_jobDbContext>();
    optionsBuilder.UseNpgsql(_dataSource!, npg => npg.UseWhizbangFunctions())
      .ConfigureWarnings(w => w.Ignore(
        Microsoft.EntityFrameworkCore.Diagnostics.CoreEventId.ManyServiceProvidersCreatedWarning));
    return new _jobDbContext(optionsBuilder.Options);
  }

  private async Task _initSchemaAsync() {
    await using var conn = new NpgsqlConnection(_connectionString);
    await conn.OpenAsync();
    await conn.ExecuteAsync("""
      CREATE TABLE IF NOT EXISTS wh_per_collective_job (
        id UUID PRIMARY KEY,
        data JSONB NOT NULL,
        metadata JSONB NOT NULL,
        scope JSONB NOT NULL,
        created_at TIMESTAMPTZ NOT NULL,
        updated_at TIMESTAMPTZ NOT NULL,
        version INTEGER NOT NULL
      );
      """);
  }

  private async Task _seedJobAsync(Guid id, string tenantId, string status) {
    await using var conn = new NpgsqlConnection(_connectionString);
    await conn.OpenAsync();

    var dataJson = JsonSerializer.Serialize(new _jobModel { Status = status });
    var scopeJson = JsonSerializer.Serialize(new PerspectiveScope { TenantId = tenantId });

    await conn.ExecuteAsync("""
      INSERT INTO wh_per_collective_job
        (id, data, metadata, scope, created_at, updated_at, version)
      VALUES
        (@id, @data::jsonb, '{}'::jsonb, @scope::jsonb, @createdAt, @updatedAt, 1);
      """, new {
      id,
      data = dataJson,
      scope = scopeJson,
      createdAt = DateTime.UtcNow,
      updatedAt = DateTime.UtcNow,
    });
  }

  private async Task<string> _readStatusAsync(Guid id) {
    await using var conn = new NpgsqlConnection(_connectionString);
    await conn.OpenAsync();
    await using var cmd = conn.CreateCommand();
    cmd.CommandText = "SELECT data->>'Status' FROM wh_per_collective_job WHERE id = @id;";
    cmd.Parameters.AddWithValue("id", id);
    var result = await cmd.ExecuteScalarAsync();
    return (string)result!;
  }

  private CollectiveDispatcher _buildDispatcher() {
    var services = new ServiceCollection();
    var handler = new _jobPerspective();
    services.AddSingleton(handler);

    var entries = new CollectiveApplyEntry[] {
      new(
        ModelType: typeof(_jobModel),
        EventType: typeof(_archiveJobsCollectiveEvent),
        HandlerType: typeof(_jobPerspective),
        MethodName: nameof(_jobPerspective.ArchiveJobs),
        ScopeHandling: CollectiveScopeHandling.Framework,
        SpecKind: CollectiveSpecKind.Linq,
        Invoker: static (h, e) => ((_jobPerspective)h).ArchiveJobs((_archiveJobsCollectiveEvent)e)
      ),
    };

    return new CollectiveDispatcher(
      services.BuildServiceProvider(),
      entries,
      [new TenantCollectiveScopeResolver()],
      [new EFCoreCollectiveEventExecutor<_jobModel>()]);
  }

  internal sealed class _jobPerspective {
    public ICollectiveSpec<_jobModel> ArchiveJobs(_archiveJobsCollectiveEvent e) =>
      new _spec(s => s
        .SetProperty(j => j.Status, "Archived")
        .SetProperty(j => j.ArchivedAt, e.OccurredAt));

    private sealed record _spec(Expression<Action<ICollectiveSetters<_jobModel>>> Setters)
      : ICollectiveSpec<_jobModel>;
  }

  private CollectiveDispatcher _buildDraftDispatcher(CollectiveScopeHandling handling) {
    var services = new ServiceCollection();
    var handler = new _archiveDraftPerspective();
    services.AddSingleton(handler);

    var entries = new CollectiveApplyEntry[] {
      new(
        ModelType: typeof(_jobModel),
        EventType: typeof(_archiveJobsCollectiveEvent),
        HandlerType: typeof(_archiveDraftPerspective),
        MethodName: nameof(_archiveDraftPerspective.ArchiveDrafts),
        ScopeHandling: handling,
        SpecKind: CollectiveSpecKind.Linq,
        Invoker: static (h, e) => ((_archiveDraftPerspective)h).ArchiveDrafts((_archiveJobsCollectiveEvent)e)
      ),
    };

    return new CollectiveDispatcher(
      services.BuildServiceProvider(),
      entries,
      [new TenantCollectiveScopeResolver()],
      [new EFCoreCollectiveEventExecutor<_jobModel>()]);
  }

  // Projects the cohort onto its own Status column via spec.Where — the per-perspective projection capability.
  internal sealed class _archiveDraftPerspective {
    public ICollectiveSpec<_jobModel> ArchiveDrafts(_archiveJobsCollectiveEvent e) =>
      new _whereSpec(
        s => s.SetProperty(j => j.Status, "Archived").SetProperty(j => j.ArchivedAt, e.OccurredAt),
        r => r.Data.Status == "Draft");

    private sealed record _whereSpec(
        Expression<Action<ICollectiveSetters<_jobModel>>> Setters,
        Expression<Func<PerspectiveRow<_jobModel>, bool>>? Where) : ICollectiveSpec<_jobModel>;
  }

  internal sealed record _archiveJobsCollectiveEvent : ICollectiveEvent {
    public required CollectiveScope Scope { get; init; }
    public required DateTimeOffset OccurredAt { get; init; }
  }
}
