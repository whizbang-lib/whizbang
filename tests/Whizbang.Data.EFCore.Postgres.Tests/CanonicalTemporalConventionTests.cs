using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;
using Npgsql;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core;
using Whizbang.Core.Lenses;
using Whizbang.Core.Perspectives;
using Whizbang.Data.EFCore.Postgres.Functions;
using Whizbang.Data.EFCore.Postgres.Perspectives;
using Whizbang.Testing.Containers;

namespace Whizbang.Data.EFCore.Postgres.Tests;

/// <summary>
/// That Entity Framework converts every temporal it maps inside a JSON document, with nothing
/// configured per model and nothing discovered by a generator.
/// </summary>
/// <remarks>
/// <para>
/// The canonical temporal form has two writers and Entity Framework is only one of them. The
/// serializer converts every temporal in a document it writes under the persistence profile. If
/// Entity Framework converted only the properties a generator had found, every property the
/// generator missed would be written as a number and read as a rendering, and the row would be
/// unreadable. So Entity Framework's own walk of the model decides what Entity Framework converts,
/// and the two sides agree by construction rather than by two discoveries staying in step.
/// </para>
/// <para>
/// The convention rides the same options extension every generated context already carries, so a
/// consumer opts into it by registering a perspective context and in no other way.
/// </para>
/// </remarks>
/// <code-under-test>src/Whizbang.Data.EFCore.Postgres/Perspectives/CanonicalTemporalConvention.cs</code-under-test>
[Category("Integration")]
[Category("Shard4")]
public class CanonicalTemporalConventionTests {
  private const string TABLE = "wh_per_temporal_convention";

  /// <summary>A temporal declared on a base class, which a member walk of the model misses.</summary>
  public abstract class Audited {
    public DateTimeOffset RecordedAt { get; set; }
  }

  /// <summary>A nested object holding two temporal kinds.</summary>
  public sealed class Window {
    public TimeOnly Opens { get; set; }
    public TimeSpan Length { get; set; }
  }

  /// <summary>A complex-collection element holding two more, one of them optional.</summary>
  public sealed class Occurrence {
    public Guid OccurrenceId { get; set; }
    public DateOnly Day { get; set; }
    public DateTime? MaybeAt { get; set; }
  }

  /// <summary>One of every placement: top level, inherited, nested, in a collection element.</summary>
  public sealed class ReachModel : Audited {
    [StreamId]
    public Guid Id { get; set; }
    public DateTime OccurredAt { get; set; }
    public Window Window { get; set; } = new();
    public List<Occurrence> Occurrences { get; set; } = [];
  }

  /// <summary>A row type with a complex property mapped to columns rather than to a document.</summary>
  public sealed class ColumnRow {
    public Guid Id { get; set; }
    public Window Window { get; set; } = new();
    public DateTime StampedAt { get; set; }
  }

  /// <summary>The mapped shape the generator emits, with no conversion written out.</summary>
  private sealed class ConventionContext(DbContextOptions<ConventionContext> options) : DbContext(options) {
    protected override void OnModelCreating(ModelBuilder modelBuilder) {
      modelBuilder.Entity<PerspectiveRow<ReachModel>>(entity => {
        entity.ToTable(TABLE);
        entity.HasKey(e => e.Id);
        entity.Property(e => e.Id).HasColumnName("id");
        entity.ComplexProperty(e => e.Data, d => d.ToJson("data"));
        entity.ComplexProperty(e => e.Metadata, m => m.ToJson("metadata"));
        entity.ComplexProperty(e => e.Scope, s => {
          s.ToJson("scope");
          s.ComplexCollection(p => p.Extensions, ex => ex.HasJsonPropertyName("ex"));
        });
        entity.Property(e => e.CreatedAt).HasColumnName("created_at");
        entity.Property(e => e.UpdatedAt).HasColumnName("updated_at");
        entity.Property(e => e.Version).HasColumnName("version");
      });
      modelBuilder.Entity<ColumnRow>(entity => {
        entity.ToTable("wh_column_row");
        entity.HasKey(e => e.Id);
        entity.ComplexProperty(e => e.Window);
      });
    }
  }

