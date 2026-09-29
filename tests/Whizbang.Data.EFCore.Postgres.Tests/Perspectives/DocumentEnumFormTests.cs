using System.Text.Json;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Serialization;
using Whizbang.Data.EFCore.Postgres.Tests.Collective;

namespace Whizbang.Data.EFCore.Postgres.Tests.Perspectives;

/// <summary>
/// Pins the form an enumeration takes inside a perspective document: its underlying number, the same scalar a
/// physical column holds. The persistence profile registers no string-enum converter, and the generated contexts
/// build an enum's metadata from the built-in numeric converter. If either changed, documents would start holding
/// names and every reader of <c>data</c> (the collective predicate compiler, expression indexes, consumers reading
/// the column directly) would disagree with the rows already stored.
/// </summary>
/// <docs>fundamentals/perspectives/physical-fields#enum-columns</docs>
public class DocumentEnumFormTests {
  [Test]
  public async Task PersistenceProfile_Enum_IsWrittenAsItsNumberAsync() {
    var options = JsonContextRegistry.CreateCombinedOptions(SerializationProfile.Persistence);

    var json = JsonSerializer.Serialize(CollectivePhysicalColumnIntegrationTests.TicketKind.Bug, options);

    await Assert.That(json).IsEqualTo("1");
  }

  [Test]
  public async Task PersistenceProfile_RegistersNoStringEnumConverterAsync() {
    var options = JsonContextRegistry.CreateCombinedOptions(SerializationProfile.Persistence);

    await Assert.That(options.Converters.Any(c => c is System.Text.Json.Serialization.JsonStringEnumConverter)).IsFalse();
  }
}
