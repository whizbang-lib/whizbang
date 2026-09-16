using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Lenses;
using Whizbang.Core.Perspectives;
using Whizbang.Core.Serialization;
using Whizbang.Data.EFCore.Postgres.Perspectives;

namespace Whizbang.Data.EFCore.Postgres.Tests;

/// <summary>
/// The one set of serializer options every perspective document is written and read with.
/// </summary>
/// <remarks>
/// <para>
/// The atomic upsert serializes a document with these options; a document stored as one value is
/// bound to Entity Framework through a converter that uses the same ones. One set, so the writer
/// and the reader cannot disagree about a document's form, and so the persistence profile's
/// converters reach a document whatever profile the data source carries.
/// </para>
/// <para>
/// Reused until the registry changes, because a set of options carries the serializer's metadata
/// cache and rebuilding it per call throws that cache away; rebuilt when the registry changes,
/// because an assembly loaded late registers its contexts late and a set built before that cannot
/// resolve them.
/// </para>
/// </remarks>
/// <code-under-test>src/Whizbang.Data.EFCore.Postgres/Perspectives/PerspectiveDocumentSerialization.cs</code-under-test>
[NotInParallel("PathOneProvider")]
[Category("Shard4")]
public class PerspectiveDocumentSerializationTests {
  private static readonly DateTime _startedAt = new(2026, 3, 4, 5, 6, 7, 890, DateTimeKind.Utc);

  [Before(Test)]
  public void Setup() {
    OpaqueDocumentFixture.EnsureRegistered();
    BaseUpsertStrategy.PathOnePersistenceOptionsProvider = null;
  }

  private static OpaqueDocument _document() => new() {
    Id = Guid.CreateVersion7(),
    StartedAt = _startedAt,
    EndedAt = new DateTimeOffset(_startedAt).AddHours(1),
    Turns = [new OpaqueTurn(Guid.CreateVersion7(), "hello", _startedAt.AddMinutes(1), null)],
  };

  /// <summary>The options are the persistence profile's, canonical converters first.</summary>
  [Test]
  public async Task TheOptionsAreThePersistenceProfilesAsync() {
    var names = PerspectiveDocumentSerialization.Options.Converters.Select(c => c.GetType().Name).ToList();

    await Assert.That(names).Contains(nameof(CanonicalTemporalJsonConverters.InstantConverter));
    await Assert.That(names).Contains(nameof(CanonicalTemporalJsonConverters.DurationConverter));
    await Assert.That(names).DoesNotContain(nameof(LenientDateTimeOffsetConverter))
      .Because("that reader is the wire's, and a document is not on the wire");
  }

  /// <summary>The same options come back until the registry changes, and new ones after it does.</summary>
  [Test]
  public async Task TheOptionsAreReusedUntilTheRegistryChangesAsync() {
    var first = PerspectiveDocumentSerialization.Options;
    var again = PerspectiveDocumentSerialization.Options;
    await Assert.That(ReferenceEquals(first, again)).IsTrue()
      .Because("a set of options carries the serializer's metadata cache, which a rebuild throws away");

    var before = JsonContextRegistry.Generation;
    JsonContextRegistry.RegisterContext(new NothingResolver());
    await Assert.That(JsonContextRegistry.Generation).IsGreaterThan(before);

    var rebuilt = PerspectiveDocumentSerialization.Options;
    await Assert.That(ReferenceEquals(first, rebuilt)).IsFalse()
      .Because("an assembly loaded late registers its contexts late, and options built before that "
        + "cannot resolve what it brought");
  }

  /// <summary>A converter writes the document in the canonical form and reads it back.</summary>
  [Test]
  public async Task AConverterWritesTheCanonicalFormAndReadsItBackAsync() {
    var converter = PerspectiveDocumentSerialization.ConverterFor<OpaqueDocument>();
    var document = _document();

    var stored = (string)converter.ConvertToProvider(document)!;
    var root = JsonDocument.Parse(stored).RootElement;
    await Assert.That(root.GetProperty("StartedAt").GetInt64())
      .IsEqualTo(CanonicalTemporalFormat.ToEpochMicroseconds(_startedAt));
    await Assert.That(root.GetProperty("EndedAt").ValueKind).IsEqualTo(JsonValueKind.Number);
    await Assert.That(root.GetProperty("Turns")[0].GetProperty("At").ValueKind).IsEqualTo(JsonValueKind.Number)
      .Because("a temporal in a collection element, reached through a positional record's constructor, "
        + "is converted like any other");

    var back = (OpaqueDocument)converter.ConvertFromProvider(stored)!;
    await Assert.That(back.StartedAt).IsEqualTo(_startedAt);
    await Assert.That(back.Turns[0].At).IsEqualTo(_startedAt.AddMinutes(1));
  }

  /// <summary>The framework's own documents convert the same way.</summary>
  [Test]
  public async Task TheFrameworkDocumentsConvertTheSameWayAsync() {
    var metadata = PerspectiveDocumentSerialization.ConverterFor<PerspectiveMetadata>();
    var scope = PerspectiveDocumentSerialization.ConverterFor<PerspectiveScope>();

    var storedMetadata = (string)metadata.ConvertToProvider(
      new PerspectiveMetadata { EventType = "e", EventId = "1", Timestamp = _startedAt })!;
    await Assert.That(JsonDocument.Parse(storedMetadata).RootElement.GetProperty("Timestamp").GetInt64())
      .IsEqualTo(CanonicalTemporalFormat.ToEpochMicroseconds(_startedAt));

    var storedScope = (string)scope.ConvertToProvider(new PerspectiveScope())!;
    await Assert.That((PerspectiveScope)scope.ConvertFromProvider(storedScope)!).IsNotNull();
  }

  /// <summary>A rendering an older release wrote reads through the converter too.</summary>
  [Test]
  public async Task AConverterReadsARenderingAsync() {
    var converter = PerspectiveDocumentSerialization.ConverterFor<OpaqueDocument>();

    var back = (OpaqueDocument)converter.ConvertFromProvider(
      """{"Id":"00000000-0000-0000-0000-000000000001","StartedAt":"2026-03-04T05:06:07.89Z","Turns":[]}""")!;

    await Assert.That(back.StartedAt).IsEqualTo(_startedAt);
  }

  /// <summary>
  /// A caller-supplied options provider, when the atomic path has one, is folded in exactly as the
  /// upsert folds it: the union first, the caller's resolver as a fallback, the profile's
  /// converters still reaching everything.
  /// </summary>
  [Test]
  public async Task ACallerProviderIsFoldedInBehindTheUnionAsync() {
    BaseUpsertStrategy.PathOnePersistenceOptionsProvider = () => new JsonSerializerOptions {
      TypeInfoResolver = OpaqueDocumentJsonContext.Default,
    };

    var options = PerspectiveDocumentSerialization.Options;
    var info = (JsonTypeInfo<OpaqueDocument>)options.GetTypeInfo(typeof(OpaqueDocument));
    var stored = JsonSerializer.Serialize(_document(), info);

    await Assert.That(JsonDocument.Parse(stored).RootElement.GetProperty("StartedAt").ValueKind)
      .IsEqualTo(JsonValueKind.Number)
      .Because("the caller's resolver answers for the type, and the profile's converters still apply");
  }

  private sealed class NothingResolver : IJsonTypeInfoResolver {
    public JsonTypeInfo? GetTypeInfo(Type type, JsonSerializerOptions options) => null;
  }
}