  /// <summary>The context as a generated registration configures it: the functions extension and nothing else.</summary>
  private static ConventionContext _context(string connectionString) =>
    new(new DbContextOptionsBuilder<ConventionContext>()
      .UseNpgsql(connectionString, npgsql => npgsql.UseWhizbangFunctions())
      .Options);

  /// <summary>Every temporal property of a complex type, with its converter, keyed by path.</summary>
  private static Dictionary<string, ValueConverter?> _temporals(IModel model, Type rowType) {
    var found = new Dictionary<string, ValueConverter?>();
    foreach (var complex in model.FindEntityType(rowType)!.GetComplexProperties()) {
      _collect(complex, complex.Name, found);
    }
    return found;
  }

  private static void _collect(IComplexProperty complex, string path, Dictionary<string, ValueConverter?> found) {
    foreach (var property in complex.ComplexType.GetProperties()) {
      var clr = Nullable.GetUnderlyingType(property.ClrType) ?? property.ClrType;
      if (clr == typeof(DateTime) || clr == typeof(DateTimeOffset) || clr == typeof(DateOnly)
          || clr == typeof(TimeOnly) || clr == typeof(TimeSpan)) {
        found[$"{path}.{property.Name}"] = property.GetValueConverter();
      }
    }
    foreach (var nested in complex.ComplexType.GetComplexProperties()) {
      _collect(nested, $"{path}.{nested.Name}", found);
    }
  }

  /// <summary>
  /// Every placement Entity Framework maps inside a document carries the conversion.
  /// </summary>
  /// <remarks>No database: building the model needs a provider, not a connection.</remarks>
  [Test]
  public async Task EveryTemporalInADocumentIsConvertedAsync() {
    using var context = _context("Host=localhost;Database=probe;Username=u;Password=p");
    var temporals = _temporals(context.Model, typeof(PerspectiveRow<ReachModel>));

    string[] expected = [
      "Data.OccurredAt",
      "Data.RecordedAt",
      "Data.Window.Opens",
      "Data.Window.Length",
      "Data.Occurrences.Day",
      "Data.Occurrences.MaybeAt",
      "Metadata.Timestamp",
    ];
    foreach (var path in expected) {
      await Assert.That(temporals.ContainsKey(path)).IsTrue()
        .Because($"{path} is a temporal Entity Framework maps and the walk must see it");
      await Assert.That(temporals[path]).IsNotNull()
        .Because($"{path} must carry the canonical conversion, or the writer's number is unreadable there");
      await Assert.That(temporals[path]!.ProviderClrType).IsEqualTo(typeof(long))
        .Because("every kind stores in the same eight-byte unit");
    }
    await Assert.That(temporals.Count).IsEqualTo(expected.Length)
      .Because("the walk is exhaustive: nothing else in this model is temporal");
  }

  /// <summary>
  /// Every placement Entity Framework maps inside a document reads through the canonical reader
  /// for its kind, so a mapped document is read the way an opaque one is.
  /// </summary>
  /// <remarks>
  /// The conversion alone left Entity Framework reading the number with its own reader, which
  /// refused a rendering and refused an unexpected token with a generic error nothing could
  /// classify. The reader/writer is what puts the serializer's reader on this path too.
  /// </remarks>
  [Test]
  public async Task EveryTemporalInADocumentReadsThroughTheCanonicalReaderAsync() {
    using var context = _context("Host=localhost;Database=probe;Username=u;Password=p");
    var readers = new Dictionary<string, Type?>();
    foreach (var complex in context.Model.FindEntityType(typeof(PerspectiveRow<ReachModel>))!.GetComplexProperties()) {
      _collectReaders(complex, complex.Name, readers);
    }

    var expected = new Dictionary<string, Type> {
      ["Data.OccurredAt"] = typeof(CanonicalTemporalJsonReaderWriters.Instant),
      ["Data.RecordedAt"] = typeof(CanonicalTemporalJsonReaderWriters.OffsetInstant),
      ["Data.Window.Opens"] = typeof(CanonicalTemporalJsonReaderWriters.TimeOfDay),
      ["Data.Window.Length"] = typeof(CanonicalTemporalJsonReaderWriters.Duration),
      ["Data.Occurrences.Day"] = typeof(CanonicalTemporalJsonReaderWriters.Day),
      ["Data.Occurrences.MaybeAt"] = typeof(CanonicalTemporalJsonReaderWriters.Instant),
      ["Metadata.Timestamp"] = typeof(CanonicalTemporalJsonReaderWriters.Instant),
    };
    foreach (var (path, reader) in expected) {
      await Assert.That(readers.GetValueOrDefault(path)).IsEqualTo(reader)
        .Because($"{path} must read through the canonical reader for its kind, or a rendering there "
          + "reads on one path and not the other");
    }
    await Assert.That(readers.Count).IsEqualTo(expected.Count);
  }

