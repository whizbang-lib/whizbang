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
/// Guardrail regression for a production incident class observed in a consuming application,
/// backported to the 0.290 line. EF Core 10 (through 10.0.10) materializes
/// nested complex-JSON collections as <see langword="null"/> under the DEFAULT perspective mapping
/// (<c>ComplexProperty().ToJson()</c>) in three flavors — the stored key is ABSENT (schema evolution:
/// pre-upgrade rows), the stored value is JSON <c>null</c>, or the collection is a complex-element collection
/// stored as EMPTY <c>[]</c> (which is why a truncate + rebuild does not stick: freshly rebuilt rows with an
/// empty nested complex collection are re-poisoned on their very next read). Filed upstream as
/// WORKAROUND(dotnet/efcore#38625).
/// <para>
/// The production crash chain: the generated perspective runner loads the current model via
/// <c>IPerspectiveStore.GetByStreamIdAsync</c> → the materialized graph carries null collections → Apply
/// mutates that graph → the upsert saves it → <c>PrepareToSave</c> walks the nulls and throws
/// (<c>InvalidOperationException</c>: null required complex property, or the parent/child mis-association
/// "property '<Collection>' belongs to <Parent> … used with … <ChildElement>").
/// </para>
/// </summary>
[Category("Integration")]
[NotInParallel("PostgreSQL")]
public class OrderSchemaEvolutionComplexTypeTests : IAsyncDisposable {
  private static readonly Uuid7IdProvider _idProvider = new();

  static OrderSchemaEvolutionComplexTypeTests() {
    AppContext.SetSwitch("Npgsql.EnableLegacyTimestampBehavior", false);
  }

  private const string TableName = "wh_per_order_evo_test";

  private string? _testDatabaseName;
  private NpgsqlDataSource? _dataSource;
  private OrderEvoDbContext? _context;
  private string _connectionString = null!;

  // ── The read model: two complex-collection levels deep (Data → Shipments[] → Parcels[]). ──
  public class OrderLikeModel {
    public string Name { get; set; } = string.Empty;
    public List<ShipmentLike> Shipments { get; set; } = [];
  }

  public class ShipmentLike {
    public string Carrier { get; set; } = string.Empty;
    public List<ParcelLike> Parcels { get; set; } = [];
  }

  public class ParcelLike {
    public string TrackingCode { get; set; } = string.Empty;
    // The "new field" added by a later release, two complex-collection levels deep. Absent from old-shape rows.
    public List<Guid> InspectionTagIds { get; set; } = [];
  }

  /// <summary>
  /// DbContext using the DEFAULT complex-mode mapping — <c>ComplexProperty().ToJson()</c> — exactly as
  /// the generator's <c>PerspectiveEntityConfiguration</c> snippet emits for a non-polymorphic, non-Split model.
  /// </summary>
  private sealed class OrderEvoDbContext(DbContextOptions<OrderSchemaEvolutionComplexTypeTests.OrderEvoDbContext> options) : DbContext(options) {
    protected override void OnModelCreating(ModelBuilder modelBuilder) {
      base.OnModelCreating(modelBuilder);

      modelBuilder.Entity<PerspectiveRow<OrderLikeModel>>(entity => {
        entity.ToTable(TableName);
        entity.HasKey(e => e.Id);
        entity.Property(e => e.Id).HasColumnName("id");

        // DEFAULT complex-mode mapping (the vulnerable one). EF Core 10 recursively maps the nested
        // complex collections (Shipments → Parcels) into the single "data" JSONB column.
        entity.ComplexProperty(e => e.Data, d => d.ToJson("data"));
        entity.ComplexProperty(e => e.Metadata, m => m.ToJson("metadata"));
        entity.ComplexProperty(e => e.Scope, s => {
          s.ToJson("scope");
          s.ComplexCollection(p => p.Extensions, ex => ex.HasJsonPropertyName("ex"));
        });

        entity.Property(e => e.CreatedAt).HasColumnName("created_at").IsRequired();
        entity.Property(e => e.UpdatedAt).HasColumnName("updated_at").IsRequired();
        entity.Property(e => e.Version).HasColumnName("version").IsRequired();
        entity.HasIndex(e => e.CreatedAt);
      });
    }
  }

