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
/// Guardrail regression for a production incident class observed in a consuming application:
/// EF Core 10 mis-handles the change tracker when a read model with a nested complex-JSON collection
/// (<c>Data → Shipments[] → Parcels[]</c>) is mapped via <c>ComplexProperty().ToJson()</c> — the DEFAULT
/// perspective mapping — and a pre-existing row whose stored JSON predates a newly added nested field is
/// loaded, mutated, and saved (the UPDATE path). Inserts of the new shape are fine.
/// <para>
/// This models the exact shape: <c>ParcelLike.InspectionTagIds</c> (a <c>List&lt;Guid&gt;</c>) is the
/// "new field" two complex-collection levels deep, added by a later release. Old-shape rows are produced by
/// upserting a row (so EF writes the metadata/scope JSON in its exact format) then stripping the
/// <c>InspectionTagIds</c> key from the stored <c>data</c> JSONB — i.e. a row serialized by the pre-field model.
/// </para>
/// <para>
/// These tests answer "does the incident still reproduce on the latest Whizbang?" empirically:
/// GREEN = the rearchitected save path (detach + AsNoTracking + fresh-row + Update in BaseUpsertStrategy)
/// covers it; RED = the crash is still live and needs a Whizbang workaround / EF bump.
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
    // Subscribe the coalescer hook. It only coalesces models that have a registered coalescer, so the bug
    // characterization tests (which don't register one) are unaffected; the FIX tests register one.
    PerspectiveDataCoalescer.EnsureHooked(_context);

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
        sys_created_at TIMESTAMPTZ,
        sys_updated_at TIMESTAMPTZ,
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
    // Remove the InspectionTagIds key from both parcels → the stored data JSON now lacks the field entirely.
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
  // Test 1 (control) — inserting the NEW shape works. The incident said inserts are fine; this baselines
  // that the complex-mode mapping + nested collections round-trip on the INSERT path.
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
  // Test 2 (the incident, via the REAL Whizbang path) — a pre-existing OLD-SHAPE row receives a post-deploy
  // event that populates the new nested field, saved through the Whizbang perspective UPDATE path
  // (PostgresUpsertStrategy). This is the exact production path that crashed with the old Whizbang.
  // ─────────────────────────────────────────────────────────────────────────────────────────────
  [Test]
  [Timeout(120000)]
  public async Task WhizbangUpsert_UpdateOldShapeRow_PopulatingNewNestedField_SavesCleanlyAsync(CancellationToken ct) {
    Guid id = _idProvider.NewGuid();
    await _seedOldShapeRowAsync(id);

    var strategy = new PostgresUpsertStrategy();
    // "apply new event": re-project the model with the new field now populated, upsert over the old-shape row.
    await strategy.UpsertPerspectiveRowAsync(_context!, TableName, id, _newShapeModel(withTags: true), _meta(), new PerspectiveScope(), ct);
    _context!.ChangeTracker.Clear();

    var row = await _context.Set<PerspectiveRow<OrderLikeModel>>().AsNoTracking().FirstAsync(r => r.Id == id, ct);
    await Assert.That(row.Data.Shipments[0].Parcels[1].InspectionTagIds).Count().IsEqualTo(2)
      .Because("updating an old-shape row through the Whizbang perspective save path must persist the new nested field without a change-tracker crash.");
  }

  // ─────────────────────────────────────────────────────────────────────────────────────────────
  // Test 3 (the incident, mutating the nested COLLECTION COUNT) — the crash was described as EF mis-associating
  // a parent-level collection property with a child element entry during PrepareToSave. Growing the deepest
  // collection over an old-shape row is the most direct trigger. Via the real Whizbang upsert path.
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
  // Test 4 (the LIVE residual hazard) — old-shape MATERIALIZATION. A row whose stored JSON predates the new
  // nested field materializes that field as NULL (EF Core 10 complex-mode ToJson ignores the CLR `= []`
  // initializer), NOT an empty list. So any code that touches the newly-added nested collection on a
  // pre-existing row throws NullReferenceException until the row is rewritten. This is the schema-evolution
  // hazard behind the incident, and it still reproduces on the latest Whizbang / EF Core 10.0.2.
  // ─────────────────────────────────────────────────────────────────────────────────────────────
  [Test]
  [Timeout(120000)]
  public async Task OldShapeRow_MaterializesAbsentNestedCollectionAsNull_NotEmptyAsync(CancellationToken ct) {
    Guid id = _idProvider.NewGuid();
    await _seedOldShapeRowAsync(id);

    var row = await _context!.Set<PerspectiveRow<OrderLikeModel>>().AsNoTracking().FirstAsync(r => r.Id == id, ct);

    // Parent complex collections still materialize fine...
    await Assert.That(row.Data.Shipments).IsNotNull();
    await Assert.That(row.Data.Shipments[0].Parcels).IsNotNull();
    await Assert.That(row.Data.Shipments[0].Parcels).Count().IsEqualTo(2);

    // ...but the field ABSENT from the old-shape JSON comes back NULL, not []. Accessing it (Add/iterate/Count)
    // NREs — the exact "poison-on-touch" property of pre-deploy rows the incident describes.
    await Assert.That(row.Data.Shipments[0].Parcels[0].InspectionTagIds).IsNull()
      .Because("EF Core 10 complex-mode ToJson() materializes a JSON-absent nested collection as null (not the CLR default []), so old-shape rows are poison-on-touch for the new field.");
  }

  // ─────────────────────────────────────────────────────────────────────────────────────────────
  // Test 5 (raw EF save probe) — load an old-shape row TRACKED, null-guard the absent field, mutate the Parcels
  // collection, and SaveChanges. This exercises the underlying EF change-tracker save path directly (NOT a
  // Whizbang code path — the upsert strategy deliberately avoids tracked mutation). Captures whether the save
  // itself is stable once the null field is defended.
  // ─────────────────────────────────────────────────────────────────────────────────────────────
  [Test]
  [Timeout(120000)]
  public async Task RawEfCore_LoadOldShape_NullGuardMutateParcels_SaveChanges_BehaviorAsync(CancellationToken ct) {
    Guid id = _idProvider.NewGuid();
    await _seedOldShapeRowAsync(id);

    var row = await _context!.Set<PerspectiveRow<OrderLikeModel>>().FirstAsync(r => r.Id == id, ct);
    // Defensive null-guard: overwrite the null (absent-in-JSON) field with an empty list before mutating.
    foreach (var shipment in row.Data.Shipments) {
      foreach (var parcel in shipment.Parcels) {
        parcel.InspectionTagIds = [];
      }
    }
    row.Data.Shipments[0].Parcels.Add(new ParcelLike { TrackingCode = "P3-tracked-add", InspectionTagIds = [Guid.NewGuid()] });
    row.UpdatedAt = DateTime.UtcNow;

    Exception? saveError = null;
    try {
      await _context.SaveChangesAsync(ct);
    } catch (Exception ex) {
      saveError = ex;
    }

    // CHARACTERIZATION of the LIVE EF Core 10.0.2 defect (the incident's exact failure surface): a tracked save
    // over an old-shape row throws InvalidOperationException in PrepareToSave because a complex collection
    // materialized as null (here the framework's PerspectiveScope.Extensions). This is NOT a Whizbang code path —
    // Whizbang's upsert (Tests 2/3) avoids it by never load-mutating the tracked graph and excluding Scope from
    // the UPDATE (ComplexProperty(Scope).IsModified = false). If a future EF/Whizbang release fixes this, flip
    // this assertion to IsNull() and drop the workaround.
    await Assert.That(saveError).IsNotNull()
      .Because("the underlying EF Core 10.0.2 PrepareToSave null-complex-property defect (the incident's mechanism) must still reproduce on the raw tracked-save path — this test guards that Whizbang's upsert workaround stays load-bearing.");
    await Assert.That(saveError).IsTypeOf<InvalidOperationException>();
    await Assert.That(saveError!.Message).Contains("null value when saving changes")
      .Because("this is the exact PrepareToSave failure (InvalidOperationException over a null complex collection) observed in the originating production incident.");
  }

  // The per-model coalescer a generator would emit (alongside the SplitMode hydrators already generated in
  // EFCoreServiceRegistrationGenerator): null-coalesce every nested collection in Data — and the framework's
  // PerspectiveScope.Extensions, a required complex collection that materializes null the same way.
  private static void _registerCoalescer() {
    PerspectiveDataCoalescer.Register(typeof(PerspectiveRow<OrderLikeModel>), entity => {
      var row = (PerspectiveRow<OrderLikeModel>)entity;
#pragma warning disable CS8073 // EF materializes these non-nullable collections as null — the defect being fixed
      row.Scope.Extensions ??= [];
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
#pragma warning restore CS8073
    });
  }

  // ─────────────────────────────────────────────────────────────────────────────────────────────
  // Test 6 (THE FIX — read) — with the coalescer registered, an OLD-SHAPE row reads back with an EMPTY nested
  // collection instead of null. No more poison-on-touch: consumers/Apply can read the new field safely.
  // ─────────────────────────────────────────────────────────────────────────────────────────────
  [Test]
  [Timeout(120000)]
  public async Task WithCoalescer_OldShapeRow_ReadsBackEmptyNestedCollection_NotNullAsync(CancellationToken ct) {
    _registerCoalescer();
    Guid id = _idProvider.NewGuid();
    await _seedOldShapeRowAsync(id);

    // Tracked read → ChangeTracker.Tracked fires → PerspectiveDataCoalescer coalesces the null collection to empty.
    var row = await _context!.Set<PerspectiveRow<OrderLikeModel>>().FirstAsync(r => r.Id == id, ct);

    await Assert.That(row.Data.Shipments[0].Parcels[0].InspectionTagIds).IsNotNull()
      .Because("the coalescer replaces the JSON-absent nested collection (null on EF Core 10) with an empty list.");
    await Assert.That(row.Data.Shipments[0].Parcels[0].InspectionTagIds!).Count().IsEqualTo(0)
      .Because("old-shape rows must read back with an EMPTY collection, not null — the materialization-coalesce fix.");
    await Assert.That(row.Data.Shipments[0].Parcels[1].InspectionTagIds!).Count().IsEqualTo(0)
      .Because("every parcel's new nested collection is coalesced, not just the first.");
  }

  // ─────────────────────────────────────────────────────────────────────────────────────────────
  // Test 7 (THE FIX — save) — with the coalescer, a tracked load→mutate→save over an OLD-SHAPE row SAVES
  // CLEANLY: the coalescer fills the null complex collections before PrepareToSave inspects them, so the
  // incident's InvalidOperationException never fires (compare Test 5, which throws without the coalescer).
  // ─────────────────────────────────────────────────────────────────────────────────────────────
  [Test]
  [Timeout(120000)]
  public async Task WithCoalescer_RawEfLoadMutateSave_OverOldShapeRow_SavesCleanlyAsync(CancellationToken ct) {
    _registerCoalescer();
    Guid id = _idProvider.NewGuid();
    await _seedOldShapeRowAsync(id);

    var row = await _context!.Set<PerspectiveRow<OrderLikeModel>>().FirstAsync(r => r.Id == id, ct);
    row.Data.Shipments[0].Parcels.Add(new ParcelLike { TrackingCode = "P3-tracked-add", InspectionTagIds = [Guid.NewGuid()] });
    row.UpdatedAt = DateTime.UtcNow;

    Exception? saveError = null;
    try {
      await _context.SaveChangesAsync(ct);
    } catch (Exception ex) {
      saveError = ex;
    }

    await Assert.That(saveError).IsNull()
      .Because($"with the coalescer, the previously-null complex collections are empty before save, so PrepareToSave.CheckForNullComplexProperties passes (Test 5 throws here without it). Threw: {saveError}");

    _context.ChangeTracker.Clear();
    var reread = await _context.Set<PerspectiveRow<OrderLikeModel>>().AsNoTracking().FirstAsync(r => r.Id == id, ct);
    await Assert.That(reread.Data.Shipments[0].Parcels).Count().IsEqualTo(3)
      .Because("the coalesced-and-grown collection persisted.");
  }

  // ─────────────────────────────────────────────────────────────────────────────────────────────
  // Test 8 (THE FIX — production seam) — the perspective store's GetByStreamIdAsync is the read the
  // perspective Apply path uses. It is a no-tracking read (the Tracked hook can't fire), so it routes through
  // PerspectiveDataCoalescer.CoalescedData — an old-shape row must come back with EMPTY nested collections.
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
      .Because("the store's no-tracking read routes through CoalescedData, so the Apply path never sees a null-materialized nested collection (WORKAROUND(dotnet/efcore#38625)).");
    await Assert.That(model.Shipments[0].Parcels[1].InspectionTagIds!).Count().IsEqualTo(0)
      .Because("the JSON-absent field reads back as an empty list on every parcel.");
  }

  // ─────────────────────────────────────────────────────────────────────────────────────────────
  // Test 9 (the PRODUCTION RUNNER rhythm — repeated updates, same context) — the originating incident's
  // rebuild-path crash is a THIRD facet of the EF 10 complex-JSON tracking defect family:
  //   InvalidOperationException: The property '<Collection>' belongs to the type '<Parent>', but is being
  //   used with an instance of type '<ChildElement>'  (InternalEntryBase.GetCurrentValue in PrepareToSave)
  // — parent/child complex-entry mis-association when tracked complex collections change shape across saves.
  // A perspective runner (and rebuildPerspective!) saves the SAME stream repeatedly through the SAME
  // DbContext via BaseUpsertStrategy, with the nested collections growing/shrinking between events. The
  // current strategy avoids tracked mutation entirely (detach + AsNoTracking + fresh row + Update), so this
  // sequence must never corrupt the tracker. Single-update coverage lives in Tests 2/3; this locks the
  // repeated-save rhythm the production stack trace shows.
  // ─────────────────────────────────────────────────────────────────────────────────────────────
  [Test]
  [Timeout(120000)]
  public async Task RepeatedUpserts_SameContextSameStream_GrowAndShrinkNestedCollections_NeverCorruptTrackerAsync(CancellationToken ct) {
    Guid id = _idProvider.NewGuid();
    var strategy = new PostgresUpsertStrategy();

    // v1 — insert (1 shipment / 2 parcels).
    await strategy.UpsertPerspectiveRowAsync(_context!, TableName, id, _newShapeModel(withTags: true), _meta(), new PerspectiveScope(), ct);

    // v2 — grow the deepest collection (3 parcels).
    var v2 = _newShapeModel(withTags: true);
    v2.Shipments[0].Parcels.Add(new ParcelLike { TrackingCode = "P3", InspectionTagIds = [Guid.NewGuid()] });
    await strategy.UpsertPerspectiveRowAsync(_context!, TableName, id, v2, _meta(), new PerspectiveScope(), ct);

    // v3 — shrink it (1 parcel) — shape change in the other direction.
    var v3 = _newShapeModel(withTags: true);
    v3.Shipments[0].Parcels.RemoveAt(1);
    await strategy.UpsertPerspectiveRowAsync(_context!, TableName, id, v3, _meta(), new PerspectiveScope(), ct);

    // v4 — grow the PARENT collection (2 shipments) while the child shape changes again.
    var v4 = _newShapeModel(withTags: true);
    v4.Shipments.Add(new ShipmentLike { Carrier = "Carrier B", Parcels = [new ParcelLike { TrackingCode = "B1", InspectionTagIds = [] }] });
    await strategy.UpsertPerspectiveRowAsync(_context!, TableName, id, v4, _meta(), new PerspectiveScope(), ct);

    _context!.ChangeTracker.Clear();
    var final = await _context.Set<PerspectiveRow<OrderLikeModel>>().AsNoTracking().FirstAsync(r => r.Id == id, ct);
    await Assert.That(final.Data.Shipments).Count().IsEqualTo(2)
      .Because("four same-stream saves through one DbContext with growing AND shrinking nested collections must never trip EF's complex-entry parent/child mis-association (a parent collection property reported against a child element entry).");
    await Assert.That(final.Data.Shipments[0].Parcels).Count().IsEqualTo(2);
    await Assert.That(final.Data.Shipments[1].Parcels).Count().IsEqualTo(1);
    await Assert.That(final.Version).IsEqualTo(4)
      .Because("each of the four upserts bumped the row version — all four saves committed.");
  }
  // ─────────────────────────────────────────────────────────────────────────────────────────────
  // Test 10 (characterization) — WRITE SHAPE of empty collections. EF ToJson writes empty collections with
  // the key present ("InspectionTagIds": [], "ex": []). An empty PRIMITIVE collection round-trips to [].
  // An empty framework Scope.Extensions (ComplexCollection with HasJsonPropertyName) materializes as NULL
  // even though the JSON is perfect — the quirk the coalescer's Scope line exists for. Pins both behaviors.
  // ─────────────────────────────────────────────────────────────────────────────────────────────
  [Test]
  [Timeout(120000)]
  public async Task FreshRow_EmptyCollections_WriteShapeAndMaterializationAsync(CancellationToken ct) {
    Guid id = _idProvider.NewGuid();
    var strategy = new PostgresUpsertStrategy();
    // Insert with EMPTY InspectionTagIds (withTags:false) — NO SQL stripping.
    await strategy.UpsertPerspectiveRowAsync(_context!, TableName, id, _newShapeModel(withTags: false), _meta(), new PerspectiveScope(), ct);
    _context!.ChangeTracker.Clear();

    await using var connection = new NpgsqlConnection(_connectionString);
    await connection.OpenAsync(ct);
    var dataJson = await connection.ExecuteScalarAsync<string>($"SELECT data::text FROM {TableName} WHERE id = @id", new { id });
    var scopeJson = await connection.ExecuteScalarAsync<string>($"SELECT scope::text FROM {TableName} WHERE id = @id", new { id });
    await Assert.That(dataJson!).Contains("\"InspectionTagIds\": []")
      .Because("EF ToJson writes an empty primitive collection with the key present — fresh rows are new-shape.");
    await Assert.That(scopeJson!).Contains("\"ex\": []")
      .Because("the scope JSON also carries the empty Extensions key.");

    var row = await _context.Set<PerspectiveRow<OrderLikeModel>>().AsNoTracking().FirstAsync(r => r.Id == id, ct);
    await Assert.That(row.Data.Shipments[0].Parcels[0].InspectionTagIds).IsNotNull()
      .Because("an empty PRIMITIVE collection with the key present round-trips to [], not null.");
    await Assert.That(row.Scope.Extensions).IsNull()
      .Because("EF materializes the framework's EMPTY complex collection (Scope.Extensions, ComplexCollection+HasJsonPropertyName) as null even with perfect JSON — the quirk the coalescer's Scope line neutralizes (WORKAROUND(dotnet/efcore#38625)).");
  }

  // ─────────────────────────────────────────────────────────────────────────────────────────────
  // Test 11 (characterization) — an empty nested COMPLEX collection in the Data graph ("Parcels": []): pins
  // whether it round-trips to [] on this stack (it does on the 0.290 line). If this ever flips to null,
  // rebuilt rows with empty complex collections become poison-on-touch and the coalescer is load-bearing
  // for them too.
  // ─────────────────────────────────────────────────────────────────────────────────────────────
  [Test]
  [Timeout(120000)]
  public async Task FreshRow_EmptyComplexCollection_RoundTripsToEmptyListAsync(CancellationToken ct) {
    Guid id = _idProvider.NewGuid();
    var strategy = new PostgresUpsertStrategy();
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
      .Because("an empty nested complex collection stored as [] must round-trip to an empty list.");
    await Assert.That(row.Data.Shipments[0].Parcels).Count().IsEqualTo(0);
  }
}