  private static void _collectReaders(IComplexProperty complex, string path, Dictionary<string, Type?> found) {
    foreach (var property in complex.ComplexType.GetProperties()) {
      if (CanonicalTemporalConvention.KindOf(property.ClrType) is not null) {
        found[$"{path}.{property.Name}"] = property.GetJsonValueReaderWriter()?.GetType();
      }
    }
    foreach (var nested in complex.ComplexType.GetComplexProperties()) {
      _collectReaders(nested, $"{path}.{nested.Name}", found);
    }
  }

  /// <summary>
  /// A column outside any document is left alone, and so is a complex type mapped to columns.
  /// </summary>
  /// <remarks>
  /// The stored form is a property of the document, not of the type. A timestamp column is a
  /// timestamp column, and a complex type spread over columns has typed columns of its own.
  /// </remarks>
  [Test]
  public async Task ATemporalOutsideADocumentIsLeftAloneAsync() {
    using var context = _context("Host=localhost;Database=probe;Username=u;Password=p");

    var row = context.Model.FindEntityType(typeof(PerspectiveRow<ReachModel>))!;
    await Assert.That(row.FindProperty(nameof(PerspectiveRow<ReachModel>.CreatedAt))!.GetValueConverter()).IsNull()
      .Because("a timestamp column is typed for what it holds and needs no conversion");

    var columns = _temporals(context.Model, typeof(ColumnRow));
    await Assert.That(columns["Window.Opens"]).IsNull()
      .Because("a complex type mapped to columns has a typed column per property; the document form "
        + "does not apply to it");
    await Assert.That(context.Model.FindEntityType(typeof(ColumnRow))!
        .FindProperty(nameof(ColumnRow.StampedAt))!.GetValueConverter()).IsNull();
  }

