using TUnit.Assertions.Extensions;

namespace Whizbang.Generators.Tests;

/// <summary>
/// That the generated mapping names no temporal property, because the conversion is not the
/// mapping's to decide.
/// </summary>
/// <remarks>
/// <para>
/// The stored form of a date, a time or a duration is a decision the framework makes once. It used
/// to be emitted here as one <c>HasConversion</c> line per property a generator had discovered, and
/// that discovery was partial: a member inherited from a base class, a nested object, an element of
/// a collection and the framework's own metadata were all invisible to it. Each was written as a
/// rendering and read as one, so nothing failed, and each was a place where widening the writer
/// alone would have produced rows the reader could not parse.
/// </para>
/// <para>
/// Now a convention every perspective context carries walks the model Entity Framework built and
/// converts whatever it maps. The mapping has nothing to say about it, and these tests pin that it
/// says nothing: a per-property line here would be a second list, and two lists are two chances to
/// disagree.
/// </para>
/// </remarks>
/// <docs>fundamentals/perspectives/jsonb-containment</docs>
public class CanonicalTemporalConfigurationTests {
  private const string MODEL = """
    using System;
    using Microsoft.EntityFrameworkCore;
    using Whizbang.Core;
    using Whizbang.Core.Perspectives;
    using Whizbang.Data.EFCore.Custom;

    namespace TestApp;

    public record Occurred : IEvent;

    public record EventModel {
      [StreamId]
      public Guid EventId { get; init; }

      public DateTime OccurredAt { get; init; }
      public DateTimeOffset RecordedAt { get; init; }
      public DateOnly Day { get; init; }
      public TimeOnly Clock { get; init; }
      public TimeSpan Elapsed { get; init; }

      public DateTime? MaybeAt { get; init; }

      public string Label { get; init; } = string.Empty;
      public int Count { get; init; }
    }

    public class EventPerspective : IPerspectiveFor<EventModel, Occurred> {
      public EventModel Apply(EventModel currentData, Occurred eventData) => currentData;
    }

    [WhizbangDbContext]
    public class EventDbContext : DbContext {
      public EventDbContext(DbContextOptions<EventDbContext> options) : base(options) { }
    }
    """;

  private static async Task<string> _generatedAsync() {
    var result = await GeneratorTestHelpers.RunEFCoreGeneratorWithEFCoreReferencesAsync(MODEL);
    return string.Join("\n", result.GeneratedSources.Select(s => s.SourceText.ToString()));
  }

  /// <summary>
  /// The mapping configures no conversion for any temporal property, of any kind, optional or not.
  /// </summary>
  /// <remarks>
  /// The convention converts every temporal Entity Framework maps, inherited and nested ones
  /// included. A line here would name a subset, and the subset is exactly what went wrong before.
  /// </remarks>
  [Test]
  public async Task TheMappingNamesNoTemporalPropertyAsync() {
    var output = await _generatedAsync();

    await Assert.That(output).DoesNotContain("HasConversion<", StringComparison.Ordinal)
      .Because("the convention converts what Entity Framework maps; a per-property line is a "
        + "second list that can disagree with it");
    await Assert.That(output).DoesNotContain("CanonicalTemporalFormat", StringComparison.Ordinal)
      .Because("the mapping has no reason to know the stored form at all");
    foreach (var property in new[] { "OccurredAt", "RecordedAt", "Day", "Clock", "Elapsed", "MaybeAt" }) {
      await Assert.That(output).DoesNotContain($"d.Property(p => p.{property})", StringComparison.Ordinal);
    }
  }

  /// <summary>The document is still mapped property by property; only the conversion moved.</summary>
  [Test]
  public async Task TheDocumentIsStillMappedAsync() {
    var output = await _generatedAsync();

    await Assert.That(output).Contains("ComplexProperty(e => e.Data", StringComparison.Ordinal)
      .Because("a mapped document is what lets a filter compile to an extraction and carry an index");
    await Assert.That(output).Contains("ToJson(\"data\")", StringComparison.Ordinal);
  }

  /// <summary>
  /// The serializer is told about the same properties, so the writer matches the reader.
  /// </summary>
  /// <remarks>
  /// <para>
  /// A perspective row is written by the upsert, which serializes with System.Text.Json, and read by
  /// the mapping, which applies the value conversions above. Both sides have to convert the same set
  /// or a row written by one is unreadable by the other.
  /// </para>
  /// <para>
  /// Emitted per model rather than registered per type, and that distinction is load-bearing. A
  /// converter on the options reaches every date in every document, including the framework's own
  /// <c>PerspectiveMetadata.Timestamp</c>, which is mapped and read with no matching conversion: it
  /// became a number the reader could not parse, and broke every perspective row until the shape
  /// changed to this one.
  /// </para>
  /// </remarks>
  [Test]
  public async Task TheSerializerIsToldAboutTheSamePropertiesAsync() {
    var result = GeneratorTestHelper.RunGenerator<
      global::Whizbang.Data.EFCore.Postgres.Generators.PerspectivePersistenceJsonContextGenerator>(MODEL);
    var output = string.Join("\n",
      result.Results.SelectMany(r => r.GeneratedSources).Select(g => g.SourceText.ToString()));

    await Assert.That(output).Contains("RegisterTypeInfoModifier", StringComparison.Ordinal)
      .Because("the upsert writes the document, so a conversion the mapping alone knows about "
        + "leaves the writer producing rows the reader cannot parse");
    await Assert.That(output).Contains("CanonicalTemporalJsonConverters.ApplyTo", StringComparison.Ordinal);

    foreach (var property in new[] { "OccurredAt", "RecordedAt", "Day", "Clock", "Elapsed", "MaybeAt" }) {
      await Assert.That(output).Contains($"\"{property}\"", StringComparison.Ordinal)
        .Because($"'{property}' is converted by the mapping, so the writer has to convert it too");
    }
  }

