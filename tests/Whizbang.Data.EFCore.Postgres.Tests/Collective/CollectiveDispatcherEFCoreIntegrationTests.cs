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

  // ── Per-model Where projection: Custom refines the cohort but scope STILL binds (D0 fix) ────────────

  [Test]
  public async Task DispatchAsync_CustomHandlerWhere_StillHonorsTenantScopeAsync() {
    // D0 data-safety fix: perspective tables are SHARED multi-tenant. Even under Custom — where the handler
    // owns the cohort predicate — the framework MUST still AND the tenant scope envelope, or a tenant-A event
    // rewrites tenant-B rows (cross-tenant corruption). The handler declares only its cohort (Status=='Draft');
    // the framework guarantees the tenant filter.
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

    await Assert.That(result.AffectedRowCount).IsEqualTo(1)
      .Because("Custom refines the cohort (Status=='Draft') but the framework still ANDs the tenant envelope — only tenant-A's single Draft row qualifies.");
    await Assert.That(await _readStatusAsync(draftA)).IsEqualTo("Archived");
    await Assert.That(await _readStatusAsync(draftB)).IsEqualTo("Draft")
      .Because("D0 FIX: a Draft row in tenant B must be UNTOUCHED even under Custom — the scope envelope always binds on a shared multi-tenant table.");
    await Assert.That(await _readStatusAsync(approvedB)).IsEqualTo("Approved")
      .Because("The handler Where (Status=='Draft') excludes the Approved row anyway.");
  }

  // ── Cross-perspective cohort: scope by a sibling table (correlated EXISTS) ──

  [Test]
  public async Task DispatchAsync_CrossPerspectiveCohort_ScopesBySiblingTableAsync() {
    // OrderModel-style split: the cohort's status lives on a SIBLING table (same id). The handler's
    // Where uses q.Of<_jobStatusModel>().Any(...), which EF funcletizes + translates to a correlated EXISTS
    // in the ExecuteUpdate — proving cross-perspective projection end-to-end on EF Core.
    var eligible = Guid.NewGuid();   // sibling status Draft → in cohort
    var ineligible = Guid.NewGuid(); // sibling status Published → out
    var noSibling = Guid.NewGuid();  // no sibling row → out

    await _seedJobAsync(eligible, tenantId: "t-A", status: "Active");
    await _seedJobAsync(ineligible, tenantId: "t-A", status: "Active");
    await _seedJobAsync(noSibling, tenantId: "t-A", status: "Active");
    await _seedJobStatusAsync(eligible, "Draft");
    await _seedJobStatusAsync(ineligible, "Published");

    var result = await _buildCrossDispatcher().DispatchAsync(
      evt: new _archiveJobsCollectiveEvent {
        Scope = new TenantCollectiveScope("t-A"),
        OccurredAt = DateTimeOffset.UtcNow,
      },
      collectiveEventId: Guid.NewGuid(),
      dbContextOrSession: _ctx!,
      cancellationToken: default);

    await Assert.That(result.AffectedRowCount).IsEqualTo(1)
      .Because("Only the job whose sibling status is Draft is in the cohort (correlated EXISTS over the status table).");
    await Assert.That(await _readStatusAsync(eligible)).IsEqualTo("Archived");
    await Assert.That(await _readStatusAsync(ineligible)).IsEqualTo("Active")
      .Because("Sibling status Published is not in the eligible set.");
    await Assert.That(await _readStatusAsync(noSibling)).IsEqualTo("Active")
      .Because("No sibling row → the EXISTS correlation finds nothing.");
  }

  private CollectiveDispatcher _buildCrossDispatcher() {
    var services = new ServiceCollection();
    var handler = new _crossPerspective();
    services.AddSingleton(handler);

    var entries = new CollectiveApplyEntry[] {
      new(
        ModelType: typeof(_jobModel),
        EventType: typeof(_archiveJobsCollectiveEvent),
        HandlerType: typeof(_crossPerspective),
        MethodName: nameof(_crossPerspective.Archive),
        ScopeHandling: CollectiveScopeHandling.Framework,
        SpecKind: CollectiveSpecKind.Linq,
        Invoker: static (h, e, q) => ((_crossPerspective)h).Archive((_archiveJobsCollectiveEvent)e, q)
      ),
    };

    return new CollectiveDispatcher(
      services.BuildServiceProvider(),
      entries,
      [new TenantCollectiveScopeResolver()],
      [new EFCoreCollectiveEventExecutor<_jobModel>()]);
  }

  // Scopes the mutated job table by a status that lives on the sibling status perspective.
  internal sealed class _crossPerspective {
    private static readonly string[] _eligible = ["Draft"];

    public ICollectiveSpec<_jobModel> Archive(_archiveJobsCollectiveEvent e, ICollectiveQuery q) =>
      new _whereSpec(
        s => s.SetProperty(j => j.Status, "Archived"),
        r => q.Of<_jobStatusModel>().Any(st => st.Id == r.Id && _eligible.Contains(st.Data.Status)));

    private sealed record _whereSpec(
        Expression<Action<ICollectiveSetters<_jobModel>>> Setters,
        Expression<Func<PerspectiveRow<_jobModel>, bool>>? Where) : ICollectiveSpec<_jobModel>;
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

  internal sealed class _jobStatusModel {
    public string Status { get; set; } = string.Empty;
  }

  private sealed class _jobDbContext(DbContextOptions<_jobDbContext> options) : DbContext(options) {
    public DbSet<PerspectiveRow<_jobModel>> Jobs => Set<PerspectiveRow<_jobModel>>();
    public DbSet<PerspectiveRow<_jobStatusModel>> JobStatuses => Set<PerspectiveRow<_jobStatusModel>>();
    public DbSet<PerspectiveRow<_cellsModel>> CellsRows => Set<PerspectiveRow<_cellsModel>>();

    protected override void OnModelCreating(ModelBuilder modelBuilder) {
      base.OnModelCreating(modelBuilder);
      _mapRow<_jobModel>(modelBuilder, "wh_per_collective_job");
      _mapRow<_jobStatusModel>(modelBuilder, "wh_per_collective_job_status");
      // POLYMORPHIC model mapping: Data is a SCALAR jsonb column (Property + HasColumnType), NOT
      // ComplexProperty().ToJson(). This is exactly what a consumer generates for perspective models with
      // [JsonPolymorphic] members (e.g. OrderTenantFields, whose field cells are polymorphic). EF Core 10
      // rejects native nested SetProperty(j => j.Data.Sub, …) on this shape — there is no complex
      // sub-property — with "does not represent a valid property to be set".
      modelBuilder.Entity<PerspectiveRow<_cellsModel>>(e => {
        e.ToTable("wh_per_collective_cells");
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

    private static void _mapRow<TModel>(ModelBuilder modelBuilder, string table) where TModel : class {
      modelBuilder.Entity<PerspectiveRow<TModel>>(e => {
        e.ToTable(table);
        e.HasKey(x => x.Id);
        e.Property(x => x.Id).HasColumnName("id");
        // Mirror the PRODUCTION turnkey mapping (EFCoreSnippets): Data is an EF Core 10
        // ComplexProperty().ToJson() complex type, NOT a scalar jsonb column. This is the mapping a consumer
        // actually generates, and the one the collective rewriter must support. (Metadata/Scope stay
        // scalar jsonb here — they are not mutated or materialized by these tests, so keeping them simple
        // avoids unrelated complex-collection materialization noise.)
        e.ComplexProperty(x => x.Data, d => d.ToJson("data"));
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
      CREATE TABLE IF NOT EXISTS wh_per_collective_job_status (
        id UUID PRIMARY KEY,
        data JSONB NOT NULL,
        metadata JSONB NOT NULL,
        scope JSONB NOT NULL,
        created_at TIMESTAMPTZ NOT NULL,
        updated_at TIMESTAMPTZ NOT NULL,
        version INTEGER NOT NULL
      );
      CREATE TABLE IF NOT EXISTS wh_per_collective_cells (
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

  private async Task _seedJobStatusAsync(Guid id, string status) {
    await using var conn = new NpgsqlConnection(_connectionString);
    await conn.OpenAsync();
    await conn.ExecuteAsync("""
      INSERT INTO wh_per_collective_job_status
        (id, data, metadata, scope, created_at, updated_at, version)
      VALUES
        (@id, @data::jsonb, '{}'::jsonb, '{}'::jsonb, @createdAt, @updatedAt, 1);
      """, new {
      id,
      data = JsonSerializer.Serialize(new _jobStatusModel { Status = status }),
      createdAt = DateTime.UtcNow,
      updatedAt = DateTime.UtcNow,
    });
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
        Invoker: static (h, e, q) => ((_jobPerspective)h).ArchiveJobs((_archiveJobsCollectiveEvent)e)
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
        Invoker: static (h, e, q) => ((_archiveDraftPerspective)h).ArchiveDrafts((_archiveJobsCollectiveEvent)e)
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

  // ── Null-valued setter: clears a JSON sub-property to jsonb null ──────────────────────────────
  // EF Core 10 cannot set a ComplexProperty().ToJson() sub-property to null via ExecuteUpdate (bare null ->
  // untyped-NULL 42804; value-selector null -> nulls the whole column). The adapter must fall back to a raw
  // jsonb_set(data, '{Prop}', 'null'::jsonb) for null-valued setters. This is the Overlay-Clear shape.

  [Test]
  public async Task DispatchAsync_NullValuedSetter_ClearsJsonSubPropertyToNullAsync() {
    var jobId = Guid.NewGuid();
    await _seedJobAsync(jobId, tenantId: "t-clear", status: "Archived");

    // First set ArchivedAt to a real value so we can prove the clear nulls it.
    await _buildDispatcher().DispatchAsync(
      evt: new _archiveJobsCollectiveEvent {
        Scope = new TenantCollectiveScope("t-clear"),
        OccurredAt = DateTimeOffset.UtcNow,
      },
      collectiveEventId: Guid.NewGuid(),
      dbContextOrSession: _ctx!,
      cancellationToken: default);
    await Assert.That(await _readArchivedAtAsync(jobId)).IsNotNull()
      .Because("Precondition: the archive set ArchivedAt to a non-null value.");

    // Now clear ArchivedAt to null via a null-valued collective setter.
    var result = await _buildClearArchivedDispatcher().DispatchAsync(
      evt: new _clearArchivedCollectiveEvent { Scope = new TenantCollectiveScope("t-clear") },
      collectiveEventId: Guid.NewGuid(),
      dbContextOrSession: _ctx!,
      cancellationToken: default);

    await Assert.That(result.AffectedRowCount).IsEqualTo(1);
    await Assert.That(await _readArchivedAtAsync(jobId)).IsNull()
      .Because("A null-valued collective setter must null the JSON sub-property (jsonb null) — not throw or null the whole column.");
  }

  private async Task<string?> _readArchivedAtAsync(Guid id) {
    await using var conn = new NpgsqlConnection(_connectionString);
    await conn.OpenAsync();
    await using var cmd = conn.CreateCommand();
    cmd.CommandText = "SELECT data->>'ArchivedAt' FROM wh_per_collective_job WHERE id = @id;";
    cmd.Parameters.AddWithValue("id", id);
    var result = await cmd.ExecuteScalarAsync();
    return result == DBNull.Value || result is null ? null : (string)result;
  }

  private CollectiveDispatcher _buildClearArchivedDispatcher() {
    var services = new ServiceCollection();
    var handler = new _clearArchivedPerspective();
    services.AddSingleton(handler);

    var entries = new CollectiveApplyEntry[] {
      new(
        ModelType: typeof(_jobModel),
        EventType: typeof(_clearArchivedCollectiveEvent),
        HandlerType: typeof(_clearArchivedPerspective),
        MethodName: nameof(_clearArchivedPerspective.ClearArchivedAt),
        ScopeHandling: CollectiveScopeHandling.Framework,
        SpecKind: CollectiveSpecKind.Linq,
        Invoker: static (h, e, q) => ((_clearArchivedPerspective)h).ClearArchivedAt((_clearArchivedCollectiveEvent)e)
      ),
    };

    return new CollectiveDispatcher(
      services.BuildServiceProvider(),
      entries,
      [new TenantCollectiveScopeResolver()],
      [new EFCoreCollectiveEventExecutor<_jobModel>()]);
  }

  internal sealed class _clearArchivedPerspective {
    public ICollectiveSpec<_jobModel> ClearArchivedAt(_clearArchivedCollectiveEvent e) =>
      new _spec(s => s.SetProperty(j => j.ArchivedAt, (DateTimeOffset?)null));

    private sealed record _spec(Expression<Action<ICollectiveSetters<_jobModel>>> Setters)
      : ICollectiveSpec<_jobModel>;
  }

  internal sealed record _clearArchivedCollectiveEvent : ICollectiveEvent {
    public required CollectiveScope Scope { get; init; }
  }

  // ── Polymorphic model: Data is a SCALAR jsonb column (OrderTenantFields shape) ────────────────
  // EF Core 10's native nested SetProperty(j => j.Tag, ...) throws "does not represent a valid property to be
  // set" because there is no complex sub-property to target. The adapter must detect the non-complex Data
  // mapping and fall back to the raw jsonb_set path.

  [Test]
  public async Task DispatchAsync_PolymorphicScalarJsonbModel_AppliesViaRawPathAsync() {
    var id = Guid.NewGuid();
    await _seedCellsAsync(id, tenantId: "t-cells", tag: "before");

    var result = await _buildSetTagDispatcher().DispatchAsync(
      evt: new _setTagCollectiveEvent { Scope = new TenantCollectiveScope("t-cells"), Tag = "after" },
      collectiveEventId: Guid.NewGuid(),
      dbContextOrSession: _ctx!,
      cancellationToken: default);

    await Assert.That(result.AffectedRowCount).IsEqualTo(1);
    await Assert.That(await _readCellsTagAsync(id)).IsEqualTo("after")
      .Because("A scalar set on a polymorphic model (Data mapped as a scalar jsonb column, not ComplexProperty) must apply via the raw jsonb_set path — EF Core 10's native SetProperty rejects it.");
  }

  private async Task _seedCellsAsync(Guid id, string tenantId, string tag) {
    await using var conn = new NpgsqlConnection(_connectionString);
    await conn.OpenAsync();
    var dataJson = JsonSerializer.Serialize(new _cellsModel {
      Tag = tag,
      Cells = [new _cell { Key = "k1", Value = "v1" }, new _cell { Key = "k2", Value = "v2" }],
    });
    var scopeJson = JsonSerializer.Serialize(new PerspectiveScope { TenantId = tenantId });
    await conn.ExecuteAsync("""
      INSERT INTO wh_per_collective_cells (id, data, metadata, scope, created_at, updated_at, version)
      VALUES (@id, @data::jsonb, '{}'::jsonb, @scope::jsonb, @createdAt, @updatedAt, 1);
      """, new { id, data = dataJson, scope = scopeJson, createdAt = DateTime.UtcNow, updatedAt = DateTime.UtcNow });
  }

  private async Task<string?> _readCellsTagAsync(Guid id) {
    await using var conn = new NpgsqlConnection(_connectionString);
    await conn.OpenAsync();
    await using var cmd = conn.CreateCommand();
    cmd.CommandText = "SELECT data->>'Tag' FROM wh_per_collective_cells WHERE id = @id;";
    cmd.Parameters.AddWithValue("id", id);
    var result = await cmd.ExecuteScalarAsync();
    return result == DBNull.Value || result is null ? null : (string)result;
  }

  private CollectiveDispatcher _buildSetTagDispatcher() {
    var services = new ServiceCollection();
    var handler = new _setTagPerspective();
    services.AddSingleton(handler);

    var entries = new CollectiveApplyEntry[] {
      new(
        ModelType: typeof(_cellsModel),
        EventType: typeof(_setTagCollectiveEvent),
        HandlerType: typeof(_setTagPerspective),
        MethodName: nameof(_setTagPerspective.SetTag),
        ScopeHandling: CollectiveScopeHandling.Framework,
        SpecKind: CollectiveSpecKind.Linq,
        Invoker: static (h, e, q) => ((_setTagPerspective)h).SetTag((_setTagCollectiveEvent)e)
      ),
    };

    return new CollectiveDispatcher(
      services.BuildServiceProvider(),
      entries,
      [new TenantCollectiveScopeResolver()],
      [new EFCoreCollectiveEventExecutor<_cellsModel>()]);
  }

  internal sealed class _setTagPerspective {
    public ICollectiveSpec<_cellsModel> SetTag(_setTagCollectiveEvent e) =>
      new _spec(s => s.SetProperty(j => j.Tag, e.Tag));

    private sealed record _spec(Expression<Action<ICollectiveSetters<_cellsModel>>> Setters)
      : ICollectiveSpec<_cellsModel>;
  }

  internal sealed record _setTagCollectiveEvent : ICollectiveEvent {
    public required CollectiveScope Scope { get; init; }
    public required string Tag { get; init; }
  }

  internal sealed class _cellsModel {
    public string? Tag { get; set; }
    public List<_cell> Cells { get; set; } = [];
  }

  internal sealed class _cell {
    public string Key { get; set; } = string.Empty;
    public string Value { get; set; } = string.Empty;
  }
}
