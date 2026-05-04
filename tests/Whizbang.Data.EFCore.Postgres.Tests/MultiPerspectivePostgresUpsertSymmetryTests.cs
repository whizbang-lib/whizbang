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
/// Real-Postgres integration tests for the JDX 2026-05-03 production symptom: two
/// perspectives on the same stream sharing one DbContext — UberDraftJob (scalar)
/// persists, DraftJobWorkingConditions (with List&lt;RowItem&gt;) does not.
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
[NotInParallel("PostgreSQL")]
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

  /// <summary>Mirrors DraftJobName: scalar properties only — the perspective that DOES persist in JDX prod.</summary>
  public class ScalarModel {
    [StreamId]
    public Guid Id { get; set; }
    public string? FieldA { get; set; }
    public string? FieldB { get; set; }
  }

  /// <summary>Mirrors DraftJobWorkingConditions: scalar fields plus a List&lt;T&gt; property
  /// that becomes a JSONB array. The perspective that does NOT persist in JDX prod.</summary>
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
        await adminConnection.ExecuteAsync($"DROP DATABASE IF EXISTS {_testDatabaseName}");
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
        version INTEGER NOT NULL
      );
      CREATE TABLE IF NOT EXISTS wh_per_list (
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

  // ===========================================================================
  // TESTS — production code path (PostgresUpsertStrategy + real Postgres + JSONB)
  // ===========================================================================

  /// <summary>
  /// Direct JDX scenario: two perspectives, scalar + list-property, both upsert
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
      .Because("List perspective row must ALSO persist symmetrically. Asymmetry here is the JDX UI loader bug.");
    await Assert.That(savedScalar!.Data.FieldA).IsEqualTo("a");
    await Assert.That(savedList!.Data.Rows.Count).IsEqualTo(2);
    await Assert.That(savedList.Data.Rows[0].Label).IsEqualTo("first");
  }

  /// <summary>
  /// Reverse order: list-perspective FIRST, then scalar. Catches a regression where
  /// the second upsert is what's lost (i.e., the scalar perspective happens to be
  /// what's "always saved last" in JDX, masking a list-second bug).
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
      await Assert.That(savedList).IsNotNull().Because($"List row missing for stream {sid} — JDX symptom on this stream.");
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
}
