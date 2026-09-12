using TUnit.Assertions.Extensions;

namespace Whizbang.Generators.Tests;

/// <summary>
/// That a perspective's dates, times and durations are configured to store as numbers.
/// </summary>
/// <remarks>
/// <para>
/// The stored form is a decision the framework makes once rather than one every model repeats, so the
/// converters are emitted into the generated configuration. A model author writes an ordinary
/// <c>DateTime</c> property and never sees this.
/// </para>
/// <para>
/// It is also what makes a new installation need no migration at all: the first row a fresh database
/// receives is already in the canonical form, so nothing is ever written in one shape and rewritten
/// into another.
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

  /// <summary>Each temporal type is converted to the number that is its canonical stored form.</summary>
  [Test]
  [Arguments("OccurredAt", "ToEpochMicroseconds")]
  [Arguments("RecordedAt", "ToEpochMicroseconds")]
  [Arguments("Day", "ToEpochDays")]
  [Arguments("Clock", "ToMicrosecondsOfDay")]
  [Arguments("Elapsed", "ToTicks")]
  public async Task ATemporalPropertyIsStoredAsANumberAsync(string property, string conversion) {
    var output = await _generatedAsync();

    await Assert.That(output).Contains(property, StringComparison.Ordinal);
    await Assert.That(output).Contains(conversion, StringComparison.Ordinal)
      .Because($"'{property}' has to reach the document as a number, or its extraction cannot carry "
        + "an index and a range over it cannot be answered");
  }

  /// <summary>An optional one is converted too, since a null simply has no entry.</summary>
  [Test]
  public async Task AnOptionalTemporalPropertyIsConvertedAsync() {
    var output = await _generatedAsync();

    await Assert.That(output).Contains("MaybeAt", StringComparison.Ordinal)
      .Because("an absent value is an absent key either way, so nullability changes nothing about "
        + "the form the value takes when it is present");
  }

  /// <summary>Nothing else is touched, because nothing else needs it.</summary>
  [Test]
  [Arguments("Label")]
  [Arguments("Count")]
  public async Task ANonTemporalPropertyIsLeftAloneAsync(string property) {
    var output = await _generatedAsync();

    var conversions = output.Split('\n')
      .Where(line => line.Contains("HasConversion", StringComparison.Ordinal)
                     && line.Contains(property, StringComparison.Ordinal));

    await Assert.That(conversions).IsEmpty()
      .Because("a string and a number already store in a form the index can reach, so converting "
        + "them would be cost without purpose");
  }

  /// <summary>
  /// The emitted line is pinned exactly, because something else has to mirror it.
  /// </summary>
  /// <remarks>
  /// <para>
  /// A value conversion needs a property expression per property, and those exist only at compile
  /// time, so nothing at runtime can apply this configuration on a caller's behalf. That leaves a
  /// seam: <c>CanonicalTemporalStorageTests</c> writes the same configuration by hand to prove it
  /// works against a real database, and the two could drift apart without either failing.
  /// </para>
  /// <para>
  /// A <c>Contains</c> on a method name would not catch a drift, which is why this is the whole line.
  /// If it changes, that file has to change with it.
  /// </para>
  /// </remarks>
  [Test]
  public async Task TheEmittedLineIsPinnedAsync() {
    var output = await _generatedAsync();

    await Assert.That(output).Contains(
      "d.Property(p => p.OccurredAt).HasConversion<long>("
      + "v => global::Whizbang.Core.Perspectives.CanonicalTemporalFormat.ToEpochMicroseconds(v), "
      + "v => global::Whizbang.Core.Perspectives.CanonicalTemporalFormat.FromEpochMicroseconds(v));",
      StringComparison.Ordinal)
      .Because("the integration test that proves this configuration works writes it by hand, so the "
        + "emitted form has to be pinned or the two can drift apart with both still passing");
  }

  /// <summary>
  /// The optional form is pinned too, since its null branch is the part most easily got wrong.
  /// </summary>
  [Test]
  public async Task TheOptionalEmittedLineIsPinnedAsync() {
    var output = await _generatedAsync();

    await Assert.That(output).Contains(
      "d.Property(p => p.MaybeAt).HasConversion<long?>("
      + "v => v == null ? (long?)null : "
      + "global::Whizbang.Core.Perspectives.CanonicalTemporalFormat.ToEpochMicroseconds(v.Value), "
      + "v => v == null ? (global::System.DateTime?)null : "
      + "global::Whizbang.Core.Perspectives.CanonicalTemporalFormat.FromEpochMicroseconds(v.Value));",
      StringComparison.Ordinal)
      .Because("a converter that lost its null branch would write the epoch for an absent value, "
        + "which reads back as a real date rather than as nothing");
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
  /// The conversion is the framework's own, not an expression written out per property.
  /// </summary>
  /// <remarks>
  /// Worth asserting rather than assuming: the stored form is a compatibility contract, and having
  /// one definition of it that the generator calls is what keeps the writer, the reader and the
  /// backfill from drifting apart. An inlined expression per property is three chances to disagree.
  /// </remarks>
  [Test]
  public async Task TheConversionIsTheSharedOneAsync() {
    var output = await _generatedAsync();

    await Assert.That(output).Contains("CanonicalTemporalFormat", StringComparison.Ordinal);
  }
}
