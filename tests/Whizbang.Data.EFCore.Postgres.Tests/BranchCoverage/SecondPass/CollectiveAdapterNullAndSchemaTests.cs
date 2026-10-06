// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using System.Data.Common;
using System.Linq.Expressions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Npgsql;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Lenses;
using Whizbang.Core.Messaging;
using Whizbang.Core.Perspectives;
using Whizbang.Data.EFCore.Postgres.Collective;
using Whizbang.Data.Postgres.Collective;
using Whizbang.Testing.Containers;

namespace Whizbang.Data.EFCore.Postgres.Tests.BranchCoverage.SecondPass;

/// <summary>
/// The collective adapter's remaining shapes against a real server: a perspective table mapped with an
/// explicit schema, a non-positive batch size, physical setters that assign null (a plain column and a
/// vector column), a computed comparison against null, and a cohort condition whose bound value is
/// null.
/// </summary>
/// <remarks>
/// <para>
/// The fixture follows <c>CollectivePhysicalColumnIntegrationTests</c> (same shard, so a branch whose
/// other arm that class takes is measured in the same coverage report), with its own Split model and
/// a context that maps the table into the <c>public</c> schema explicitly. Rows are seeded the way the
/// generated runner writes them.
/// </para>
/// <para>
/// Each null case asserts what lands in the row: a null assignment must clear the column rather than
/// fail on an untyped parameter, a comparison against null must behave as a null test, and an
/// ordering condition against a null value must match nothing.
/// </para>
/// </remarks>
/// <code-under-test>src/Whizbang.Data.EFCore.Postgres/Collective/EFCoreCollectiveAdapter.cs</code-under-test>
[Category("Integration")]
[Category("CollectiveEvents")]
[NotInParallel("EFCorePostgresTests")]
[Category("Shard3")]
public class CollectiveAdapterNullAndSchemaTests : IAsyncDisposable {
  private const string TABLE = "wh_per_nullable_ticket";
  private const string SCHEMA = "public";
  private const string TENANT = "t-null";

  static CollectiveAdapterNullAndSchemaTests() {
    AppContext.SetSwitch("Npgsql.EnableLegacyTimestampBehavior", false);
    // What the perspective runner's [ModuleInitializer] registers for this model.
    PerspectivePhysicalFieldRegistry.Register(typeof(NullableTicketModel), nameof(NullableTicketModel.Lane), "lane", FieldStorageMode.Split);
    PerspectivePhysicalFieldRegistry.Register(typeof(NullableTicketModel), nameof(NullableTicketModel.Priority), "prio", FieldStorageMode.Split);
    PerspectivePhysicalFieldRegistry.Register(typeof(NullableTicketModel), nameof(NullableTicketModel.Embedding), "embedding", FieldStorageMode.Split, isVector: true);
  }

  private string? _databaseName;
  private string _connectionString = null!;
  private NpgsqlDataSource? _dataSource;
  private readonly List<string> _capturedSql = [];

  /// <summary>
  /// Null assignments to a plain physical column and to a vector column clear both columns, on a table
  /// the model maps with an explicit schema, with a batch size of zero falling back to the default.
  /// </summary>
  [Test]
  public async Task Apply_NullAssignmentsOnASchemaQualifiedTable_ClearTheColumnsAsync() {
    var id = Guid.NewGuid();
    await _runnerWriteAsync(id, new NullableTicketModel { Lane = "cold", Priority = 1, Embedding = [1f, 2f, 3f] });

    var affected = await _applyAsync(
      new NullableTicketSpec(s => s.SetProperty(t => t.Lane, (string?)null).SetProperty(t => t.Embedding, (float[]?)null)),
      new CollectiveApplyOptions { BatchSize = 0 });

    await Assert.That(affected).IsEqualTo(1)
      .Because("a non-positive batch size falls back to the default rather than selecting nothing");
    await using var conn = await _openAsync();
    await Assert.That(await conn.QuerySingleAsync<bool>($"SELECT lane IS NULL FROM {TABLE} WHERE id = @id", new { id })).IsTrue()
      .Because("a null assignment to a plain column binds a database null, which clears the column");
    await Assert.That(await conn.QuerySingleAsync<bool>($"SELECT embedding IS NULL FROM {TABLE} WHERE id = @id", new { id })).IsTrue()
      .Because("a null vector is not a float array, so it binds a database null rather than an empty vector");
    await Assert.That(_updateStatements().Single()).Contains($"\"{SCHEMA}\".\"{TABLE}\"")
      .Because("a table mapped with a schema is written schema-qualified");
  }

