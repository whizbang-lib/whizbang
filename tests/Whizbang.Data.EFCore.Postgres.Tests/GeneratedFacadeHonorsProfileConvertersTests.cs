using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Lenses;
using Whizbang.Core.Perspectives;
using Whizbang.Core.Serialization;
using Whizbang.Data.EFCore.Postgres.Tests.Generated;

namespace Whizbang.Data.EFCore.Postgres.Tests;

/// <summary>
/// That a generated <c>MessageJsonContext</c>, consulted before any other resolver, still yields the
/// profile's form for a temporal.
/// </summary>
/// <remarks>
/// <para>
/// The generated facade answers for primitive types itself, so that a message's <c>DateTime</c>
/// resolves without a round trip through the resolver chain. It used to answer with a fixed
/// built-in converter, which meant that whenever the facade came before the framework's own
/// contexts in a chain, a converter registered on the options for that type never ran: a date in
/// a document serialized through such a chain was a rendering under a profile that stores a
/// number. The persistence profile's union happened to order the framework's contexts first, and
/// nothing else did, so the defect surfaced only in options built by hand.
/// </para>
/// <para>
/// The facade now defers to a converter registered on the options before falling back to the
/// built-in one. These chains put the facade first on purpose.
/// </para>
/// </remarks>
/// <code-under-test>src/Whizbang.Generators/MessageJsonContextGenerator.cs</code-under-test>
[Category("Shard4")]
public class GeneratedFacadeHonorsProfileConvertersTests {
  private static readonly DateTime _at = new(2026, 3, 4, 5, 6, 7, DateTimeKind.Utc);

  private static JsonValueKind _timestampKind(JsonSerializerOptions options) {
    var json = JsonSerializer.Serialize(
      new PerspectiveMetadata { EventType = "e", EventId = "1", Timestamp = _at },
      options.GetTypeInfo(typeof(PerspectiveMetadata)));
    return JsonDocument.Parse(json).RootElement.GetProperty("Timestamp").ValueKind;
  }

  /// <summary>The facade first, then the framework's context: still the profile's number.</summary>
  [Test]
  public async Task TheFacadeConsultedFirstStillYieldsTheProfilesFormAsync() {
    var union = JsonContextRegistry.CreateCombinedOptions(SerializationProfile.Persistence);
    var facadeFirst = new JsonSerializerOptions(union) {
      TypeInfoResolver = JsonTypeInfoResolver.Combine(
        MessageJsonContext.Default, global::Whizbang.Core.Generated.InfrastructureJsonContext.Default),
    };

    await Assert.That(_timestampKind(facadeFirst)).IsEqualTo(JsonValueKind.Number)
      .Because("the converter registered on the options for DateTime has to win over the facade's "
        + "built-in one, or every chain that consults the facade first writes a rendering");
  }

  /// <summary>The generated atomic-upsert factory, which puts the facade first, yields the number.</summary>
  [Test]
  public async Task TheGeneratedPersistenceFactoryYieldsTheProfilesFormAsync() {
    var options = PerspectivePersistenceJsonContext.CreateOptions(
      MessageJsonContext.Default, global::Whizbang.Core.Generated.InfrastructureJsonContext.Default);

    await Assert.That(_timestampKind(options)).IsEqualTo(JsonValueKind.Number);
    await Assert.That(JsonDocument.Parse(JsonSerializer.Serialize(
        new PerspectiveMetadata { EventType = "e", EventId = "1", Timestamp = _at },
        options.GetTypeInfo(typeof(PerspectiveMetadata)))).RootElement.GetProperty("Timestamp").GetInt64())
      .IsEqualTo(CanonicalTemporalFormat.ToEpochMicroseconds(_at));
  }

  /// <summary>On the default profile the facade first still yields the wire's rendering.</summary>
  [Test]
  public async Task TheFacadeOnTheDefaultProfileStillYieldsARenderingAsync() {
    var union = JsonContextRegistry.CreateCombinedOptions();
    var facadeFirst = new JsonSerializerOptions(union) {
      TypeInfoResolver = JsonTypeInfoResolver.Combine(
        MessageJsonContext.Default, global::Whizbang.Core.Generated.InfrastructureJsonContext.Default),
    };

    await Assert.That(_timestampKind(facadeFirst)).IsEqualTo(JsonValueKind.String)
      .Because("deferring to a registered converter changes nothing where none is registered");
  }
}
