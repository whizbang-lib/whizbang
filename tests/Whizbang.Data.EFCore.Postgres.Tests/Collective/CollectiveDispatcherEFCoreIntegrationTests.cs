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
/// End-to-end integration test for <see cref="CollectiveDispatcher"/>
/// against a real Postgres testcontainer. Exercises the full Slice
/// 7b-α composition:
/// <c>ICollectiveDispatcher → ICollectiveEventExecutor → CollectiveEventApplier&lt;TModel&gt; → EFCoreCollectiveAdapter → ExecuteUpdateAsync</c>.
/// </summary>
/// <remarks>
/// <para>
/// Verifies the locked design invariants land at the SQL layer:
/// </para>
/// <list type="bullet">
///   <item><description>Scope filter actually restricts the update to the captured tenant (tenant B's rows must not be touched).</description></item>
///   <item><description>Matched-set membership is enforced (out-of-set rows in the same tenant are not touched).</description></item>
///   <item><description>Audit pointer (<c>last_collective_event_id</c>) lands on every affected row in the same UPDATE.</description></item>
///   <item><description>Affected-row count surfaces in <see cref="CollectiveDispatchResult.AffectedRowCount"/>.</description></item>
/// </list>
/// <para>
/// The test bypasses the inbox/outbox transport (Slice 3) — that flow
/// is already covered by Slice 3's transport-roundtrip tests. The
/// purpose here is to lock the dispatch-to-Postgres seam itself.
/// </para>
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

  // ── End-to-end: tenant scope + matched set + audit pointer ────────────

  [Test]
  public async Task DispatchAsync_TenantScoped_MutatesOnlyMatchedRowsInTenantAsync() {
    // Seed: two tenants × two jobs each = four rows.
    //  t-A: jobA1 (matched), jobA2 (NOT matched — same tenant, excluded from set)
    //  t-B: jobB1 (NOT matched — different tenant), jobB2 (NOT matched — different tenant)
    var jobA1 = Guid.NewGuid();
    var jobA2 = Guid.NewGuid();
    var jobB1 = Guid.NewGuid();
    var jobB2 = Guid.NewGuid();

    await _seedJobAsync(jobA1, tenantId: "t-A", status: "Active");
    await _seedJobAsync(jobA2, tenantId: "t-A", status: "Active");
    await _seedJobAsync(jobB1, tenantId: "t-B", status: "Active");
    await _seedJobAsync(jobB2, tenantId: "t-B", status: "Active");

    // Dispatch an archive event scoped to t-A, matching only jobA1.
    var evtId = Guid.NewGuid();
    var dispatcher = _buildDispatcher();
    var result = await dispatcher.DispatchAsync(
      evt: new _archiveJobsCollectiveEvent {
        Scope = new TenantCollectiveScope("t-A"),
        MatchedStreamIds = [jobA1],
        OccurredAt = DateTimeOffset.UtcNow,
      },
      collectiveEventId: evtId,
      dbContextOrSession: _ctx!,
      cancellationToken: default);

    // Result aggregate
    await Assert.That(result.HandlerCount).IsEqualTo(1);
    await Assert.That(result.AffectedRowCount).IsEqualTo(1)
      .Because("Exactly one row (jobA1) intersected the tenant filter AND the matched-id set — the SQL UPDATE must report exactly that count.");

    // Read back via raw SQL — the test exercises the SET-clause + WHERE
    // path; verification doesn't need to go through EF's jsonb materialization.
    var (statusA1, auditA1) = await _readJobAsync(jobA1);
    var (statusA2, auditA2) = await _readJobAsync(jobA2);
    var (statusB1, auditB1) = await _readJobAsync(jobB1);
    var (statusB2, auditB2) = await _readJobAsync(jobB2);

    await Assert.That(statusA1).IsEqualTo("Archived")
      .Because("jobA1 was in the matched-set and matched the tenant scope — must be archived.");
    await Assert.That(auditA1).IsEqualTo(evtId)
      .Because("The audit pointer MUST land on every row the SQL UPDATE touched, in the same statement, so audit visibility is atomic with the mutation.");

    await Assert.That(statusA2).IsEqualTo("Active")
      .Because("jobA2 was in the same tenant but NOT in the matched-set — the membership clause must exclude it.");
    await Assert.That(auditA2).IsNull()
      .Because("If the audit pointer landed on jobA2 the matched-set membership wasn't actually enforced — that would be a silent over-mutation.");

    await Assert.That(statusB1).IsEqualTo("Active");
    await Assert.That(auditB1).IsNull();
    await Assert.That(statusB2).IsEqualTo("Active");
    await Assert.That(auditB2).IsNull()
      .Because("Tenant B rows must be entirely untouched — the resolver's scope filter must restrict by row.scope.TenantId.");
  }

  // ── Multi-row in same tenant ──────────────────────────────────────────

  [Test]
  public async Task DispatchAsync_AllRowsInTenantMatched_UpdatesAllAsync() {
    var job1 = Guid.NewGuid();
    var job2 = Guid.NewGuid();
    var job3 = Guid.NewGuid();

    await _seedJobAsync(job1, tenantId: "t-multi", status: "Active");
    await _seedJobAsync(job2, tenantId: "t-multi", status: "Active");
    await _seedJobAsync(job3, tenantId: "t-multi", status: "Active");

    var evtId = Guid.NewGuid();
    var dispatcher = _buildDispatcher();
    var result = await dispatcher.DispatchAsync(
      evt: new _archiveJobsCollectiveEvent {
        Scope = new TenantCollectiveScope("t-multi"),
        MatchedStreamIds = [job1, job2, job3],
        OccurredAt = DateTimeOffset.UtcNow,
      },
      collectiveEventId: evtId,
      dbContextOrSession: _ctx!,
      cancellationToken: default);

    await Assert.That(result.AffectedRowCount).IsEqualTo(3)
      .Because("All three matched-ids fall inside the tenant scope — all three must be updated in one SQL statement.");

    foreach (var id in new[] { job1, job2, job3 }) {
      var (status, audit) = await _readJobAsync(id);
      await Assert.That(status).IsEqualTo("Archived");
      await Assert.That(audit).IsEqualTo(evtId);
    }
  }

  private async Task<(string Status, Guid? AuditId)> _readJobAsync(Guid id) {
    await using var conn = new NpgsqlConnection(_connectionString);
    await conn.OpenAsync();
    await using var cmd = conn.CreateCommand();
    cmd.CommandText = "SELECT data->>'Status', last_collective_event_id FROM wh_per_collective_job WHERE id = @id;";
    cmd.Parameters.AddWithValue("id", id);
    await using var reader = await cmd.ExecuteReaderAsync();
    if (!await reader.ReadAsync()) {
      throw new InvalidOperationException($"No row found for id {id}");
    }
    var status = reader.GetString(0);
    Guid? audit = reader.IsDBNull(1) ? null : reader.GetGuid(1);
    return (status, audit);
  }

  // ── Empty matched-set short-circuit ───────────────────────────────────

  [Test]
  public async Task DispatchAsync_EmptyMatchedSet_AffectsZeroRowsAndDoesNotErrorAsync() {
    var dispatcher = _buildDispatcher();
    var result = await dispatcher.DispatchAsync(
      evt: new _archiveJobsCollectiveEvent {
        Scope = new TenantCollectiveScope("t-empty"),
        MatchedStreamIds = [], // captured-at-write-time empty set
        OccurredAt = DateTimeOffset.UtcNow,
      },
      collectiveEventId: Guid.NewGuid(),
      dbContextOrSession: _ctx!,
      cancellationToken: default);

    await Assert.That(result.HandlerCount).IsEqualTo(1)
      .Because("The handler still fires — the matched-set being empty is a valid producer outcome, not an absent subscriber.");
    await Assert.That(result.AffectedRowCount).IsEqualTo(0)
      .Because("Adapter short-circuits empty matched-sets to Task.FromResult(0) — no SQL round-trip, but the count surfaces cleanly.");
  }

  // ── Setup / teardown ──────────────────────────────────────────────────

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

  // ── DbContext + model ─────────────────────────────────────────────────

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
        e.Property(x => x.LastCollectiveEventId).HasColumnName("last_collective_event_id");
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
        version INTEGER NOT NULL,
        last_collective_event_id UUID NULL
      );
      """);
  }

  private async Task _seedJobAsync(Guid id, string tenantId, string status) {
    await using var conn = new NpgsqlConnection(_connectionString);
    await conn.OpenAsync();

    var dataJson = JsonSerializer.Serialize(new _jobModel { Status = status });
    var scopeJson = JsonSerializer.Serialize(new PerspectiveScope { TenantId = tenantId });
    var metadataJson = "{}";

    await conn.ExecuteAsync("""
      INSERT INTO wh_per_collective_job
        (id, data, metadata, scope, created_at, updated_at, version)
      VALUES
        (@id, @data::jsonb, @metadata::jsonb, @scope::jsonb, @createdAt, @updatedAt, 1);
      """, new {
      id,
      data = dataJson,
      metadata = metadataJson,
      scope = scopeJson,
      createdAt = DateTime.UtcNow,
      updatedAt = DateTime.UtcNow,
    });
  }

  // ── Dispatcher wiring ─────────────────────────────────────────────────

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

    var resolvers = new ICollectiveScopeResolver[] {
      new TenantCollectiveScopeResolver(),
    };

    var executors = new ICollectiveEventExecutor[] {
      new EFCoreCollectiveEventExecutor<_jobModel>(),
    };

    return new CollectiveDispatcher(services.BuildServiceProvider(), entries, resolvers, executors);
  }

  // ── Test perspective + event ──────────────────────────────────────────

  internal sealed class _jobPerspective {
    public ICollectiveSpec<_jobModel> ArchiveJobs(_archiveJobsCollectiveEvent e) =>
      new _spec(s => s
        .SetProperty(j => j.Status, "Archived")
        .SetProperty(j => j.ArchivedAt, e.OccurredAt));

    private sealed record _spec(Expression<Action<ICollectiveSetters<_jobModel>>> Setters)
      : ICollectiveSpec<_jobModel>;
  }

  internal sealed record _archiveJobsCollectiveEvent : ICollectiveEvent {
    public required ICollectiveScope Scope { get; init; }
    public required IReadOnlyList<Guid> MatchedStreamIds { get; init; }
    public required DateTimeOffset OccurredAt { get; init; }
  }
}