  /// <summary>
  /// A computed comparison against null is a null test: the flag is set on the row whose column is null
  /// and cleared on the row whose column has a value.
  /// </summary>
  [Test]
  public async Task Apply_ComputedComparisonAgainstNull_IsANullTestAsync() {
    var unassigned = Guid.NewGuid();
    var assigned = Guid.NewGuid();
    await _runnerWriteAsync(unassigned, new NullableTicketModel { Lane = null, Priority = 1 });
    await _runnerWriteAsync(assigned, new NullableTicketModel { Lane = "hot", Priority = 1 });

    await _applyAsync(new NullableTicketSpec(s => s.SetProperty(t => t.IsUnassigned, t => t.Lane == null)), CollectiveApplyOptions.Default);

    await using var conn = await _openAsync();
    await Assert.That(await conn.QuerySingleAsync<bool>($"SELECT (data->>'IsUnassigned')::boolean FROM {TABLE} WHERE id = @id", new { id = unassigned }))
      .IsTrue();
    await Assert.That(await conn.QuerySingleAsync<bool>($"SELECT (data->>'IsUnassigned')::boolean FROM {TABLE} WHERE id = @id", new { id = assigned }))
      .IsFalse();
  }

  /// <summary>
  /// An ordering condition against a null value is unknown for every row, as a lifted comparison with
  /// a null operand is false in C#, so the cohort is empty and no row changes.
  /// </summary>
  [Test]
  public async Task Apply_OrderingConditionAgainstANullValue_MatchesNothingAsync() {
    var id = Guid.NewGuid();
    await _runnerWriteAsync(id, new NullableTicketModel { Lane = "cold", Priority = 1, Title = "Original" });
    int? noLimit = null;

    var affected = await _applyAsync(
      new NullableTicketSpec(s => s.SetProperty(t => t.Title, "Changed"), r => r.Data.Priority < noLimit),
      CollectiveApplyOptions.Default);

    await Assert.That(affected).IsEqualTo(0);
    await using var conn = await _openAsync();
    await Assert.That(await conn.QuerySingleAsync<string>($"SELECT data->>'Title' FROM {TABLE} WHERE id = @id", new { id }))
      .IsEqualTo("Original");
  }

  // ── Model, spec, event ────────────────────────────────────────────────────────────────────────

  [PerspectiveStorage(FieldStorageMode.Split)]
  internal sealed class NullableTicketModel {
    [PhysicalField] public string? Lane { get; set; }
    [PhysicalField(ColumnName = "prio")] public int Priority { get; set; }
    [VectorField(3)] public float[]? Embedding { get; set; }
    public string Title { get; set; } = "";
    public bool IsUnassigned { get; set; }
  }

  private sealed record NullableTicketSpec(
      Expression<Action<ICollectiveSetters<NullableTicketModel>>> Setters,
      Expression<Func<PerspectiveRow<NullableTicketModel>, bool>>? Where = null) : ICollectiveSpec<NullableTicketModel>;

  internal sealed record NullableCollectiveEvent : ICollectiveEvent {
    public required CollectiveScope Scope { get; init; }
  }

  private sealed class SpecHandler;

  private async Task<int> _applyAsync(ICollectiveSpec<NullableTicketModel> spec, CollectiveApplyOptions options) {
    var entry = new CollectiveApplyEntry(
      typeof(NullableTicketModel), typeof(NullableCollectiveEvent), typeof(SpecHandler), "Apply",
      CollectiveScopeHandling.Framework, CollectiveSpecKind.Linq, (_, _, _) => spec);
    await using var ctx = _newContext();
    _capturedSql.Clear();
    return await CollectiveEventApplier<NullableTicketModel>.ApplyAsync(
      entry, new SpecHandler(), new NullableCollectiveEvent { Scope = new TenantCollectiveScope(TENANT) },
      new TenantCollectiveScopeResolver(), ctx, Guid.NewGuid(), options);
  }

  private List<string> _updateStatements() =>
    [.. _capturedSql.Where(sql => sql.TrimStart().StartsWith("UPDATE", StringComparison.Ordinal))];

  // ── Writing a row the way the generated runner does ───────────────────────────────────────────

  private async Task _runnerWriteAsync(Guid id, NullableTicketModel model) {
    // Generated for a Split model: the physical values go to their columns, the document holds the defaults.
    var physicalFieldValues = new Dictionary<string, object?> {
      { "lane", model.Lane },
      { "prio", model.Priority },
      { "embedding", model.Embedding != null ? new Pgvector.Vector(model.Embedding) : null },
    };
    var document = new NullableTicketModel {
      Lane = default,
      Priority = default,
      Embedding = [],
      Title = model.Title,
      IsUnassigned = model.IsUnassigned,
    };
    await using var ctx = _newContext();
    var metadata = new PerspectiveMetadata { EventType = "Seeded", EventId = Guid.NewGuid().ToString(), Timestamp = DateTime.UtcNow };
    await new PostgresUpsertStrategy().UpsertPerspectiveRowWithPhysicalFieldsAsync(
      ctx, TABLE, id, document, metadata, new PerspectiveScope { TenantId = TENANT }, physicalFieldValues);
  }

