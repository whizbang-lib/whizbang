using Microsoft.EntityFrameworkCore;
using Npgsql;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core;
using Whizbang.Core.Lenses;
using Whizbang.Core.Perspectives;
using Whizbang.Testing.Containers;

namespace Whizbang.Data.EFCore.Postgres.Tests;

/// <summary>
/// Real-Postgres integration tests for a production symptom observed on a consumer: two
/// perspectives on the same stream sharing one DbContext — Order (scalar)
/// persists, OrderLines (with List&lt;RowItem&gt;) does not.
///
/// <para>
/// These tests use Testcontainers Postgres + the production
/// <c>PostgresUpsertStrategy</c> (NOT InMemory), and exercise the JSONB-via-
/// <c>ComplexProperty().ToJson()</c> path that the InMemory provider does not.
/// </para>
///
/// <para>
/// Companion to <see cref="MultiPerspectiveUpsertSymmetryTests"/>, which covers the
/// same invariants on InMemory with scalar-only models.
/// </para>
/// </summary>
[Category("Integration")]
[NotInParallel("EFCorePostgresTests")]
[Category("Shard2")]
public class MultiPerspectivePostgresUpsertSymmetryTests : IAsyncDisposable {
  private static readonly Uuid7IdProvider _idProvider = new();

  private static Guid _newGuid() => (Guid)_idProvider.NewGuid();

  static MultiPerspectivePostgresUpsertSymmetryTests() {
    AppContext.SetSwitch("Npgsql.EnableLegacyTimestampBehavior", false);
  }

  private string? _testDatabaseName;
  private NpgsqlDataSource? _dataSource;
  private string _connectionString = null!;
  private DbContextOptions<TwoPerspectivesDbContext> _options = null!;

  /// <summary>Mirrors OrderModel: scalar properties only — the perspective that DOES persist in the consumer's production system.</summary>
  public class ScalarModel {
    [StreamId]
    public Guid Id { get; set; }
    public string? FieldA { get; set; }
    public string? FieldB { get; set; }
  }

  /// <summary>Mirrors OrderLines: scalar fields plus a List&lt;T&gt; property
  /// that becomes a JSONB array. The perspective that does NOT persist in the consumer's production system.</summary>
  public class ListModel {
    [StreamId]
    public Guid Id { get; set; }
    public string? Name { get; set; }
    public List<RowItem> Rows { get; set; } = [];
  }

  public class RowItem {
    public string? Label { get; set; }
    public int Value { get; set; }
  }

  private sealed class TwoPerspectivesDbContext(DbContextOptions<TwoPerspectivesDbContext> options) : DbContext(options) {
    protected override void OnModelCreating(ModelBuilder modelBuilder) {
      base.OnModelCreating(modelBuilder);

      modelBuilder.Entity<PerspectiveRow<ScalarModel>>(entity => {
        entity.ToTable("wh_per_scalar");
        entity.HasKey(e => e.Id);
        entity.Property(e => e.Id).HasColumnName("id");
        entity.Property(e => e.CreatedAt).HasColumnName("created_at");
        entity.Property(e => e.UpdatedAt).HasColumnName("updated_at");
        entity.Property(e => e.Version).HasColumnName("version");
        entity.ComplexProperty(e => e.Data, d => d.ToJson("data"));
        entity.ComplexProperty(e => e.Metadata, m => m.ToJson("metadata"));
        entity.ComplexProperty(e => e.Scope, s => s.ToJson("scope"));
      });

      modelBuilder.Entity<PerspectiveRow<ListModel>>(entity => {
        entity.ToTable("wh_per_list");
        entity.HasKey(e => e.Id);
        entity.Property(e => e.Id).HasColumnName("id");
        entity.Property(e => e.CreatedAt).HasColumnName("created_at");
        entity.Property(e => e.UpdatedAt).HasColumnName("updated_at");
        entity.Property(e => e.Version).HasColumnName("version");
        entity.ComplexProperty(e => e.Data, d => d.ToJson("data"));
        entity.ComplexProperty(e => e.Metadata, m => m.ToJson("metadata"));
        entity.ComplexProperty(e => e.Scope, s => s.ToJson("scope"));
      });
    }
  }

