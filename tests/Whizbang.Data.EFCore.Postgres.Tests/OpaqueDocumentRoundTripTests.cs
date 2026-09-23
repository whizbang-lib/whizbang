using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using TUnit.Assertions;
using TUnit.Assertions.Enums;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core;
using Whizbang.Core.Lenses;
using Whizbang.Core.Perspectives;
using Whizbang.Core.Serialization;
using Whizbang.Data.EFCore.Postgres.Functions;
using Whizbang.Data.EFCore.Postgres.Perspectives;
using Whizbang.Testing.Containers;

namespace Whizbang.Data.EFCore.Postgres.Tests;

/// <summary>A nested positional record, which the mapped path cannot construct inside a collection.</summary>
public sealed record OpaqueAttachment(Guid UploadId, string FileName);

/// <summary>A turn, holding a temporal the mapped path would never have seen.</summary>
public sealed record OpaqueTurn(Guid TurnId, string Content, DateTime At, IReadOnlyList<OpaqueAttachment>? Attachments);

/// <summary>
/// A model the mapped path refuses, so its document is stored as one serialized value.
/// </summary>
[SuppressIndexAdvisory("the ordering case below accepts the scan; what it does not accept is a statement that cannot run")]
public sealed class OpaqueDocument {
  [StreamId]
  public Guid Id { get; set; }
  public DateTime StartedAt { get; set; }
  public DateTimeOffset? EndedAt { get; set; }
  public List<OpaqueTurn> Turns { get; set; } = [];
}

/// <summary>Source-generated metadata for the opaque model, the way a consumer's is.</summary>
[JsonSerializable(typeof(OpaqueDocument))]
public sealed partial class OpaqueDocumentJsonContext : JsonSerializerContext;

/// <summary>Registers the opaque model's metadata with the registry once, for every test that needs it.</summary>
public static class OpaqueDocumentFixture {
  private static readonly Lazy<bool> _registered = new(() => {
    JsonContextRegistry.RegisterContext(OpaqueDocumentJsonContext.Default);
    return true;
  });

  /// <summary>Ensures the model resolves through the registry's union, as a consumer's does.</summary>
  public static void EnsureRegistered() => _ = _registered.Value;
}

/// <summary>
/// That a document stored as one serialized value is written and read under the persistence
/// profile, whatever profile the data source carries.
/// </summary>
/// <remarks>
/// <para>
/// The failure this guards against was total. An opaque document was written by the upsert under
/// the persistence profile, where a date is a number, and read by Entity Framework through the
/// data source's JSON options, which are the default profile, where the only date reader takes a
/// rendering. Every read of every such row failed, forever, and the feature behind it stopped.
/// </para>
/// <para>
/// The data source cannot simply move to the persistence profile: the same options serve the
/// outbox, inbox and event store metadata columns, whose form is the wire's. So the document
/// column is bound to the persistence profile explicitly, through a value converter that
/// serializes with the profile's own options, and the data source keeps the default profile for
/// everything else. This test configures the data source exactly as the generated registration
/// does, with the default profile, and proves the document round-trips regardless.
/// </para>
/// </remarks>
/// <code-under-test>src/Whizbang.Data.EFCore.Postgres/Perspectives/PerspectiveDocumentSerialization.cs</code-under-test>
[Category("Integration")]
[Category("Shard4")]
[NotInParallel("PathOneProvider")]
public class OpaqueDocumentRoundTripTests : IAsyncDisposable {
  private const string TABLE = "wh_per_opaque_document";

  private static readonly DateTime _startedAt = new(2026, 3, 4, 5, 6, 7, 890, DateTimeKind.Utc);

  private string _databaseName = null!;
  private string _connectionString = null!;
  private NpgsqlDataSource _dataSource = null!;