  [Before(Test)]
  public async Task SetupAsync() {
    await SharedPostgresContainer.InitializeAsync();

    _testDatabaseName = $"order_evo_test_{Guid.NewGuid():N}";
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

    var optionsBuilder = new DbContextOptionsBuilder<OrderEvoDbContext>();
    optionsBuilder
        .UseNpgsql(_dataSource)
        .ConfigureWarnings(w => w.Ignore(Microsoft.EntityFrameworkCore.Diagnostics.CoreEventId.ManyServiceProvidersCreatedWarning));
    _context = new OrderEvoDbContext(optionsBuilder.Options);

    await _initializeSchemaAsync();
  }

  [After(Test)]
  public async Task TeardownAsync() {
    PerspectiveDataCoalescer.Clear();  // global registry — reset so tests stay independent
    if (_context != null) { await _context.DisposeAsync(); _context = null; }
    if (_dataSource != null) { await _dataSource.DisposeAsync(); _dataSource = null; }
    if (_testDatabaseName != null) {
      try {
        await using var adminConnection = new NpgsqlConnection(SharedPostgresContainer.ConnectionString);
        await adminConnection.OpenAsync();
        await adminConnection.ExecuteAsync($@"
          SELECT pg_terminate_backend(pg_stat_activity.pid)
          FROM pg_stat_activity
          WHERE pg_stat_activity.datname = '{_testDatabaseName}' AND pid <> pg_backend_pid()");
        await adminConnection.ExecuteAsync($"DROP DATABASE IF EXISTS {_testDatabaseName}");
      } catch { /* ignore cleanup errors */ }
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
    await connection.ExecuteAsync($"""
      CREATE TABLE IF NOT EXISTS {TableName} (
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

  private static PerspectiveMetadata _meta() => new() {
    EventType = "ParcelChanged",
    EventId = Guid.NewGuid().ToString(),
    Timestamp = DateTime.UtcNow,
  };

  private static OrderLikeModel _newShapeModel(bool withTags) => new() {
    Name = "ORD-1",
    Shipments = [
      new ShipmentLike {
        Carrier = "Carrier A",
        Parcels = [
          new ParcelLike { TrackingCode = "P1", InspectionTagIds = withTags ? [Guid.NewGuid()] : [] },
          new ParcelLike { TrackingCode = "P2", InspectionTagIds = withTags ? [Guid.NewGuid(), Guid.NewGuid()] : [] },
        ],
      },
    ],
  };

  /// <summary>
  /// Produces an OLD-SHAPE row: upsert a row (EF writes data/metadata/scope in its exact JSON format), then
  /// strip the <c>InspectionTagIds</c> key from every stored parcel so the persisted JSON predates the field —
  /// exactly a pre-upgrade row. Verifies the strip matched EF's JSON casing.
  /// </summary>
  private async Task _seedOldShapeRowAsync(Guid id) {
    var strategy = new PostgresUpsertStrategy();
    await strategy.UpsertPerspectiveRowAsync(_context!, TableName, id, _newShapeModel(withTags: false), _meta(), new PerspectiveScope());
    _context!.ChangeTracker.Clear();

    await using var connection = new NpgsqlConnection(_connectionString);
    await connection.OpenAsync();
    await connection.ExecuteAsync($$"""
      UPDATE {{TableName}}
      SET data = data #- '{Shipments,0,Parcels,0,InspectionTagIds}' #- '{Shipments,0,Parcels,1,InspectionTagIds}'
      WHERE id = @id;
      """, new { id });

    var dataJson = await connection.ExecuteScalarAsync<string>($"SELECT data::text FROM {TableName} WHERE id = @id", new { id });
    await Assert.That(dataJson).IsNotNull();
    await Assert.That(dataJson!).DoesNotContain("InspectionTagIds")
      .Because("the seeded row must be genuinely old-shape — if the strip missed (JSON casing mismatch), the repro is invalid.");
  }

  // ─────────────────────────────────────────────────────────────────────────────────────────────
  // Test 1 (control) — inserting the NEW shape works (the incident said inserts are fine).
  // ─────────────────────────────────────────────────────────────────────────────────────────────
  [Test]
  [Timeout(120000)]
  public async Task Insert_NewShape_ComplexModeNestedCollections_SavesAndReadsBackAsync(CancellationToken ct) {
    Guid id = _idProvider.NewGuid();
    var strategy = new PostgresUpsertStrategy();

    await strategy.UpsertPerspectiveRowAsync(_context!, TableName, id, _newShapeModel(withTags: true), _meta(), new PerspectiveScope(), ct);
    _context!.ChangeTracker.Clear();

    var row = await _context.Set<PerspectiveRow<OrderLikeModel>>().AsNoTracking().FirstAsync(r => r.Id == id, ct);
    await Assert.That(row.Data.Shipments).Count().IsEqualTo(1);
    await Assert.That(row.Data.Shipments[0].Parcels).Count().IsEqualTo(2);
    await Assert.That(row.Data.Shipments[0].Parcels[1].InspectionTagIds).Count().IsEqualTo(2)
      .Because("the new nested field must round-trip through complex-mode JSON on insert.");
  }

  // ─────────────────────────────────────────────────────────────────────────────────────────────
  // Test 2 — Whizbang upsert UPDATE over an OLD-SHAPE row, populating the new nested field.
  // ─────────────────────────────────────────────────────────────────────────────────────────────
  [Test]
  [Timeout(120000)]
  public async Task WhizbangUpsert_UpdateOldShapeRow_PopulatingNewNestedField_SavesCleanlyAsync(CancellationToken ct) {
    Guid id = _idProvider.NewGuid();
    await _seedOldShapeRowAsync(id);

    var strategy = new PostgresUpsertStrategy();
    await strategy.UpsertPerspectiveRowAsync(_context!, TableName, id, _newShapeModel(withTags: true), _meta(), new PerspectiveScope(), ct);
    _context!.ChangeTracker.Clear();

    var row = await _context.Set<PerspectiveRow<OrderLikeModel>>().AsNoTracking().FirstAsync(r => r.Id == id, ct);
    await Assert.That(row.Data.Shipments[0].Parcels[1].InspectionTagIds).Count().IsEqualTo(2)
      .Because("updating an old-shape row through the Whizbang perspective save path must persist the new nested field without a change-tracker crash.");
  }

  // ─────────────────────────────────────────────────────────────────────────────────────────────
  // Test 3 — Whizbang upsert UPDATE over an OLD-SHAPE row, growing the deepest collection.
  // ─────────────────────────────────────────────────────────────────────────────────────────────
  [Test]
  [Timeout(120000)]
  public async Task WhizbangUpsert_UpdateOldShapeRow_GrowingDeepestCollection_SavesCleanlyAsync(CancellationToken ct) {
    Guid id = _idProvider.NewGuid();
    await _seedOldShapeRowAsync(id);

    var grown = _newShapeModel(withTags: true);
    grown.Shipments[0].Parcels.Add(new ParcelLike { TrackingCode = "P3-new", InspectionTagIds = [Guid.NewGuid()] });

    var strategy = new PostgresUpsertStrategy();
    await strategy.UpsertPerspectiveRowAsync(_context!, TableName, id, grown, _meta(), new PerspectiveScope(), ct);
    _context!.ChangeTracker.Clear();

    var row = await _context.Set<PerspectiveRow<OrderLikeModel>>().AsNoTracking().FirstAsync(r => r.Id == id, ct);
    await Assert.That(row.Data.Shipments[0].Parcels).Count().IsEqualTo(3)
      .Because("growing the deepest nested collection over an old-shape row must save without the PrepareToSave parent/child mis-association.");
  }

  // ─────────────────────────────────────────────────────────────────────────────────────────────
  // Test 4 (LIVE hazard characterization) — old-shape MATERIALIZATION: the JSON-absent nested collection
  // reads back as NULL, not []. Pins the EF Core 10.0.2 behavior this workaround exists for.
  // ─────────────────────────────────────────────────────────────────────────────────────────────
  [Test]
  [Timeout(120000)]
  public async Task OldShapeRow_MaterializesAbsentNestedCollectionAsNull_NotEmptyAsync(CancellationToken ct) {
    Guid id = _idProvider.NewGuid();
    await _seedOldShapeRowAsync(id);

    var row = await _context!.Set<PerspectiveRow<OrderLikeModel>>().AsNoTracking().FirstAsync(r => r.Id == id, ct);

    await Assert.That(row.Data.Shipments).IsNotNull();
    await Assert.That(row.Data.Shipments[0].Parcels).IsNotNull();
    await Assert.That(row.Data.Shipments[0].Parcels).Count().IsEqualTo(2);
    await Assert.That(row.Data.Shipments[0].Parcels[0].InspectionTagIds).IsNull()
      .Because("EF Core 10 complex-mode ToJson() materializes a JSON-absent nested collection as null (not the CLR default []), so old-shape rows are poison-on-touch for the new field.");
  }

  // ─────────────────────────────────────────────────────────────────────────────────────────────
  // Test 5 (characterization) — EMPTY COMPLEX collection round-trips correctly on this stack: a shipment with
  // Parcels stored as [] reads back as an empty list, NOT null. (The Data-graph poison on this line is the
  // ABSENT key, i.e. old-shape rows — Test 4.) Pins the behavior so a regression to null-materialization of
  // empty complex collections would be caught here.
  // ─────────────────────────────────────────────────────────────────────────────────────────────
  [Test]
  [Timeout(120000)]
  public async Task FreshRow_EmptyComplexCollection_RoundTripsToEmptyListAsync(CancellationToken ct) {
    Guid id = _idProvider.NewGuid();
    var strategy = new PostgresUpsertStrategy();
    // A shipment with EMPTY Parcels — written by the CURRENT code, perfect JSON ("Parcels": []).
    var model = new OrderLikeModel { Name = "ORD-empty", Shipments = [new ShipmentLike { Carrier = "Carrier A", Parcels = [] }] };
    await strategy.UpsertPerspectiveRowAsync(_context!, TableName, id, model, _meta(), new PerspectiveScope(), ct);
    _context!.ChangeTracker.Clear();

    await using var connection = new NpgsqlConnection(_connectionString);
    await connection.OpenAsync(ct);
    var dataJson = await connection.ExecuteScalarAsync<string>($"SELECT data::text FROM {TableName} WHERE id = @id", new { id });
    await Assert.That(dataJson!).Contains("\"Parcels\": []")
      .Because("the stored JSON has the key present and empty.");

    var row = await _context.Set<PerspectiveRow<OrderLikeModel>>().AsNoTracking().FirstAsync(r => r.Id == id, ct);
    await Assert.That(row.Data.Shipments[0].Parcels).IsNotNull()
      .Because("an empty nested complex collection stored as [] must round-trip to an empty list on this stack.");
    await Assert.That(row.Data.Shipments[0].Parcels).Count().IsEqualTo(0);
  }

  // The per-model coalescer the (backported) generator emits for real perspectives — registered manually here
  // because this ad-hoc test model isn't a generator-discovered perspective. WORKAROUND(dotnet/efcore#38625).
  private static void _registerCoalescer() {
    PerspectiveDataCoalescer.Register(typeof(PerspectiveRow<OrderLikeModel>), entity => {
      var row = (PerspectiveRow<OrderLikeModel>)entity;
      if (row.Scope is not null) { row.Scope.Extensions ??= []; }
      var data = row.Data;
      if (data is null) {
        return;
      }
      data.Shipments ??= [];
      foreach (var shipment in data.Shipments) {
        shipment.Parcels ??= [];
        foreach (var parcel in shipment.Parcels) {
          parcel.InspectionTagIds ??= [];
        }
      }
    });
  }

  // ─────────────────────────────────────────────────────────────────────────────────────────────
  // Test 6 (THE PROD CRASH CHAIN — runner rhythm) — exactly what the generated perspective runner does:
  // load the current model via IPerspectiveStore.GetByStreamIdAsync, mutate it like Apply, save via the
  // upsert, then load again (the NEXT event's read). Seeded with the true prod poison: an OLD-SHAPE row
  // whose stored JSON predates InspectionTagIds. Unpatched, the loaded graph carries null collections —
  // Apply code touching them NREs, saves can trip PrepareToSave, and the null persists forever (a plain
  // rebuild writes it right back). RED before the coalescer backport, GREEN after.
  // ─────────────────────────────────────────────────────────────────────────────────────────────
  [Test]
  [Timeout(120000)]
  public async Task RunnerChain_StoreLoad_ApplyMutate_Upsert_Reload_OverOldShapeRow_StaysHealthyAsync(CancellationToken ct) {
    _registerCoalescer();
    Guid id = _idProvider.NewGuid();
    await _seedOldShapeRowAsync(id);
    var strategy = new PostgresUpsertStrategy();

    // 1. Load current model the way the generated runner does (IPerspectiveStore.GetByStreamIdAsync).
    var store = new EFCorePostgresPerspectiveStore<OrderLikeModel>(_context!, TableName);
    var current = await store.GetByStreamIdAsync(id, ct);
    await Assert.That(current).IsNotNull();

    // 2. Apply-style mutation that does NOT touch the (possibly null-materialized) collections — like an
    //    event that only renames the order. Any poison rides along silently into the save.
    current!.Name = "ORD-chain-updated";

    // 3. Save through the perspective upsert — the incident's exact save path
    //    (runner.SaveModelAndCheckpointAsync → BaseUpsertStrategy → SaveChangesAsync → PrepareToSave).
    await strategy.UpsertPerspectiveRowAsync(_context!, TableName, id, current, _meta(), new PerspectiveScope(), ct);
    _context!.ChangeTracker.Clear();

    // 4. The NEXT event's read — the model handed to the next Apply must be fully healthy: mutation
    //    persisted AND no null collections anywhere (otherwise the next Apply/save is the production retry storm).
    var next = await store.GetByStreamIdAsync(id, ct);
    await Assert.That(next!.Name).IsEqualTo("ORD-chain-updated")
      .Because("the runner chain (store load → Apply mutate → upsert save) over an old-shape row must save cleanly — this is the originating production crash chain.");
    await Assert.That(next.Shipments[0].Parcels[0].InspectionTagIds).IsNotNull()
      .Because("after a full runner cycle the model handed to the NEXT Apply must carry no null collections — unpatched, the old-shape null persists forever and every touch re-runs the hazard.");
  }

  // ─────────────────────────────────────────────────────────────────────────────────────────────
  // Test 7 (store seam) — GetByStreamIdAsync over an OLD-SHAPE row must return a model whose new nested
  // field is EMPTY, not null, so Apply code can touch it without NREs. RED before the coalescer backport.
  // ─────────────────────────────────────────────────────────────────────────────────────────────
  [Test]
  [Timeout(120000)]
  public async Task PerspectiveStore_GetByStreamId_OverOldShapeRow_ReturnsCoalescedModelAsync(CancellationToken ct) {
    _registerCoalescer();
    Guid id = _idProvider.NewGuid();
    await _seedOldShapeRowAsync(id);

    var store = new EFCorePostgresPerspectiveStore<OrderLikeModel>(_context!, TableName);
    var model = await store.GetByStreamIdAsync(id, ct);

    await Assert.That(model).IsNotNull();
    await Assert.That(model!.Shipments[0].Parcels[0].InspectionTagIds).IsNotNull()
      .Because("the store read must coalesce null-materialized nested collections so the Apply path never sees them (WORKAROUND(dotnet/efcore#38625)).");
    await Assert.That(model.Shipments[0].Parcels[1].InspectionTagIds!).Count().IsEqualTo(0)
      .Because("the JSON-absent field reads back as an empty list on every parcel.");
  }
}
