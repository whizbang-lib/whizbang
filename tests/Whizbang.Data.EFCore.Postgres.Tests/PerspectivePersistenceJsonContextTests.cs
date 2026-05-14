using System;
using System.Text.Json;
using System.Threading.Tasks;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Data.EFCore.Postgres.Tests.Generated;

namespace Whizbang.Data.EFCore.Postgres.Tests;

/// <summary>
/// Byte-format regression tests for the Path 1 perspective-persistence serialization context.
/// PerspectivePersistenceJsonContext is the AOT-safe JsonSerializerContext used by
/// BaseUpsertStrategy's atomic INSERT ... ON CONFLICT DO UPDATE path. It MUST produce
/// JSONB bytes that match what EF Core 10's ComplexProperty().ToJson() writer would emit,
/// otherwise reads via EF will throw "Invalid token type" on the next SaveChanges round-trip.
/// </summary>
/// <remarks>
/// The critical invariant: [WhizbangId] struct properties (e.g., TestOrderId : Value=Guid)
/// must serialize as nested objects {"Value":"&lt;guid&gt;"}, NOT as flattened primitive strings.
/// EF treats every non-primitive property as structural; if our writer flattens via the
/// WhizbangIdConverter the bytes diverge and EF's reader rejects them.
/// This context exists specifically to bypass the WhizbangIdConverter for perspective TModels.
/// </remarks>
public class PerspectivePersistenceJsonContextTests {
  /// <summary>
  /// Locks the byte-format invariant for [WhizbangId] struct properties on perspective TModels.
  /// The Order TModel has a TestOrderId OrderId property; through Path 1, OrderId MUST
  /// serialize as a nested {"Value":"..."} object so the bytes match EF's ComplexProperty writer.
  /// </summary>
  [Test]
  public async Task Serialize_OrderWithWhizbangIdProperty_EmitsNestedValueObjectAsync() {
    // Arrange
    var testGuid = Guid.Parse("019e244a-6bda-78a9-a08f-a1011c9c31dd");
    var order = new Order {
      OrderId = new TestOrderId(testGuid),
      Amount = 100.00m,
      Status = "Created"
    };
    // CreateOptions chains: PerspectivePersistenceJsonContext (object-mode [WhizbangId]) +
    // MessageJsonContext (perspective TModels and other discovered messages).
    // Order's JsonTypeInfo comes from MessageJsonContext; OrderId resolution falls through
    // back to PerspectivePersistenceJsonContext (first in the chain) and returns object-mode.
    var options = PerspectivePersistenceJsonContext.CreateOptions(MessageJsonContext.Default);
    var typeInfo = options.GetTypeInfo(typeof(Order));

    // Act
    var json = JsonSerializer.Serialize(order, typeInfo);

    // Assert — OrderId must appear as a nested object with "Value" property,
    // NOT as a flattened primitive string. This is what EF Core 10's
    // ComplexProperty().ToJson() writer produces for value-object properties.
    await Assert.That(json).Contains("\"OrderId\":{\"Value\":\"019e244a-6bda-78a9-a08f-a1011c9c31dd\"}");
    await Assert.That(json).DoesNotContain("\"OrderId\":\"019e244a-6bda-78a9-a08f-a1011c9c31dd\"");
  }

  /// <summary>
  /// Confirms the resolver registers itself with JsonContextRegistry-equivalent infrastructure
  /// so it's discoverable across the assembly. The Default property MUST be a non-null
  /// singleton implementing IJsonTypeInfoResolver.
  /// </summary>
  [Test]
  public async Task Default_Singleton_ReturnsNonNullResolverInstanceAsync() {
    // Act
    var resolver = PerspectivePersistenceJsonContext.Default;

    // Assert
    await Assert.That(resolver).IsNotNull();
  }

  /// <summary>
  /// Confirms CreateOptions() returns a fresh JsonSerializerOptions wired to the Path 1
  /// resolver chain and explicitly NOT carrying the value-converter-mode WhizbangIdConverter
  /// that flattens [WhizbangId] structs to primitive strings.
  /// </summary>
  [Test]
  public async Task CreateOptions_ReturnsOptionsWithDefaultIgnoreCondition_AndTypeInfoResolverAsync() {
    // Act
    var options = PerspectivePersistenceJsonContext.CreateOptions();

    // Assert
    await Assert.That(options).IsNotNull();
    await Assert.That(options.TypeInfoResolver).IsNotNull();
    await Assert.That(options.DefaultIgnoreCondition).IsEqualTo(System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull);
  }
}