  [Before(Test)]
  public async Task SetupAsync() {
    await SharedPostgresContainer.InitializeAsync();
    _testDatabaseName = $"multipers_test_{Guid.NewGuid():N}";

    await using var adminConnection = new NpgsqlConnection(SharedPostgresContainer.ConnectionString);
    await adminConnection.OpenAsync();
    await adminConnection.ExecuteAsync($"CREATE DATABASE {_testDatabaseName}");

    var builder = new NpgsqlConnectionStringBuilder(SharedPostgresContainer.ConnectionString) {
      Database = _testDatabaseName,
      Timezone = "UTC",
      IncludeErrorDetail = true
    };
    _connectionString = builder.ConnectionString;

    var dataSourceBuilder = new NpgsqlDataSourceBuilder(_connectionString);
    dataSourceBuilder.EnableDynamicJson();
    _dataSource = dataSourceBuilder.Build();

    _options = new DbContextOptionsBuilder<TwoPerspectivesDbContext>()
      .UseNpgsql(_dataSource)
      .ConfigureWarnings(w => w.Ignore(Microsoft.EntityFrameworkCore.Diagnostics.CoreEventId.ManyServiceProvidersCreatedWarning))
      .Options;

    await _initializeSchemaAsync();
  }

  [After(Test)]
  public async Task TeardownAsync() {
    if (_dataSource != null) {
      await _dataSource.DisposeAsync();
      _dataSource = null;
    }
    if (_testDatabaseName != null) {
      try {
        await using var adminConnection = new NpgsqlConnection(SharedPostgresContainer.ConnectionString);
        await adminConnection.OpenAsync();
        await adminConnection.ExecuteAsync($@"
          SELECT pg_terminate_backend(pg_stat_activity.pid)
          FROM pg_stat_activity
          WHERE pg_stat_activity.datname = '{_testDatabaseName}'
          AND pid <> pg_backend_pid()");
        await adminConnection.ExecuteAsync($"DROP DATABASE IF EXISTS {_testDatabaseName} WITH (FORCE)");
      } catch {
        // Cleanup best-effort
      }
      _testDatabaseName = null;
    }
  }

  public async ValueTask DisposeAsync() {
    await TeardownAsync();
    GC.SuppressFinalize(this);
  }

  private async Task _initializeSchemaAsync() {
    await using var connection = new NpgsqlConnection(_connectionString);
    await connection.OpenAsync();
    await connection.ExecuteAsync("""
      CREATE TABLE IF NOT EXISTS wh_per_scalar (
        id UUID PRIMARY KEY,
        data JSONB NOT NULL,
        metadata JSONB NOT NULL,
        scope JSONB NOT NULL,
        created_at TIMESTAMPTZ NOT NULL,
        updated_at TIMESTAMPTZ NOT NULL,
        sys_created_at TIMESTAMPTZ,
        sys_updated_at TIMESTAMPTZ,
        version INTEGER NOT NULL
      );
      CREATE TABLE IF NOT EXISTS wh_per_list (
        id UUID PRIMARY KEY,
        data JSONB NOT NULL,
        metadata JSONB NOT NULL,
        scope JSONB NOT NULL,
        created_at TIMESTAMPTZ NOT NULL,
        updated_at TIMESTAMPTZ NOT NULL,
        sys_created_at TIMESTAMPTZ,
        sys_updated_at TIMESTAMPTZ,
        version INTEGER NOT NULL
      );
      """);
  }

  // ===========================================================================
  // TESTS — production code path (PostgresUpsertStrategy + real Postgres + JSONB)
  // ===========================================================================

  /// <summary>
  /// Direct consumer scenario: two perspectives, scalar + list-property, both upsert
  /// for the SAME stream on the SAME DbContext. Both rows must persist in their
  /// respective tables. RED here would directly reproduce the production symptom
  /// and localize the bug to the list-property upsert path.
  /// </summary>
  [Test, Timeout(60000)]
  public async Task TwoPerspectives_Scalar_AndList_SameStreamId_BothRowsPersistAsync(CancellationToken ct) {
    await using var context = new TwoPerspectivesDbContext(_options);
    var strategy = new PostgresUpsertStrategy();
    var scalarStore = new EFCorePostgresPerspectiveStore<ScalarModel>(context, "wh_per_scalar", strategy);
    var listStore = new EFCorePostgresPerspectiveStore<ListModel>(context, "wh_per_list", strategy);
    var streamId = _newGuid();

    await scalarStore.UpsertAsync(streamId, new ScalarModel { Id = streamId, FieldA = "a", FieldB = "b" }, ct);
    await listStore.UpsertAsync(streamId, new ListModel {
      Id = streamId,
      Name = "list-perspective",
      Rows = [new() { Label = "first", Value = 1 }, new() { Label = "second", Value = 2 }]
    }, ct);

    var savedScalar = await context.Set<PerspectiveRow<ScalarModel>>().AsNoTracking().FirstOrDefaultAsync(r => r.Id == streamId, ct);
    var savedList = await context.Set<PerspectiveRow<ListModel>>().AsNoTracking().FirstOrDefaultAsync(r => r.Id == streamId, ct);

    await Assert.That(savedScalar).IsNotNull()
      .Because("Scalar perspective row must persist on shared DbContext.");
    await Assert.That(savedList).IsNotNull()
      .Because("List perspective row must ALSO persist symmetrically. Asymmetry here is the consumer's UI loader bug.");
    await Assert.That(savedScalar!.Data.FieldA).IsEqualTo("a");
    await Assert.That(savedList!.Data.Rows.Count).IsEqualTo(2);
    await Assert.That(savedList.Data.Rows[0].Label).IsEqualTo("first");
  }

  /// <summary>
  /// Reverse order: list-perspective FIRST, then scalar. Catches a regression where
  /// the second upsert is what's lost (i.e., the scalar perspective happens to be
  /// what's "always saved last" for this consumer, masking a list-second bug).
  /// </summary>
  [Test, Timeout(60000)]
  public async Task TwoPerspectives_ListFirst_ThenScalar_BothPersistAsync(CancellationToken ct) {
    await using var context = new TwoPerspectivesDbContext(_options);
    var strategy = new PostgresUpsertStrategy();
    var scalarStore = new EFCorePostgresPerspectiveStore<ScalarModel>(context, "wh_per_scalar", strategy);
    var listStore = new EFCorePostgresPerspectiveStore<ListModel>(context, "wh_per_list", strategy);
    var streamId = _newGuid();

    await listStore.UpsertAsync(streamId, new ListModel {
      Id = streamId,
      Name = "first-up",
      Rows = [new() { Label = "row1", Value = 10 }]
    }, ct);
    await scalarStore.UpsertAsync(streamId, new ScalarModel { Id = streamId, FieldA = "after", FieldB = "list" }, ct);

    var savedScalar = await context.Set<PerspectiveRow<ScalarModel>>().AsNoTracking().FirstOrDefaultAsync(r => r.Id == streamId, ct);
    var savedList = await context.Set<PerspectiveRow<ListModel>>().AsNoTracking().FirstOrDefaultAsync(r => r.Id == streamId, ct);

    await Assert.That(savedList).IsNotNull()
      .Because("List perspective row must persist regardless of insertion order.");
    await Assert.That(savedScalar).IsNotNull()
      .Because("Scalar perspective row must persist after a list upsert.");
    await Assert.That(savedList!.Data.Rows.Count).IsEqualTo(1);
    await Assert.That(savedScalar!.Data.FieldA).IsEqualTo("after");
  }

  /// <summary>
  /// Update path: list-perspective gets a SECOND upsert (event 2 for the stream)
  /// while scalar perspective also exists. The scalar row must remain intact and
  /// the list row must update its Rows array correctly.
  /// </summary>
  [Test, Timeout(60000)]
  public async Task ListPerspective_SecondUpsert_UpdatesRowsArray_ScalarRowUntouchedAsync(CancellationToken ct) {
    await using var context = new TwoPerspectivesDbContext(_options);
    var strategy = new PostgresUpsertStrategy();
    var scalarStore = new EFCorePostgresPerspectiveStore<ScalarModel>(context, "wh_per_scalar", strategy);
    var listStore = new EFCorePostgresPerspectiveStore<ListModel>(context, "wh_per_list", strategy);
    var streamId = _newGuid();

    await scalarStore.UpsertAsync(streamId, new ScalarModel { Id = streamId, FieldA = "scalar-stable", FieldB = "x" }, ct);
    await listStore.UpsertAsync(streamId, new ListModel {
      Id = streamId,
      Name = "v1",
      Rows = [new() { Label = "v1-row", Value = 1 }]
    }, ct);
    await listStore.UpsertAsync(streamId, new ListModel {
      Id = streamId,
      Name = "v2",
      Rows = [new() { Label = "v2-row-a", Value = 10 }, new() { Label = "v2-row-b", Value = 20 }]
    }, ct);

    var savedScalar = await context.Set<PerspectiveRow<ScalarModel>>().AsNoTracking().FirstOrDefaultAsync(r => r.Id == streamId, ct);
    var savedList = await context.Set<PerspectiveRow<ListModel>>().AsNoTracking().FirstOrDefaultAsync(r => r.Id == streamId, ct);

    await Assert.That(savedScalar).IsNotNull()
      .Because("Scalar row must remain after sibling list-perspective updates.");
    await Assert.That(savedScalar!.Data.FieldA).IsEqualTo("scalar-stable");
    await Assert.That(savedList).IsNotNull();
    await Assert.That(savedList!.Data.Name).IsEqualTo("v2");
    await Assert.That(savedList.Data.Rows.Count).IsEqualTo(2)
      .Because("Updated list-perspective must have the v2 Rows array (size 2), not the v1 array (size 1).");
    await Assert.That(savedList.Data.Rows[0].Label).IsEqualTo("v2-row-a");
  }

  /// <summary>
  /// Many fan-out: 3 streams, each with both a scalar AND a list perspective. Catches
  /// cross-stream contamination via shared change tracker state.
  /// </summary>
  [Test, Timeout(60000)]
  public async Task MultipleStreams_EachWithBothPerspectives_AllSixRowsPersistAsync(CancellationToken ct) {
    await using var context = new TwoPerspectivesDbContext(_options);
    var strategy = new PostgresUpsertStrategy();
    var scalarStore = new EFCorePostgresPerspectiveStore<ScalarModel>(context, "wh_per_scalar", strategy);
    var listStore = new EFCorePostgresPerspectiveStore<ListModel>(context, "wh_per_list", strategy);
    var streams = new[] { _newGuid(), _newGuid(), _newGuid() };

    foreach (var sid in streams) {
      await scalarStore.UpsertAsync(sid, new ScalarModel { Id = sid, FieldA = $"scalar-{sid}", FieldB = "b" }, ct);
      await listStore.UpsertAsync(sid, new ListModel {
        Id = sid,
        Name = $"list-{sid}",
        Rows = [new() { Label = $"row-for-{sid}", Value = 7 }]
      }, ct);
    }

    foreach (var sid in streams) {
      var savedScalar = await context.Set<PerspectiveRow<ScalarModel>>().AsNoTracking().FirstOrDefaultAsync(r => r.Id == sid, ct);
      var savedList = await context.Set<PerspectiveRow<ListModel>>().AsNoTracking().FirstOrDefaultAsync(r => r.Id == sid, ct);
      await Assert.That(savedScalar).IsNotNull().Because($"Scalar row missing for stream {sid}.");
      await Assert.That(savedList).IsNotNull().Because($"List row missing for stream {sid} — the production symptom reproduced on this stream.");
      await Assert.That(savedList!.Data.Rows.Count).IsEqualTo(1);
      await Assert.That(savedList.Data.Rows[0].Label).IsEqualTo($"row-for-{sid}");
    }
  }

  /// <summary>
  /// Empty list to non-empty list: an event comes in for a stream whose list-perspective
  /// was created with an empty Rows array, then a second event populates it. Catches a
  /// bug where empty-array → populated-array updates fail to persist the new entries.
  /// </summary>
  [Test, Timeout(60000)]
  public async Task ListPerspective_EmptyToNonEmpty_PersistsRowsAsync(CancellationToken ct) {
    await using var context = new TwoPerspectivesDbContext(_options);
    var strategy = new PostgresUpsertStrategy();
    var listStore = new EFCorePostgresPerspectiveStore<ListModel>(context, "wh_per_list", strategy);
    var streamId = _newGuid();

    await listStore.UpsertAsync(streamId, new ListModel { Id = streamId, Name = "empty-init", Rows = [] }, ct);
    await listStore.UpsertAsync(streamId, new ListModel {
      Id = streamId,
      Name = "filled",
      Rows = [new() { Label = "added", Value = 99 }]
    }, ct);

    var saved = await context.Set<PerspectiveRow<ListModel>>().AsNoTracking().FirstOrDefaultAsync(r => r.Id == streamId, ct);
    await Assert.That(saved).IsNotNull();
    await Assert.That(saved!.Data.Rows.Count).IsEqualTo(1)
      .Because("Empty→non-empty list update must persist the new entries.");
    await Assert.That(saved.Data.Rows[0].Label).IsEqualTo("added");
    await Assert.That(saved.Data.Name).IsEqualTo("filled");
  }

  // ===========================================================================
  // METADATA ISOLATION + IDEMPOTENCY-GUARD CROSS-TALK TESTS
  //
  // The runner template's idempotency guard reads the previous EventId via
  // GetMetadataByStreamIdAsync to filter out already-applied events. If the
  // metadata stored on perspective A's row leaks into perspective B's metadata
  // read (or vice versa), perspective B's events would be filtered out and
  // never applied — matching the production symptom (one perspective row missing).
  // ===========================================================================

  /// <summary>
  /// Per-perspective metadata isolation: ScalarModel and ListModel both upsert
  /// for the same stream with DIFFERENT EventIds. GetMetadataByStreamIdAsync on
  /// each store must return its OWN perspective's EventId, not the sibling's.
  /// If the stores cross-pollute on metadata reads, the runner's idempotency
  /// guard would silently drop events and the row would appear "missing".
  /// </summary>
  [Test, Timeout(60000)]
  public async Task PerPerspectiveMetadata_IsolatedReads_DoNotCrossPolluteAsync(CancellationToken ct) {
    await using var context = new TwoPerspectivesDbContext(_options);
    var strategy = new PostgresUpsertStrategy();
    var scalarStore = new EFCorePostgresPerspectiveStore<ScalarModel>(context, "wh_per_scalar", strategy);
    var listStore = new EFCorePostgresPerspectiveStore<ListModel>(context, "wh_per_list", strategy);
    var streamId = _newGuid();
    var scalarEventId = _newGuid();
    var listEventId = _newGuid();

    var scalarMeta = new PerspectiveMetadata {
      EventId = scalarEventId.ToString("D"),
      EventType = "ScalarApplied",
      Timestamp = DateTime.UtcNow
    };
    var listMeta = new PerspectiveMetadata {
      EventId = listEventId.ToString("D"),
      EventType = "ListApplied",
      Timestamp = DateTime.UtcNow
    };

    await scalarStore.UpsertAsync(streamId, new ScalarModel { Id = streamId, FieldA = "s" }, new PerspectiveScope(), false, scalarMeta, ct);
    await listStore.UpsertAsync(streamId, new ListModel { Id = streamId, Name = "l", Rows = [new() { Label = "r", Value = 1 }] }, new PerspectiveScope(), false, listMeta, ct);

    var scalarReadBack = await scalarStore.GetMetadataByStreamIdAsync(streamId, ct);
    var listReadBack = await listStore.GetMetadataByStreamIdAsync(streamId, ct);

    await Assert.That(scalarReadBack).IsNotNull();
    await Assert.That(scalarReadBack!.EventId).IsEqualTo(scalarEventId.ToString("D"))
      .Because("Scalar perspective's metadata read must return scalar's own EventId.");
    await Assert.That(listReadBack).IsNotNull();
    await Assert.That(listReadBack!.EventId).IsEqualTo(listEventId.ToString("D"))
      .Because("List perspective's metadata read must return list's own EventId, NOT the scalar's. If these cross, the runner's idempotency guard silently drops events and the production symptom appears.");
  }

  /// <summary>
  /// Idempotency-guard cross-talk: scalar perspective applies an UUIDv7 EventId
  /// that is LATER than the list perspective's pending event. If the list
  /// perspective's runner accidentally read scalar's metadata, the
  /// string-compare-greater filter would drop ALL of list's events because
  /// "list event id &lt; scalar last applied" — exactly the production symptom.
  /// </summary>
  [Test, Timeout(60000)]
  public async Task IdempotencyFilter_DoesNotApplyAcrossPerspectives_WhenScalarAdvancedFurtherAsync(CancellationToken ct) {
    await using var context = new TwoPerspectivesDbContext(_options);
    var strategy = new PostgresUpsertStrategy();
    var scalarStore = new EFCorePostgresPerspectiveStore<ScalarModel>(context, "wh_per_scalar", strategy);
    var listStore = new EFCorePostgresPerspectiveStore<ListModel>(context, "wh_per_list", strategy);
    var streamId = _newGuid();

    // Scalar is at a LATE event id (UUIDv7 = chronologically late).
    var scalarLateMeta = new PerspectiveMetadata {
      EventId = _newGuid().ToString("D"),
      EventType = "ScalarLate",
      Timestamp = DateTime.UtcNow
    };
    await scalarStore.UpsertAsync(streamId, new ScalarModel { Id = streamId, FieldA = "scalar-applied-late" }, new PerspectiveScope(), false, scalarLateMeta, ct);

    // List perspective applies its FIRST event for this stream (no prior metadata).
    // Its event id can be earlier or later than scalar's — the comparison must
    // happen against LIST's own metadata (none yet) NOT scalar's.
    var listFirstMeta = new PerspectiveMetadata {
      EventId = _newGuid().ToString("D"),
      EventType = "ListFirst",
      Timestamp = DateTime.UtcNow
    };
    await listStore.UpsertAsync(streamId, new ListModel { Id = streamId, Name = "first", Rows = [new() { Label = "r1", Value = 1 }] }, new PerspectiveScope(), false, listFirstMeta, ct);

    var listMeta = await listStore.GetMetadataByStreamIdAsync(streamId, ct);
    var savedList = await context.Set<PerspectiveRow<ListModel>>().AsNoTracking().FirstOrDefaultAsync(r => r.Id == streamId, ct);

    await Assert.That(savedList).IsNotNull()
      .Because("List perspective row must persist regardless of scalar perspective's metadata state.");
    await Assert.That(listMeta).IsNotNull();
    await Assert.That(listMeta!.EventId).IsEqualTo(listFirstMeta.EventId)
      .Because("List metadata must reflect list's own apply, not be affected by scalar's prior late upsert.");
    await Assert.That(savedList!.Data.Name).IsEqualTo("first");
  }

  // ===========================================================================
  // SCOPE-FILTER SYMMETRY TESTS
  //
  // The runner applies lastScope?.FilterByFields(_inheritScopeOnCreate) before
  // upserting. Perspectives with different [InheritScope] settings get different
  // ScopeFields constants. If the filter produces a problematic value (e.g.,
  // empty scope when DB has NOT NULL constraint, or a scope that fails
  // some downstream filter), one perspective could fail to save while the other
  // succeeds — matching the production symptom.
  // ===========================================================================

  /// <summary>
  /// Symmetry across [InheritScope] settings: scalar perspective gets ScopeFields.None
  /// (empty scope) and list perspective gets ScopeFields.All (full scope). Both must
  /// upsert successfully. JSONB NOT NULL constraint vs an empty scope object is one
  /// path where this could fail.
  /// </summary>
  [Test, Timeout(60000)]
  public async Task SymmetricUpsert_WhenScalarGetsEmptyScope_AndListGetsFullScope_BothPersistAsync(CancellationToken ct) {
    await using var context = new TwoPerspectivesDbContext(_options);
    var strategy = new PostgresUpsertStrategy();
    var scalarStore = new EFCorePostgresPerspectiveStore<ScalarModel>(context, "wh_per_scalar", strategy);
    var listStore = new EFCorePostgresPerspectiveStore<ListModel>(context, "wh_per_list", strategy);
    var streamId = _newGuid();

    // Scalar gets the result of FilterByFields(ScopeFields.None) — empty scope (all defaults)
    var scalarEmptyScope = new PerspectiveScope().FilterByFields(global::Whizbang.Core.Lenses.ScopeFields.None);
    // List gets the result of FilterByFields with ALL fields — full scope
    var listFullScope = new PerspectiveScope {
      TenantId = _newGuid().ToString(),
      CustomerId = _newGuid().ToString(),
      UserId = _newGuid().ToString(),
      OrganizationId = _newGuid().ToString()
    }.FilterByFields(
      global::Whizbang.Core.Lenses.ScopeFields.Tenant |
      global::Whizbang.Core.Lenses.ScopeFields.Customer |
      global::Whizbang.Core.Lenses.ScopeFields.User |
      global::Whizbang.Core.Lenses.ScopeFields.Organization);

    await scalarStore.UpsertAsync(streamId, new ScalarModel { Id = streamId, FieldA = "no-scope" }, scalarEmptyScope, false, ct);
    await listStore.UpsertAsync(streamId, new ListModel { Id = streamId, Name = "with-scope", Rows = [new() { Label = "r", Value = 1 }] }, listFullScope, false, ct);

    var savedScalar = await context.Set<PerspectiveRow<ScalarModel>>().AsNoTracking().FirstOrDefaultAsync(r => r.Id == streamId, ct);
    var savedList = await context.Set<PerspectiveRow<ListModel>>().AsNoTracking().FirstOrDefaultAsync(r => r.Id == streamId, ct);

    await Assert.That(savedScalar).IsNotNull()
      .Because("Scalar perspective with empty scope must save (JSONB column accepts {}).");
    await Assert.That(savedList).IsNotNull()
      .Because("List perspective with full scope must save (JSONB column accepts populated object). Asymmetry here = the production symptom.");
    await Assert.That(savedList!.Data.Rows.Count).IsEqualTo(1);
  }

  /// <summary>
  /// Re-application with scope mismatch: list perspective's first apply uses scope X,
  /// the second apply for the same stream uses scope Y. The default behavior is
  /// "scope is INSERT-only" (line 130-134 of BaseUpsertStrategy uses forceUpdateScope
  /// to gate scope writes). The row must still persist; only its data should update.
  /// </summary>
  [Test, Timeout(60000)]
  public async Task ListPerspective_SecondUpsertWithDifferentScope_ScopeNotUpdated_RowStillPersistsAsync(CancellationToken ct) {
    await using var context = new TwoPerspectivesDbContext(_options);
    var strategy = new PostgresUpsertStrategy();
    var listStore = new EFCorePostgresPerspectiveStore<ListModel>(context, "wh_per_list", strategy);
    var streamId = _newGuid();

    var initialScope = new PerspectiveScope { TenantId = "tenant-A" };
    var changedScope = new PerspectiveScope { TenantId = "tenant-B" };

    await listStore.UpsertAsync(streamId, new ListModel { Id = streamId, Name = "v1", Rows = [new() { Label = "v1", Value = 1 }] }, initialScope, false, ct);
    await listStore.UpsertAsync(streamId, new ListModel { Id = streamId, Name = "v2", Rows = [new() { Label = "v2", Value = 2 }] }, changedScope, false, ct);

    var saved = await context.Set<PerspectiveRow<ListModel>>().AsNoTracking().FirstOrDefaultAsync(r => r.Id == streamId, ct);
    await Assert.That(saved).IsNotNull();
    await Assert.That(saved!.Data.Name).IsEqualTo("v2");
    await Assert.That(saved.Data.Rows[0].Label).IsEqualTo("v2");
    await Assert.That(saved.Scope.TenantId).IsEqualTo("tenant-A")
      .Because("Scope is INSERT-only by default (forceUpdateScope=false); update should NOT change tenant.");
  }
}