  /// <summary>
  /// A row written by Entity Framework stores a number at every placement and reads back equal.
  /// </summary>
  [Test]
  [Timeout(120_000)]
  public async Task ARowRoundTripsAsNumbersAtEveryPlacementAsync(CancellationToken cancellationToken) {
    await SharedPostgresContainer.InitializeAsync(cancellationToken);
    var connectionString = SharedPostgresContainer.ConnectionString;
    await _recreateTableAsync(connectionString, cancellationToken);

    var id = Guid.CreateVersion7();
    var occurredAt = new DateTime(2026, 3, 4, 5, 6, 7, 890, DateTimeKind.Utc);
    var row = new PerspectiveRow<ReachModel> {
      Id = id,
      Data = new ReachModel {
        Id = id,
        OccurredAt = occurredAt,
        RecordedAt = new DateTimeOffset(occurredAt).AddHours(1),
        Window = new Window { Opens = new TimeOnly(9, 30), Length = TimeSpan.FromMinutes(90) },
        Occurrences = [new Occurrence {
          OccurrenceId = Guid.CreateVersion7(),
          Day = new DateOnly(2026, 3, 5),
          MaybeAt = occurredAt.AddDays(1),
        }],
      },
      Metadata = new PerspectiveMetadata { EventType = "probe", EventId = "1", Timestamp = occurredAt },
      Scope = new PerspectiveScope(),
      CreatedAt = DateTime.UtcNow,
      UpdatedAt = DateTime.UtcNow,
      Version = 1,
    };

    await using (var writer = _context(connectionString)) {
      writer.Add(row);
      await writer.SaveChangesAsync(cancellationToken);
    }

    var (data, metadata) = await _storedAsync(connectionString, id, cancellationToken);
    await Assert.That(data.GetProperty("OccurredAt").GetInt64())
      .IsEqualTo(CanonicalTemporalFormat.ToEpochMicroseconds(occurredAt));
    await Assert.That(data.GetProperty("RecordedAt").ValueKind).IsEqualTo(JsonValueKind.Number)
      .Because("an inherited temporal is one Entity Framework maps, so it must be converted too");
    await Assert.That(data.GetProperty("Window").GetProperty("Opens").ValueKind).IsEqualTo(JsonValueKind.Number);
    await Assert.That(data.GetProperty("Window").GetProperty("Length").GetInt64())
      .IsEqualTo(CanonicalTemporalFormat.ToMicroseconds(TimeSpan.FromMinutes(90)));
    var occurrence = data.GetProperty("Occurrences")[0];
    await Assert.That(occurrence.GetProperty("Day").GetInt64())
      .IsEqualTo(CanonicalTemporalFormat.ToEpochMicroseconds(new DateOnly(2026, 3, 5)));
    await Assert.That(occurrence.GetProperty("MaybeAt").ValueKind).IsEqualTo(JsonValueKind.Number);
    await Assert.That(metadata.GetProperty("Timestamp").GetInt64())
      .IsEqualTo(CanonicalTemporalFormat.ToEpochMicroseconds(occurredAt))
      .Because("the framework's own document is a document like any other");

    await using var reader = _context(connectionString);
    var back = await reader.Set<PerspectiveRow<ReachModel>>().SingleAsync(r => r.Id == id, cancellationToken);
    await Assert.That(back.Data.OccurredAt).IsEqualTo(occurredAt);
    await Assert.That(back.Data.RecordedAt).IsEqualTo(row.Data.RecordedAt);
    await Assert.That(back.Data.Window.Opens).IsEqualTo(new TimeOnly(9, 30));
    await Assert.That(back.Data.Window.Length).IsEqualTo(TimeSpan.FromMinutes(90));
    await Assert.That(back.Data.Occurrences[0].Day).IsEqualTo(new DateOnly(2026, 3, 5));
    await Assert.That(back.Data.Occurrences[0].MaybeAt).IsEqualTo(occurredAt.AddDays(1));
    await Assert.That(back.Metadata.Timestamp).IsEqualTo(occurredAt);
  }

  private static async Task _recreateTableAsync(string connectionString, CancellationToken cancellationToken) {
    await using var connection = new NpgsqlConnection(connectionString);
    await connection.OpenAsync(cancellationToken);
    await using var command = new NpgsqlCommand(
      $"DROP TABLE IF EXISTS {TABLE}; CREATE TABLE {TABLE} ("
      + "id uuid PRIMARY KEY, data jsonb NOT NULL, metadata jsonb NOT NULL, scope jsonb NOT NULL, "
      + "created_at timestamptz NOT NULL, updated_at timestamptz NOT NULL, version int NOT NULL)",
      connection);
    await command.ExecuteNonQueryAsync(cancellationToken);
  }

  private static async Task<(JsonElement Data, JsonElement Metadata)> _storedAsync(
      string connectionString, Guid id, CancellationToken cancellationToken) {
    await using var connection = new NpgsqlConnection(connectionString);
    await connection.OpenAsync(cancellationToken);
    await using var command = new NpgsqlCommand(
      $"SELECT data::text, metadata::text FROM {TABLE} WHERE id = $1", connection);
    command.Parameters.AddWithValue(id);
    await using var reader = await command.ExecuteReaderAsync(cancellationToken);
    await Assert.That(await reader.ReadAsync(cancellationToken)).IsTrue();
    return (
      JsonDocument.Parse(reader.GetString(0)).RootElement,
      JsonDocument.Parse(reader.GetString(1)).RootElement);
  }
}
