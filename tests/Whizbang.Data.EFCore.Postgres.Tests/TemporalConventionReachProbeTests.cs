using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Microsoft.EntityFrameworkCore.Metadata.Conventions;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;
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
/// Whether a model-finalizing convention can reach every temporal property Entity Framework maps
/// inside a JSON document, and convert it, without anything having been discovered by a generator.
/// </summary>
/// <remarks>
/// <para>
/// The canonical temporal form has two writers and Entity Framework is only one of them. If the
/// serializer converts every temporal in a document and Entity Framework converts only the ones a
/// generator found, every property the generator missed is written as a number and read as a
/// rendering, and the row is unreadable. The way out is to let Entity Framework's own walk decide
/// what Entity Framework converts, so the two sides agree by construction rather than by two
/// discoveries staying in step.
/// </para>
/// <para>
/// Measured against a real model build and a real round trip rather than reasoned about, because
/// what a convention can reach inside a complex collection's element type is Entity Framework's
/// business and it has changed between releases. If a future release stops reaching one of these
/// placements, this fails and the design gets revisited before a consumer finds out.
/// </para>
/// </remarks>
/// <docs>fundamentals/perspectives/jsonb-containment</docs>
[Category("Integration")]
[Category("Shard4")]
public class TemporalConventionReachProbeTests {
  private const string TABLE = "wh_per_temporal_reach_probe";

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

  /// <summary>
  /// The convention under measurement: every temporal property of every JSON-mapped complex type,
  /// found by walking the model Entity Framework built.
  /// </summary>
  private sealed class CanonicalTemporalProbeConvention : IModelFinalizingConvention {
    private static readonly ValueConverter<DateTime, long> _instant = new(
      v => CanonicalTemporalFormat.ToEpochMicroseconds(v),
      v => CanonicalTemporalFormat.FromEpochMicroseconds(v));
    private static readonly ValueConverter<DateTimeOffset, long> _offset = new(
      v => CanonicalTemporalFormat.ToEpochMicroseconds(v),
      v => CanonicalTemporalFormat.OffsetFromEpochMicroseconds(v));
    private static readonly ValueConverter<DateOnly, int> _day = new(
      v => CanonicalTemporalFormat.ToEpochDays(v),
      v => CanonicalTemporalFormat.FromEpochDays(v));
    private static readonly ValueConverter<TimeOnly, long> _timeOfDay = new(
      v => CanonicalTemporalFormat.ToMicrosecondsOfDay(v),
      v => CanonicalTemporalFormat.FromMicrosecondsOfDay(v));
    private static readonly ValueConverter<TimeSpan, long> _duration = new(
      v => CanonicalTemporalFormat.ToTicks(v),
      v => CanonicalTemporalFormat.FromTicks(v));

    public void ProcessModelFinalizing(
        IConventionModelBuilder modelBuilder, IConventionContext<IConventionModelBuilder> context) {
      foreach (var entityType in modelBuilder.Metadata.GetEntityTypes()) {
        foreach (var complex in entityType.GetComplexProperties()) {
          _walk(complex);
        }
      }
    }

    private static void _walk(IConventionComplexProperty complex) {
      foreach (var property in complex.ComplexType.GetProperties()) {
        var converter = _converterFor(Nullable.GetUnderlyingType(property.ClrType) ?? property.ClrType);
        if (converter is not null) {
          property.Builder.HasConversion(converter, fromDataAnnotation: false);
        }
      }
      foreach (var nested in complex.ComplexType.GetComplexProperties()) {
        _walk(nested);
      }
    }

    private static ValueConverter? _converterFor(Type clrType) =>
      clrType == typeof(DateTime) ? _instant
      : clrType == typeof(DateTimeOffset) ? _offset
      : clrType == typeof(DateOnly) ? _day
      : clrType == typeof(TimeOnly) ? _timeOfDay
      : clrType == typeof(TimeSpan) ? _duration
      : null;
  }

  /// <summary>The mapped shape the generator emits, with the convention and nothing else.</summary>
  private sealed class ProbeContext(DbContextOptions<ProbeContext> options) : DbContext(options) {
    protected override void ConfigureConventions(ModelConfigurationBuilder configurationBuilder) =>
      configurationBuilder.Conventions.Add(_ => new CanonicalTemporalProbeConvention());

    protected override void OnModelCreating(ModelBuilder modelBuilder) =>
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
  }

  private static ProbeContext _context(string connectionString) =>
    new(new DbContextOptionsBuilder<ProbeContext>().UseNpgsql(connectionString).Options);

  /// <summary>Every property of a JSON-mapped complex type, with its converter, keyed by path.</summary>
  private static Dictionary<string, ValueConverter?> _mappedTemporals(IModel model) {
    var found = new Dictionary<string, ValueConverter?>();
    foreach (var entityType in model.GetEntityTypes()) {
      foreach (var complex in entityType.GetComplexProperties()) {
        _collect(complex, complex.Name, found);
      }
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
  /// The convention reaches every placement, including the ones a member walk of the model misses.
  /// </summary>
  /// <remarks>No database: building the model needs a provider, not a connection.</remarks>
  [Test]
  public async Task TheConventionReachesEveryPlacementEntityFrameworkMapsAsync() {
    using var context = _context("Host=localhost;Database=probe;Username=u;Password=p");
    var temporals = _mappedTemporals(context.Model);

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
    }
    await Assert.That(temporals.Count).IsEqualTo(expected.Length)
      .Because("the walk is exhaustive: nothing else in this model is temporal");
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
    await Assert.That(data.GetProperty("OccurredAt").ValueKind).IsEqualTo(JsonValueKind.Number);
    await Assert.That(data.GetProperty("RecordedAt").ValueKind).IsEqualTo(JsonValueKind.Number)
      .Because("an inherited temporal is one Entity Framework maps, so it must be converted too");
    await Assert.That(data.GetProperty("Window").GetProperty("Opens").ValueKind).IsEqualTo(JsonValueKind.Number);
    await Assert.That(data.GetProperty("Window").GetProperty("Length").ValueKind).IsEqualTo(JsonValueKind.Number);
    var occurrence = data.GetProperty("Occurrences")[0];
    await Assert.That(occurrence.GetProperty("Day").ValueKind).IsEqualTo(JsonValueKind.Number);
    await Assert.That(occurrence.GetProperty("MaybeAt").ValueKind).IsEqualTo(JsonValueKind.Number);
    await Assert.That(metadata.GetProperty("Timestamp").ValueKind).IsEqualTo(JsonValueKind.Number)
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