  /// <summary>The opaque shape the generator emits, bound to the persistence profile.</summary>
  private sealed class OpaqueContext(DbContextOptions<OpaqueContext> options) : DbContext(options) {
    protected override void OnModelCreating(ModelBuilder modelBuilder) =>
      modelBuilder.Entity<PerspectiveRow<OpaqueDocument>>(entity => {
        entity.ToTable(TABLE);
        entity.HasKey(e => e.Id);
        entity.Property(e => e.Id).HasColumnName("id");
        entity.Property(e => e.Data).HasColumnName("data").HasColumnType("jsonb")
          .HasConversion(PerspectiveDocumentSerialization.ConverterFor<OpaqueDocument>());
        entity.Property(e => e.Metadata).HasColumnName("metadata").HasColumnType("jsonb")
          .HasConversion(PerspectiveDocumentSerialization.ConverterFor<PerspectiveMetadata>());
        entity.Property(e => e.Scope).HasColumnName("scope").HasColumnType("jsonb")
          .HasConversion(PerspectiveDocumentSerialization.ConverterFor<PerspectiveScope>());
        entity.Property(e => e.CreatedAt).HasColumnName("created_at");
        entity.Property(e => e.UpdatedAt).HasColumnName("updated_at");
        entity.Property(e => e.Version).HasColumnName("version");
      });
  }

  [Before(Test)]
  public async Task SetupAsync() {
    OpaqueDocumentFixture.EnsureRegistered();
    await SharedPostgresContainer.InitializeAsync();

    _databaseName = $"opaque_{Guid.NewGuid():N}";
    await using (var admin = new NpgsqlConnection(SharedPostgresContainer.ConnectionString)) {
      await admin.OpenAsync();
      await using var create = new NpgsqlCommand($"CREATE DATABASE {_databaseName}", admin);
      await create.ExecuteNonQueryAsync();
    }

    _connectionString = new NpgsqlConnectionStringBuilder(SharedPostgresContainer.ConnectionString) {
      Database = _databaseName,
      Timezone = "UTC",
    }.ConnectionString;

    // Exactly what the generated registration builds: the DEFAULT profile on the data source,
    // because the outbox, inbox and event store metadata columns read through it.
    var dataSourceBuilder = new NpgsqlDataSourceBuilder(_connectionString);
    dataSourceBuilder.ConfigureJsonOptions(JsonContextRegistry.CreateCombinedOptions());
    dataSourceBuilder.EnableDynamicJson();
    _dataSource = dataSourceBuilder.Build();

    await using var connection = new NpgsqlConnection(_connectionString);
    await connection.OpenAsync();
    await using var table = new NpgsqlCommand(
      $"CREATE TABLE {TABLE} (id uuid PRIMARY KEY, data jsonb NOT NULL, metadata jsonb NOT NULL, "
      + "scope jsonb NOT NULL, created_at timestamptz NOT NULL, updated_at timestamptz NOT NULL, "
      + "sys_created_at timestamptz NOT NULL, sys_updated_at timestamptz NOT NULL, version int NOT NULL)",
      connection);
    await table.ExecuteNonQueryAsync();

    BaseUpsertStrategy.PathOnePersistenceOptionsProvider = () =>
      Generated.PerspectivePersistenceJsonContext.CreateOptions(
        Generated.MessageJsonContext.Default,
        global::Whizbang.Core.Generated.InfrastructureJsonContext.Default);
  }

  [After(Test)]
  public async ValueTask DisposeAsync() {
    BaseUpsertStrategy.PathOnePersistenceOptionsProvider = null;
    if (_dataSource is not null) {
      await _dataSource.DisposeAsync();
    }
    if (_databaseName is not null) {
      try {
        await using var admin = new NpgsqlConnection(SharedPostgresContainer.ConnectionString);
        await admin.OpenAsync();
        await using var drop = new NpgsqlCommand($"DROP DATABASE IF EXISTS {_databaseName} WITH (FORCE)", admin);
        await drop.ExecuteNonQueryAsync();
      } catch (NpgsqlException) {
        // The container is torn down with the run; a database left behind costs nothing.
      }
    }
    GC.SuppressFinalize(this);
  }

