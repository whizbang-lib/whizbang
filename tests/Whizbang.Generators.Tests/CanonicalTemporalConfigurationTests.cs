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

  /// <summary>
  /// A document stored as one serialized value is bound to the persistence profile explicitly.
  /// </summary>
  /// <remarks>
  /// <para>
  /// Such a document is written by the upsert under the persistence profile and read back by
  /// Entity Framework as one value. Read through the data source's JSON options it was read under
  /// the default profile, whose only date reader takes a rendering, and every row holding a
  /// canonical number was unreadable. The data source cannot move profiles, because the outbox,
  /// inbox and event store metadata read through it in the wire's form.
  /// </para>
  /// <para>
  /// So the column is bound to the profile it is written in, through a converter that uses the
  /// same options the upsert does. This pins the binding for the document, its metadata and its
  /// scope; a plain jsonb mapping here is the failure coming back.
  /// </para>
  /// </remarks>
  [Test]
  public async Task AnOpaqueDocumentIsBoundToThePersistenceProfileAsync() {
    var result = await GeneratorTestHelpers.RunEFCoreGeneratorWithEFCoreReferencesAsync("""
      using System;
      using System.Collections.Generic;
      using Microsoft.EntityFrameworkCore;
      using Whizbang.Core;
      using Whizbang.Core.Perspectives;
      using Whizbang.Data.EFCore.Custom;

      namespace TestApp;

      public record Spoke : IEvent;

      public record Attachment(Guid UploadId, string FileName);

      public record Turn(Guid TurnId, DateTime At, IReadOnlyList<Attachment>? Attachments);

      public class ConversationModel {
        [StreamId]
        public Guid Id { get; init; }
        public DateTime StartedAt { get; init; }
        public List<Turn> Turns { get; init; } = new();
      }

      public class ConversationPerspective : IPerspectiveFor<ConversationModel, Spoke> {
        public ConversationModel Apply(ConversationModel currentData, Spoke eventData) => currentData;
      }

      [WhizbangDbContext]
      public class ConversationDbContext : DbContext {
        public ConversationDbContext(DbContextOptions<ConversationDbContext> options) : base(options) { }
      }
      """);
    var output = string.Join("\n", result.GeneratedSources.Select(s => s.SourceText.ToString()));

    const string SERIALIZATION = "global::Whizbang.Data.EFCore.Postgres.Perspectives.PerspectiveDocumentSerialization";
    await Assert.That(output).Contains(
      $".HasConversion({SERIALIZATION}.ConverterFor<global::TestApp.ConversationModel>())",
      StringComparison.Ordinal)
      .Because("the document is written under the persistence profile, so it has to be read under it, "
        + "whatever profile the data source carries");
    await Assert.That(output).Contains(
      $".HasConversion({SERIALIZATION}.ConverterFor<global::Whizbang.Core.Lenses.PerspectiveMetadata>())",
      StringComparison.Ordinal);
    await Assert.That(output).Contains(
      $".HasConversion({SERIALIZATION}.ConverterFor<global::Whizbang.Core.Lenses.PerspectiveScope>())",
      StringComparison.Ordinal);
    await Assert.That(output).DoesNotContain("HasColumnType(\"jsonb\");", StringComparison.Ordinal)
      .Because("a jsonb column with no binding is read through the data source, on the wrong profile");
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
  /// The serializer registers nothing per model either: the persistence profile carries the
  /// conversion for every document.
  /// </summary>
  /// <remarks>
  /// <para>
  /// A perspective row is written by the upsert, which serializes with System.Text.Json, and read
  /// by the mapping, which converts every temporal it maps. The writer used to be told, per model,
  /// which properties to convert, from the same partial discovery the mapping used; the two agreed
  /// by construction and were wrong together about every placement the discovery missed.
  /// </para>
  /// <para>
  /// Now the converters sit on the persistence profile's options and the serializer applies them
  /// wherever the type occurs. There is nothing for a generator to name, and this pins that it
  /// names nothing: a per-model modifier here would narrow the writer to a subset the reader no
  /// longer shares.
  /// </para>
  /// </remarks>
  [Test]
  public async Task TheSerializerRegistersNoPerModelModifierAsync() {
    var result = GeneratorTestHelper.RunGenerator<
      global::Whizbang.Data.EFCore.Postgres.Generators.PerspectivePersistenceJsonContextGenerator>(MODEL);
    var output = string.Join("\n",
      result.Results.SelectMany(r => r.GeneratedSources).Select(g => g.SourceText.ToString()));

    await Assert.That(output).DoesNotContain("RegisterTypeInfoModifier", StringComparison.Ordinal)
      .Because("a modifier names a subset of the document's temporals, and the subset is exactly "
        + "what the reader no longer shares");
    await Assert.That(output).DoesNotContain("CanonicalTemporalJsonConverters", StringComparison.Ordinal);
    await Assert.That(output).Contains("SerializationProfile.Persistence", StringComparison.Ordinal)
      .Because("the persistence context still joins the profile whose options carry the conversion");
  }

  /// <summary>
  /// A computed temporal property is mapped by nobody, and the mapping does not have to know it.
  /// </summary>
  /// <remarks>
  /// A property with no setter and no backing field is a calculation, not storage. Entity Framework
  /// leaves it unmapped by convention, so the convention that converts mapped temporals never sees
  /// it; naming it in the configuration was what used to force Entity Framework to map it and fail
  /// model validation for the whole context at startup. There is nothing to name now.
  /// </remarks>
  [Test]
  public async Task AComputedTemporalIsNotMappedAsync() {
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

    await Assert.That(mapping).DoesNotContain("p.Elapsed", StringComparison.Ordinal)
      .Because("naming a computed property is what forced Entity Framework to map it and fail at "
        + "startup; the mapping names nothing now");
    await Assert.That(mapping).DoesNotContain("p.StartedAt", StringComparison.Ordinal);
  }
}