  /// <summary>
  /// A model with nothing temporal gets no modifier, so nothing is registered for nothing.
  /// </summary>
  [Test]
  public async Task AModelWithNoTemporalPropertyGetsNoModifierAsync() {
    var result = GeneratorTestHelper.RunGenerator<
      global::Whizbang.Data.EFCore.Postgres.Generators.PerspectivePersistenceJsonContextGenerator>("""
      using System;
      using Microsoft.EntityFrameworkCore;
      using Whizbang.Core;
      using Whizbang.Core.Perspectives;

      namespace TestApp;

      public record Occurred : IEvent;

      public record PlainModel {
        [StreamId]
        public Guid Id { get; init; }
        public string Label { get; init; } = string.Empty;
      }

      public class PlainPerspective : IPerspectiveFor<PlainModel, Occurred> {
        public PlainModel Apply(PlainModel currentData, Occurred eventData) => currentData;
      }

      [WhizbangDbContext]
      public class PlainDbContext : DbContext {
        public PlainDbContext(DbContextOptions<PlainDbContext> options) : base(options) { }
      }
      """);

    var output = string.Join("\n",
      result.Results.SelectMany(r => r.GeneratedSources).Select(g => g.SourceText.ToString()));

    await Assert.That(output).DoesNotContain("CanonicalTemporalJsonConverters.ApplyTo",
      StringComparison.Ordinal)
      .Because("a model with no date has nothing to convert, and a modifier that matches nothing "
        + "still runs for every type the serializer resolves");
  }

  /// <summary>
  /// A computed temporal property is mapped by nobody and converted by the serializer, and the
  /// mapping does not have to know either of those things.
  /// </summary>
  /// <remarks>
  /// <para>
  /// A property with no setter and no backing field is a calculation, not storage. Entity Framework
  /// leaves it unmapped by convention, so the convention that converts mapped temporals never sees
  /// it; naming it in the configuration was what used to force Entity Framework to map it and fail
  /// model validation for the whole context at startup. There is nothing to name now.
  /// </para>
  /// <para>
  /// It still appears in the stored document, because the serializer writes read-only properties,
  /// and it keeps the canonical form its siblings have.
  /// </para>
  /// </remarks>
  [Test]
  public async Task AComputedTemporalIsSerializedAndNotMappedAsync() {
    const string SOURCE = """
      using System;
      using Microsoft.EntityFrameworkCore;
      using Whizbang.Core;
      using Whizbang.Core.Perspectives;
      using Whizbang.Data.EFCore.Custom;

      namespace TestApp;

      public record Ran : IEvent;

      public record RunModel {
        [StreamId]
        public Guid RunId { get; init; }

        public DateTimeOffset StartedAt { get; init; }
        public DateTimeOffset? FinishedAt { get; init; }

        public TimeSpan? Elapsed => FinishedAt.HasValue ? FinishedAt.Value - StartedAt : null;
      }

      public class RunPerspective : IPerspectiveFor<RunModel, Ran> {
        public RunModel Apply(RunModel currentData, Ran eventData) => currentData;
      }

      [WhizbangDbContext]
      public class RunDbContext : DbContext {
        public RunDbContext(DbContextOptions<RunDbContext> options) : base(options) { }
      }
      """;

    var result = await GeneratorTestHelpers.RunEFCoreGeneratorWithEFCoreReferencesAsync(SOURCE);
    var mapping = string.Join("\n", result.GeneratedSources.Select(s => s.SourceText.ToString()));

    var serialization = GeneratorTestHelper.RunGenerator<
      global::Whizbang.Data.EFCore.Postgres.Generators.PerspectivePersistenceJsonContextGenerator>(SOURCE);
    var serialized = string.Join("\n",
      serialization.Results.SelectMany(r => r.GeneratedSources).Select(g => g.SourceText.ToString()));

    await Assert.That(serialized).Contains("\"Elapsed\"", StringComparison.Ordinal)
      .Because("the serializer writes read-only properties, so the derived value is in the document "
        + "and keeps the canonical form the rest of the document uses");
    await Assert.That(mapping).DoesNotContain("p.Elapsed", StringComparison.Ordinal)
      .Because("naming a computed property is what forced Entity Framework to map it and fail at "
        + "startup; the mapping names nothing now");
    await Assert.That(mapping).DoesNotContain("p.StartedAt", StringComparison.Ordinal);
  }
}