  private OpaqueContext _context() =>
    new(new DbContextOptionsBuilder<OpaqueContext>()
      .UseNpgsql(_dataSource, npgsql => npgsql.UseWhizbangFunctions())
      .Options);

  private static OpaqueDocument _document(Guid id) => new() {
    Id = id,
    StartedAt = _startedAt,
    EndedAt = new DateTimeOffset(_startedAt).AddHours(1),
    Turns = [new OpaqueTurn(Guid.CreateVersion7(), "hello", _startedAt.AddMinutes(1), [new OpaqueAttachment(Guid.CreateVersion7(), "a.txt")])],
  };

  private async Task<string> _storedDataAsync(Guid id) {
    await using var connection = new NpgsqlConnection(_connectionString);
    await connection.OpenAsync();
    await using var command = new NpgsqlCommand($"SELECT data::text FROM {TABLE} WHERE id = $1", connection);
    command.Parameters.AddWithValue(id);
    return (string)(await command.ExecuteScalarAsync())!;
  }

  /// <summary>
  /// A row the upsert wrote reads back through Entity Framework, with the data source on the
  /// default profile. This is the failure that stopped a feature, as a test.
  /// </summary>
  [Test]
  public async Task ARowTheUpsertWroteReadsBackThroughEntityFrameworkAsync() {
    var id = Guid.CreateVersion7();
    await using (var writer = _context()) {
      await new PostgresUpsertStrategy().UpsertPerspectiveRowAsync(
        writer, TABLE, id, _document(id),
        new PerspectiveMetadata { EventType = "e", EventId = "1", Timestamp = _startedAt },
        new PerspectiveScope());
    }

    var stored = JsonDocument.Parse(await _storedDataAsync(id)).RootElement;
    await Assert.That(stored.GetProperty("StartedAt").ValueKind).IsEqualTo(JsonValueKind.Number)
      .Because("the upsert writes under the persistence profile, where a date is a number");
    await Assert.That(stored.GetProperty("Turns")[0].GetProperty("At").ValueKind).IsEqualTo(JsonValueKind.Number)
      .Because("a temporal inside a collection element is converted like any other");

    await using var reader = _context();
    var row = await reader.Set<PerspectiveRow<OpaqueDocument>>().AsNoTracking().SingleAsync(r => r.Id == id);
    await Assert.That(row.Data.StartedAt).IsEqualTo(_startedAt);
    await Assert.That(row.Data.EndedAt).IsEqualTo(new DateTimeOffset(_startedAt).AddHours(1));
    await Assert.That(row.Data.Turns[0].At).IsEqualTo(_startedAt.AddMinutes(1));
    await Assert.That(row.Data.Turns[0].Attachments![0].FileName).IsEqualTo("a.txt");
    await Assert.That(row.Metadata.Timestamp).IsEqualTo(_startedAt);
  }

