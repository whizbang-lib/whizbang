// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

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
/// serialize as the UUID string, the form Entity Framework writes under WhizbangIdTypeMapping
/// (#2518), and read only that form.
/// </remarks>
[Category("Shard1")]
public class PerspectivePersistenceJsonContextTests {
  /// <summary>
  /// Locks the byte-format invariant for [WhizbangId] struct properties on perspective TModels.
  /// The Order TModel has a TestOrderId OrderId property; through Path 1, OrderId MUST
  /// serialize as the UUID string so the bytes match EF's writer under the id's scalar type mapping (#2518).
  /// </summary>
  [Test]
  public async Task Serialize_OrderWithWhizbangIdProperty_EmitsTheUuidStringAsync() {
    // Arrange
    var testGuid = Guid.Parse("019e244a-6bda-78a9-a08f-a1011c9c31dd");
    var order = new Order {
      OrderId = new TestOrderId(testGuid),
      Amount = 100.00m,
      Status = "Created"
    };
    // CreateOptions chains: PerspectivePersistenceJsonContext (scalar [WhizbangId]) +
    // MessageJsonContext (perspective TModels and other discovered messages).
    // Order's JsonTypeInfo comes from MessageJsonContext; OrderId resolution falls through
    // back to PerspectivePersistenceJsonContext (first in the chain) and returns the scalar form.
    var options = PerspectivePersistenceJsonContext.CreateOptions(MessageJsonContext.Default);
    var typeInfo = options.GetTypeInfo(typeof(Order));

    // Act
    var json = JsonSerializer.Serialize(order, typeInfo);

    // Assert — OrderId appears as the UUID string, not the object {"Value": ...} Entity Framework
    // would discover for a struct. This is what it writes under WhizbangIdTypeMapping (#2518).
    await Assert.That(json).Contains("\"OrderId\":\"019e244a-6bda-78a9-a08f-a1011c9c31dd\"");
    await Assert.That(json).DoesNotContain("\"Value\"");
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

  /// <summary>
  /// The options the factory hands out are persistence options in every respect: they carry the
  /// persistence profile's converters, so a document serialized with them directly takes the same
  /// form the upsert writes.
  /// </summary>
  /// <remarks>
  /// A bare resolver chain looks like persistence options and is not: it writes a date as a
  /// rendering, and Entity Framework reads the document's dates as numbers. Any caller serializing
  /// with these options, a test mirroring the upsert included, has to get the profile's form.
  /// </remarks>
  [Test]
  public async Task CreateOptions_CarriesThePersistenceProfilesConvertersAsync() {
    var options = PerspectivePersistenceJsonContext.CreateOptions(
      MessageJsonContext.Default, global::Whizbang.Core.Generated.InfrastructureJsonContext.Default);
    var names = options.Converters.Select(c => c.GetType().Name).ToList();

    await Assert.That(names).Contains(nameof(Whizbang.Core.Perspectives.CanonicalTemporalJsonConverters.InstantConverter))
      .Because("options that claim to be persistence options must write a date the way persistence does");
    await Assert.That(names).Contains(nameof(Whizbang.Core.Perspectives.CanonicalTemporalJsonConverters.DurationConverter));
    await Assert.That(names).DoesNotContain(nameof(Whizbang.Core.Serialization.LenientDateTimeOffsetConverter))
      .Because("that reader is the wire's profile's, not this one's");

    var written = System.Text.Json.JsonSerializer.Serialize(
      new Whizbang.Core.Lenses.PerspectiveMetadata { EventType = "e", EventId = "1", Timestamp = new DateTime(2026, 3, 4, 5, 6, 7, DateTimeKind.Utc) },
      options.GetTypeInfo(typeof(Whizbang.Core.Lenses.PerspectiveMetadata)));
    await Assert.That(System.Text.Json.JsonElement.Parse(written).GetProperty("Timestamp").ValueKind)
      .IsEqualTo(System.Text.Json.JsonValueKind.Number);
  }

  /// <summary>An identifier stored as the UUID string reads back; the object form is refused.</summary>
  [Test]
  public async Task Deserialize_OrderWithAUuidStringIdentifier_ReadsItAndRefusesTheObjectFormAsync() {
    var options = PerspectivePersistenceJsonContext.CreateOptions(MessageJsonContext.Default);
    var typeInfo = options.GetTypeInfo(typeof(Order));

    var fromString = (Order)JsonSerializer.Deserialize(
      """{"OrderId":"019e244a-6bda-78a9-a08f-a1011c9c31dd","Amount":100,"Status":"Created"}""", typeInfo)!;

    await Assert.That(fromString.OrderId.Value).IsEqualTo(Guid.Parse("019e244a-6bda-78a9-a08f-a1011c9c31dd"));
    var error = await Assert.That(() => JsonSerializer.Deserialize(
      """{"OrderId":{"Value":"019e244a-6bda-78a9-a08f-a1011c9c31dd"},"Amount":100,"Status":"Created"}""", typeInfo))
      .Throws<JsonException>();
    await Assert.That(error!.Message).Contains("TestOrderId must be a UUID string");
  }

  /// <summary>Any other token is refused with a message naming the type and the forms accepted.</summary>
  [Test]
  public async Task Deserialize_OrderWithAnotherIdentifierToken_IsRefusedAsync() {
    var options = PerspectivePersistenceJsonContext.CreateOptions(MessageJsonContext.Default);
    var typeInfo = options.GetTypeInfo(typeof(Order));

    var error = await Assert.That(() => JsonSerializer.Deserialize(
      """{"OrderId":42,"Amount":100,"Status":"Created"}""", typeInfo)).Throws<JsonException>();

    await Assert.That(error!.Message).Contains("TestOrderId");
    await Assert.That(error.Message).Contains("Number");
  }
}