  private async Task<NpgsqlConnection> _openAsync() {
    var conn = new NpgsqlConnection(_connectionString);
    await conn.OpenAsync();
    return conn;
  }

  // ── Setup / teardown ──────────────────────────────────────────────────────────────────────────

  [Before(Test)]
  public async Task SetupAsync() {
    await SharedPostgresContainer.InitializeAsync();
    _databaseName = $"test_collective_nullable_{Guid.NewGuid():N}";
    await using (var admin = new NpgsqlConnection(SharedPostgresContainer.ConnectionString)) {
      await admin.OpenAsync();
      await admin.ExecuteAsync($"CREATE DATABASE {_databaseName}");
    }
    _connectionString = new NpgsqlConnectionStringBuilder(SharedPostgresContainer.ConnectionString) {
      Database = _databaseName,
      Timezone = "UTC",
      IncludeErrorDetail = true,
    }.ConnectionString;

    var dataSourceBuilder = new NpgsqlDataSourceBuilder(_connectionString);
    // Reflection-based options: the fixture model is a nested test type no generated JSON context covers.
    dataSourceBuilder.ConfigureJsonOptions(new System.Text.Json.JsonSerializerOptions());
    dataSourceBuilder.EnableDynamicJson();
    dataSourceBuilder.UseVector();
    _dataSource = dataSourceBuilder.Build();

    await using var conn = await _openAsync();
    await conn.ExecuteAsync("CREATE EXTENSION IF NOT EXISTS vector");
    await conn.ReloadTypesAsync();
    await conn.ExecuteAsync($"""
      CREATE TABLE {SCHEMA}.{TABLE} (
        id UUID PRIMARY KEY, data JSONB NOT NULL, metadata JSONB NOT NULL, scope JSONB NOT NULL,
        created_at TIMESTAMPTZ NOT NULL, updated_at TIMESTAMPTZ NOT NULL, version INTEGER NOT NULL,
        lane TEXT, prio INTEGER NOT NULL DEFAULT 0, embedding vector(3));
      """);
  }

  [After(Test)]
  public async Task TeardownAsync() {
    if (_dataSource is not null) {
      await _dataSource.DisposeAsync();
      _dataSource = null;
    }
    if (_databaseName is not null) {
      await using var admin = new NpgsqlConnection(SharedPostgresContainer.ConnectionString);
      await admin.OpenAsync();
      await admin.ExecuteAsync($"DROP DATABASE IF EXISTS {_databaseName} WITH (FORCE)");
      _databaseName = null;
    }
  }

  public async ValueTask DisposeAsync() {
    await TeardownAsync();
    GC.SuppressFinalize(this);
  }

  private NullableDbContext _newContext() {
    var options = new DbContextOptionsBuilder<NullableDbContext>()
      .UseNpgsql(_dataSource!, o => o.UseVector())
      .AddInterceptors(new SqlCapture(_capturedSql))
      .ConfigureWarnings(w => w.Ignore(CoreEventId.ManyServiceProvidersCreatedWarning))
      .Options;
    return new NullableDbContext(options);
  }

  private sealed class NullableDbContext(DbContextOptions<NullableDbContext> options) : DbContext(options) {
    protected override void OnModelCreating(ModelBuilder modelBuilder) {
      base.OnModelCreating(modelBuilder);
      modelBuilder.Entity<PerspectiveRow<NullableTicketModel>>(e => {
        e.ToTable(TABLE, SCHEMA);
        e.HasKey(x => x.Id);
        e.Property(x => x.Id).HasColumnName("id");
        e.Property(x => x.Data).HasColumnName("data").HasColumnType("jsonb");
        e.Property(x => x.Metadata).HasColumnName("metadata").HasColumnType("jsonb");
        e.Property(x => x.Scope).HasColumnName("scope").HasColumnType("jsonb");
        e.Property(x => x.CreatedAt).HasColumnName("created_at");
        e.Property(x => x.UpdatedAt).HasColumnName("updated_at");
        e.Property(x => x.Version).HasColumnName("version");
        e.Property<string?>("lane").HasColumnName("lane");
        e.Property<int>("prio").HasColumnName("prio");
        e.Property<Pgvector.Vector?>("embedding").HasColumnName("embedding").HasColumnType("vector(3)");
      });
    }
  }

  private sealed class SqlCapture(List<string> captured) : DbCommandInterceptor {
    public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(
        DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result,
        CancellationToken cancellationToken = default) {
      captured.Add(command.CommandText);
      return base.ReaderExecutingAsync(command, eventData, result, cancellationToken);
    }

    public override ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(
        DbCommand command, CommandEventData eventData, InterceptionResult<int> result,
        CancellationToken cancellationToken = default) {
      captured.Add(command.CommandText);
      return base.NonQueryExecutingAsync(command, eventData, result, cancellationToken);
    }
  }
}