  /// <summary>
  /// Ordering and filtering on a temporal inside the document answer in the instant's own order.
  /// </summary>
  /// <remarks>
  /// <para>
  /// The document is stored as one serialized value, so the mapped path holds no metadata for the
  /// members inside it: a member read in a query is translated from its CLR type alone, and a date
  /// becomes a cast of the extracted text to a timestamp. What is stored is the canonical number, so
  /// the cast is handed a count of microseconds and PostgreSQL refuses the whole statement.
  /// </para>
  /// <para>
  /// Ordering and filtering are where a consumer meets this, and neither needs the instant
  /// reconstructed: the canonical unit counts forward, so the number's order is the instant's order
  /// and a bound converted the same way compares the same way.
  /// </para>
  /// </remarks>
  [Test]
  public async Task OrderingAndFilteringOnATemporalInsideTheDocumentAnswerAsync() {
    var earlier = Guid.CreateVersion7();
    var later = Guid.CreateVersion7();

    await using (var writer = _context()) {
      var strategy = new PostgresUpsertStrategy();
      foreach (var (id, at) in new[] { (earlier, _startedAt), (later, _startedAt.AddDays(1)) }) {
        await strategy.UpsertPerspectiveRowAsync(
          writer, TABLE, id,
          new OpaqueDocument { Id = id, StartedAt = at, Turns = [] },
          new PerspectiveMetadata { EventType = "e", EventId = "1", Timestamp = at },
          new PerspectiveScope());
      }
    }

    await using var reader = _context();

    var newestFirst = await reader.Set<PerspectiveRow<OpaqueDocument>>().AsNoTracking()
      .OrderByDescending(r => r.Data.StartedAt)
      .Select(r => r.Id)
      .ToListAsync();

    await Assert.That(newestFirst).IsEquivalentTo([later, earlier], CollectionOrdering.Matching)
      .Because("the canonical unit counts forward, so its order is the instant's order");

    var after = await reader.Set<PerspectiveRow<OpaqueDocument>>().AsNoTracking()
      .Where(r => r.Data.StartedAt > _startedAt)
      .Select(r => r.Id)
      .ToListAsync();

    await Assert.That(after).IsEquivalentTo([later])
      .Because("a bound converted the same way compares the same way");

    // The bound above is written into the query, so it is converted here, by the same code that
    // wrote the row. A bound that arrives as a parameter cannot be, and is converted by the database
    // instead, which is the only place the two renderings of an instant can disagree.
    var bound = _startedAt;
    var afterParameter = await reader.Set<PerspectiveRow<OpaqueDocument>>().AsNoTracking()
      .Where(r => r.Data.StartedAt > bound)
      .Select(r => r.Id)
      .ToListAsync();

    await Assert.That(afterParameter).IsEquivalentTo(after)
      .Because("where the bound came from cannot change which rows answer");
  }

  /// <summary>
  /// The database converts an instant to the canonical unit exactly as the serializer does.
  /// </summary>
  /// <remarks>
  /// A filter whose bound arrives as a parameter is converted in the database, and a filter whose
  /// bound is written into the query is converted by the same code that wrote the row. A
  /// disagreement of one microsecond between those two would be an off-by-one on every boundary
  /// comparison and would show up as a row missing from a range, which is the kind of wrong answer
  /// that never looks like a bug in the conversion. The instants below are the ones where a
  /// rendering can drift: a whole second, a fraction that is not representable in binary, the
  /// smallest unit the format keeps, and the epoch itself.
  /// </remarks>
  [Test]
  public async Task TheDatabaseConvertsAnInstantAsTheSerializerDoesAsync(CancellationToken cancellationToken) {
    DateTime[] instants = [
      new(2026, 3, 4, 5, 6, 7, 0, DateTimeKind.Utc),
      new(2026, 3, 4, 5, 6, 7, 123, DateTimeKind.Utc),
      new DateTime(2026, 3, 4, 5, 6, 7, 0, DateTimeKind.Utc).AddTicks(1230),
      DateTime.UnixEpoch,
      new DateTime(1969, 12, 31, 23, 59, 59, 999, DateTimeKind.Utc),
    ];

    await using var db = new NpgsqlConnection(_connectionString);
    await db.OpenAsync(cancellationToken);

    foreach (var instant in instants) {
      await using var command = new NpgsqlCommand(
        "SELECT (date_part('epoch', $1::timestamptz) * 1000000)::bigint", db);
      command.Parameters.AddWithValue(instant);

      var inDatabase = (long)(await command.ExecuteScalarAsync(cancellationToken))!;

      await Assert.That(inDatabase).IsEqualTo(CanonicalTemporalFormat.ToEpochMicroseconds(instant))
        .Because($"the two conversions of {instant:O} have to be the same number");
    }
  }

  /// <summary>
  /// The default profile could not have read that row, which is why the column is bound explicitly.
  /// </summary>
  /// <remarks>
  /// Not a test of the default profile. A test of the seam: if the default profile ever learns to
  /// read a canonical number, the explicit binding stops being load-bearing and this says so.
  /// </remarks>
  [Test]
  public async Task TheDefaultProfileCouldNotHaveReadItAsync() {
    var id = Guid.CreateVersion7();
    await using (var writer = _context()) {
      await new PostgresUpsertStrategy().UpsertPerspectiveRowAsync(
        writer, TABLE, id, _document(id),
        new PerspectiveMetadata { EventType = "e", EventId = "1", Timestamp = _startedAt },
        new PerspectiveScope());
    }
    var stored = await _storedDataAsync(id);

    var defaultOptions = JsonContextRegistry.CreateCombinedOptions();
    var info = defaultOptions.GetTypeInfo(typeof(OpaqueDocument));

    await Assert.That(() => JsonSerializer.Deserialize(stored, info)).Throws<JsonException>()
      .Because("the default profile is the wire's, where a date is a rendering; the data source "
        + "carries it for the outbox, inbox and event store, and a document must not depend on it");
  }

  /// <summary>
  /// A row Entity Framework wrote itself, the fallback when the atomic path is unavailable, takes
  /// the same form and reads back the same way.
  /// </summary>
  [Test]
  public async Task ARowEntityFrameworkWroteTakesTheSameFormAsync() {
    var id = Guid.CreateVersion7();
    await using (var writer = _context()) {
      writer.Add(new PerspectiveRow<OpaqueDocument> {
        Id = id,
        Data = _document(id),
        Metadata = new PerspectiveMetadata { EventType = "e", EventId = "1", Timestamp = _startedAt },
        Scope = new PerspectiveScope(),
        CreatedAt = DateTime.UtcNow,
        UpdatedAt = DateTime.UtcNow,
        Version = 1,
      });
      await writer.Database.ExecuteSqlRawAsync(
        $"ALTER TABLE {TABLE} ALTER COLUMN sys_created_at SET DEFAULT now(), ALTER COLUMN sys_updated_at SET DEFAULT now()");
      await writer.SaveChangesAsync();
    }

    var stored = JsonDocument.Parse(await _storedDataAsync(id)).RootElement;
    await Assert.That(stored.GetProperty("StartedAt").GetInt64())
      .IsEqualTo(CanonicalTemporalFormat.ToEpochMicroseconds(_startedAt))
      .Because("both writers use the same options, so a row is the same row whichever wrote it");

    await using var reader = _context();
    var row = await reader.Set<PerspectiveRow<OpaqueDocument>>().AsNoTracking().SingleAsync(r => r.Id == id);
    await Assert.That(row.Data.Turns[0].At).IsEqualTo(_startedAt.AddMinutes(1));
  }

  /// <summary>
  /// A row an older release wrote, holding renderings, still reads, and the read is counted.
  /// </summary>
  [Test]
  public async Task ARowHoldingRenderingsStillReadsAsync() {
    var id = Guid.CreateVersion7();
    await using (var connection = new NpgsqlConnection(_connectionString)) {
      await connection.OpenAsync();
      await using var insert = new NpgsqlCommand(
        $"INSERT INTO {TABLE} (id, data, metadata, scope, created_at, updated_at, sys_created_at, sys_updated_at, version) "
        + "VALUES ($1, $2::jsonb, $3::jsonb, '{}'::jsonb, now(), now(), now(), now(), 1)", connection);
      insert.Parameters.AddWithValue(id);
      insert.Parameters.AddWithValue(
        $$"""{"Id":"{{id}}","StartedAt":"2026-03-04T05:06:07.89Z","EndedAt":null,"Turns":[]}""");
      insert.Parameters.AddWithValue(
        """{"EventType":"e","EventId":"1","Timestamp":"2026-03-04T05:06:07.89Z"}""");
      await insert.ExecuteNonQueryAsync();
    }

    await using var reader = _context();
    var row = await reader.Set<PerspectiveRow<OpaqueDocument>>().AsNoTracking().SingleAsync(r => r.Id == id);

    await Assert.That(row.Data.StartedAt).IsEqualTo(_startedAt)
      .Because("a rendering the rewrite has not reached is still a document, read and counted");
    await Assert.That(row.Metadata.Timestamp).IsEqualTo(_startedAt);
  }
}
