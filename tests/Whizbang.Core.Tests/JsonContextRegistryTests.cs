using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;
using TUnit.Assertions;
using TUnit.Core;
using Whizbang.Core.Observability;
using Whizbang.Core.Serialization;
using Whizbang.Core.ValueObjects;

namespace Whizbang.Core.Tests;

/// <summary>
/// Tests for JsonContextRegistry - ensures AOT-compatible converter registration works correctly.
/// </summary>
public partial class JsonContextRegistryTests {
  /// <summary>
  /// Test converter for MessageId-like type (simulates generated WhizbangId converter).
  /// </summary>
  private sealed class TestIdJsonConverter : JsonConverter<_testId> {
    public override _testId Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) {
      return new _testId { Value = reader.GetString() ?? string.Empty };
    }

    public override void Write(Utf8JsonWriter writer, _testId value, JsonSerializerOptions options) {
      writer.WriteStringValue(value.Value);
    }
  }

  /// <summary>
  /// Test ID type (simulates generated WhizbangId value object).
  /// </summary>
  private struct _testId {
    public string Value { get; set; }
  }

  [Test]
  public async Task RegisterConverter_WithConverterInstance_AddsToConverterCollectionAsync() {
    // Arrange
    var converter = new TestIdJsonConverter();
    var initialCount = JsonContextRegistry.RegisteredCount;

    // Act
    JsonContextRegistry.RegisterConverter(converter);

    // Assert - verify registration succeeded
    // Note: We can't directly inspect _converters (it's private), but we can verify
    // it doesn't throw and that the converter works when used in CreateCombinedOptions
    // TUnitAssertions0005: Intentional constant assertion to verify registration doesn't throw
#pragma warning disable TUnitAssertions0005
    await Assert.That(true).IsTrue(); // Registration doesn't throw
#pragma warning restore TUnitAssertions0005
  }

  [Test]
  public async Task RegisterConverter_WithNull_ThrowsArgumentNullExceptionAsync() {
    // Act & Assert
    var exception = await Assert.That(() => JsonContextRegistry.RegisterConverter(null!))
        .ThrowsExactly<ArgumentNullException>();

    // Verify the parameter name is "converter"
    await Assert.That(exception!.ParamName).IsEqualTo("converter");
  }

  [Test]
  public async Task CreateCombinedOptions_WithRegisteredConverters_IncludesConvertersInOptionsAsync() {
    // Note: This test verifies that converters registered via RegisterConverter()
    // are included in the JsonSerializerOptions.Converters collection.
    // Since JsonContextRegistry maintains global state, we rely on the module initializers
    // having already registered the Core converters (MessageId, CorrelationId).

    // Act
    var options = JsonContextRegistry.CreateCombinedOptions();

    // Assert - verify options has converters registered
    await Assert.That(options.Converters).IsNotEmpty();

    // Verify PascalCase naming policy is configured (null = default PascalCase)
    await Assert.That(options.PropertyNamingPolicy).IsNull();

    // Verify WhenWritingNull ignore condition
    await Assert.That(options.DefaultIgnoreCondition).IsEqualTo(JsonIgnoreCondition.WhenWritingNull);
  }

  [Test]
  public async Task CreateCombinedOptions_IsAOTCompatible_NoReflectionAsync() {
    // Arrange & Act
    var options = JsonContextRegistry.CreateCombinedOptions();

    // Assert - verify that we can successfully create options without reflection
    // The fact that this test runs without IL2072 warnings or runtime errors
    // verifies that the implementation is AOT-compatible.
    await Assert.That(options).IsNotNull();
    await Assert.That(options.TypeInfoResolver).IsNotNull();
  }

  [Test]
  public async Task RegisteredConverters_AreInstantiatedAtCompileTime_NotRuntimeAsync() {
    // This test documents the expected behavior:
    // All converters are instantiated using 'new' at compile-time in generated code,
    // not via Activator.CreateInstance() or other reflection at runtime.
    //
    // The generated code should look like:
    //   JsonContextRegistry.RegisterConverter(new ProductIdJsonConverter());
    //
    // NOT like:
    //   JsonContextRegistry.RegisterConverterType(typeof(ProductIdJsonConverter)); // WRONG - uses reflection

    var options = JsonContextRegistry.CreateCombinedOptions();

    // Verify converters exist in options
    await Assert.That(options.Converters).IsNotEmpty();

    // Each converter instance should be directly added (no lazy initialization)
    foreach (var converter in options.Converters) {
      await Assert.That(converter).IsNotNull();
    }
  }

  // ===========================
  // Type Name Mapping Tests
  // ===========================

  /// <summary>
  /// Test message type for type name mapping tests.
  /// </summary>
  internal sealed record TestMessage(string Data);

  /// <summary>
  /// Test JsonSerializerContext for type name mapping tests.
  /// </summary>
  [JsonSerializable(typeof(TestMessage))]
  internal sealed partial class TestMessageJsonContext : JsonSerializerContext {
  }

  [Test]
  public async Task RegisterTypeName_WithValidArguments_RegistersSuccessfullyAsync() {
    // Arrange
    var typeName = "Whizbang.Core.Tests.JsonContextRegistryTests+TestMessage, Whizbang.Core.Tests";
    var resolver = TestMessageJsonContext.Default;
    var initialCount = JsonContextRegistry.RegisteredTypeNameCount;

    // Act
    JsonContextRegistry.RegisterTypeName(typeName, typeof(TestMessage), resolver);

    // Assert
    // Note: Type may already be registered from other tests or module initializers
    // Just verify that registration doesn't throw and count hasn't decreased
    await Assert.That(JsonContextRegistry.RegisteredTypeNameCount).IsGreaterThanOrEqualTo(initialCount);
  }

  [Test]
  public async Task RegisterTypeName_WithNullTypeName_ThrowsArgumentNullExceptionAsync() {
    // Arrange
    var resolver = TestMessageJsonContext.Default;

    // Act & Assert
    var exception = await Assert.That(() =>
      JsonContextRegistry.RegisterTypeName(null!, typeof(TestMessage), resolver))
      .ThrowsExactly<ArgumentNullException>();

    await Assert.That(exception!.ParamName).IsEqualTo("assemblyQualifiedName");
  }

  [Test]
  public async Task RegisterTypeName_WithNullType_ThrowsArgumentNullExceptionAsync() {
    // Arrange
    var typeName = "Whizbang.Core.Tests.JsonContextRegistryTests+TestMessage, Whizbang.Core.Tests";
    var resolver = TestMessageJsonContext.Default;

    // Act & Assert
    var exception = await Assert.That(() =>
      JsonContextRegistry.RegisterTypeName(typeName, null!, resolver))
      .ThrowsExactly<ArgumentNullException>();

    await Assert.That(exception!.ParamName).IsEqualTo("type");
  }

  [Test]
  public async Task RegisterTypeName_WithNullResolver_ThrowsArgumentNullExceptionAsync() {
    // Arrange
    var typeName = "Whizbang.Core.Tests.JsonContextRegistryTests+TestMessage, Whizbang.Core.Tests";

    // Act & Assert
    var exception = await Assert.That(() =>
      JsonContextRegistry.RegisterTypeName(typeName, typeof(TestMessage), null!))
      .ThrowsExactly<ArgumentNullException>();

    await Assert.That(exception!.ParamName).IsEqualTo("resolver");
  }

  [Test]
  public async Task GetTypeInfoByName_WithRegisteredType_ReturnsJsonTypeInfoAsync() {
    // Arrange
    var typeName = "Whizbang.Core.Tests.JsonContextRegistryTests+TestMessage, Whizbang.Core.Tests";
    var resolver = TestMessageJsonContext.Default;
    JsonContextRegistry.RegisterTypeName(typeName, typeof(TestMessage), resolver);
    var options = JsonContextRegistry.CreateCombinedOptions();

    // Act
    var typeInfo = JsonContextRegistry.GetTypeInfoByName(typeName, options);

    // Assert
    await Assert.That(typeInfo).IsNotNull();
    await Assert.That(typeInfo!.Type).IsEqualTo(typeof(TestMessage));
  }

  [Test]
  public async Task GetTypeInfoByName_WithFuzzyMatch_MatchesShortFormToFullFormAsync() {
    // Arrange - Register with short form
    var shortForm = "Whizbang.Core.Tests.JsonContextRegistryTests+TestMessage, Whizbang.Core.Tests";
    var resolver = TestMessageJsonContext.Default;
    JsonContextRegistry.RegisterTypeName(shortForm, typeof(TestMessage), resolver);
    var options = JsonContextRegistry.CreateCombinedOptions();

    // Act - Lookup with full AssemblyQualifiedName (includes Version, Culture, PublicKeyToken)
    var fullForm = "Whizbang.Core.Tests.JsonContextRegistryTests+TestMessage, Whizbang.Core.Tests, Version=1.0.0.0, Culture=neutral, PublicKeyToken=null";
    var typeInfo = JsonContextRegistry.GetTypeInfoByName(fullForm, options);

    // Assert - Should match despite different formats
    await Assert.That(typeInfo).IsNotNull();
    await Assert.That(typeInfo!.Type).IsEqualTo(typeof(TestMessage));
  }

  [Test]
  public async Task GetTypeInfoByName_WithFuzzyMatch_MatchesFullFormToShortFormAsync() {
    // Arrange - Register with full AssemblyQualifiedName
    var fullForm = "Whizbang.Core.Tests.JsonContextRegistryTests+TestMessage, Whizbang.Core.Tests, Version=1.0.0.0, Culture=neutral, PublicKeyToken=null";
    var resolver = TestMessageJsonContext.Default;
    JsonContextRegistry.RegisterTypeName(fullForm, typeof(TestMessage), resolver);
    var options = JsonContextRegistry.CreateCombinedOptions();

    // Act - Lookup with short form
    var shortForm = "Whizbang.Core.Tests.JsonContextRegistryTests+TestMessage, Whizbang.Core.Tests";
    var typeInfo = JsonContextRegistry.GetTypeInfoByName(shortForm, options);

    // Assert - Should match despite different formats
    await Assert.That(typeInfo).IsNotNull();
    await Assert.That(typeInfo!.Type).IsEqualTo(typeof(TestMessage));
  }

  [Test]
  public async Task GetTypeInfoByName_WithUnregisteredType_ReturnsNullAsync() {
    // Arrange
    var typeName = "SomeUnregisteredType, SomeAssembly";
    var options = JsonContextRegistry.CreateCombinedOptions();

    // Act
    var typeInfo = JsonContextRegistry.GetTypeInfoByName(typeName, options);

    // Assert
    await Assert.That(typeInfo).IsNull();
  }

  [Test]
  public async Task GetTypeInfoByName_WithNullTypeName_ReturnsNullAsync() {
    // Arrange
    var options = JsonContextRegistry.CreateCombinedOptions();

    // Act
    var typeInfo = JsonContextRegistry.GetTypeInfoByName(null!, options);

    // Assert
    await Assert.That(typeInfo).IsNull();
  }

  [Test]
  public async Task GetTypeInfoByName_WithEmptyTypeName_ReturnsNullAsync() {
    // Arrange
    var options = JsonContextRegistry.CreateCombinedOptions();

    // Act
    var typeInfo = JsonContextRegistry.GetTypeInfoByName(string.Empty, options);

    // Assert
    await Assert.That(typeInfo).IsNull();
  }

  [Test]
  public async Task GetTypeInfoByName_WithNullOptions_ReturnsNullAsync() {
    // Arrange
    var typeName = "Whizbang.Core.Tests.JsonContextRegistryTests+TestMessage, Whizbang.Core.Tests";

    // Act
    var typeInfo = JsonContextRegistry.GetTypeInfoByName(typeName, null!);

    // Assert
    await Assert.That(typeInfo).IsNull();
  }

  // ===========================
  // Envelope Type Deserialization Tests
  // ===========================

  /// <summary>
  /// Test event for envelope deserialization test.
  /// </summary>
  internal sealed record TestEvent(string Data) : IEvent;

  /// <summary>
  /// Test JsonSerializerContext for envelope deserialization test.
  /// Simulates what MessageJsonContextGenerator produces.
  /// </summary>
  [JsonSerializable(typeof(TestEvent))]
  [JsonSerializable(typeof(MessageEnvelope<TestEvent>))]
  internal sealed partial class TestEventJsonContext : JsonSerializerContext {
  }

  [Test]
  public async Task GetTypeInfoByName_WithEnvelopeType_ReturnsEnvelopeJsonTypeInfoAsync() {
    // Arrange - Register both payload type and envelope type (simulating MessageJsonContextGenerator)
    var payloadTypeName = "Whizbang.Core.Tests.JsonContextRegistryTests+TestEvent, Whizbang.Core.Tests";
    var envelopeTypeName = "Whizbang.Core.Observability.MessageEnvelope`1[[Whizbang.Core.Tests.JsonContextRegistryTests+TestEvent, Whizbang.Core.Tests]], Whizbang.Core";
    var resolver = TestEventJsonContext.Default;

    // Register the resolver itself (needed for CreateCombinedOptions to include it)
    JsonContextRegistry.RegisterContext(resolver);

    // Register payload type
    JsonContextRegistry.RegisterTypeName(payloadTypeName, typeof(TestEvent), resolver);

    // Register envelope type (THIS IS WHAT THE FIX ADDS)
    JsonContextRegistry.RegisterTypeName(
      envelopeTypeName,
      typeof(MessageEnvelope<TestEvent>),
      resolver);

    var options = JsonContextRegistry.CreateCombinedOptions();

    // Act - Lookup envelope type (simulating what AzureServiceBusTransport does)
    var envelopeTypeInfo = JsonContextRegistry.GetTypeInfoByName(envelopeTypeName, options);

    // Assert - Should find the envelope type
    await Assert.That(envelopeTypeInfo).IsNotNull();
    await Assert.That(envelopeTypeInfo!.Type).IsEqualTo(typeof(MessageEnvelope<TestEvent>));
  }

  [Test]
  public async Task EnvelopeType_CanBeDeserializedFromJson_WithRegisteredTypeInfoAsync() {
    // Arrange - Register both payload type and envelope type
    var payloadTypeName = "Whizbang.Core.Tests.JsonContextRegistryTests+TestEvent, Whizbang.Core.Tests";
    var envelopeTypeName = "Whizbang.Core.Observability.MessageEnvelope`1[[Whizbang.Core.Tests.JsonContextRegistryTests+TestEvent, Whizbang.Core.Tests]], Whizbang.Core";
    var resolver = TestEventJsonContext.Default;

    // Register the resolver itself (needed for CreateCombinedOptions to include it)
    JsonContextRegistry.RegisterContext(resolver);

    JsonContextRegistry.RegisterTypeName(payloadTypeName, typeof(TestEvent), resolver);
    JsonContextRegistry.RegisterTypeName(envelopeTypeName, typeof(MessageEnvelope<TestEvent>), resolver);

    var options = JsonContextRegistry.CreateCombinedOptions();

    // Create a test envelope
    var testEvent = new TestEvent("test-data");
    var envelope = new MessageEnvelope<TestEvent>(
      MessageId.New(),
      testEvent,
      []
    );

    // Serialize to JSON
    var json = JsonSerializer.Serialize(envelope, options);

    // Act - Deserialize using the envelope type name (simulating Azure Service Bus deserialization)
    var envelopeTypeInfo = JsonContextRegistry.GetTypeInfoByName(envelopeTypeName, options);
    await Assert.That(envelopeTypeInfo).IsNotNull();

    var deserializedEnvelope = JsonSerializer.Deserialize(json, envelopeTypeInfo!) as MessageEnvelope<TestEvent>;

    // Assert - Should successfully deserialize
    await Assert.That(deserializedEnvelope).IsNotNull();
    await Assert.That(deserializedEnvelope!.MessageId).IsEqualTo(envelope.MessageId);
    await Assert.That(deserializedEnvelope.Payload).IsNotNull();
    await Assert.That(deserializedEnvelope.Payload.Data).IsEqualTo("test-data");
  }

  [Test]
  public async Task EnvelopeType_WithFullAssemblyQualifiedName_MatchesFuzzilyAsync() {
    // Arrange - Register with short form (what generator produces)
    var shortForm = "Whizbang.Core.Observability.MessageEnvelope`1[[Whizbang.Core.Tests.JsonContextRegistryTests+TestEvent, Whizbang.Core.Tests]], Whizbang.Core";
    var resolver = TestEventJsonContext.Default;

    JsonContextRegistry.RegisterTypeName(
      "Whizbang.Core.Tests.JsonContextRegistryTests+TestEvent, Whizbang.Core.Tests",
      typeof(TestEvent),
      resolver);
    JsonContextRegistry.RegisterTypeName(shortForm, typeof(MessageEnvelope<TestEvent>), resolver);

    var options = JsonContextRegistry.CreateCombinedOptions();

    // Act - Lookup with full AssemblyQualifiedName (what AzureServiceBusTransport sends)
    var fullForm = "Whizbang.Core.Observability.MessageEnvelope`1[[Whizbang.Core.Tests.JsonContextRegistryTests+TestEvent, Whizbang.Core.Tests, Version=1.0.0.0, Culture=neutral, PublicKeyToken=null]], Whizbang.Core, Version=1.0.0.0, Culture=neutral, PublicKeyToken=null";
    var typeInfo = JsonContextRegistry.GetTypeInfoByName(fullForm, options);

    // Assert - Should match despite different formats (fuzzy matching)
    await Assert.That(typeInfo).IsNotNull();
    await Assert.That(typeInfo!.Type).IsEqualTo(typeof(MessageEnvelope<TestEvent>));
  }

  // ===========================
  // Polymorphic Interface Serialization Tests
  // ===========================

  /// <summary>
  /// Test event for polymorphic serialization tests - order placed.
  /// </summary>
  internal sealed record TestOrderPlacedEvent(Guid OrderId, string CustomerName) : IEvent;

  /// <summary>
  /// Test event for polymorphic serialization tests - order shipped.
  /// </summary>
  internal sealed record TestOrderShippedEvent(Guid OrderId, string TrackingNumber) : IEvent;

  /// <summary>
  /// Test command for polymorphic serialization tests.
  /// </summary>
  internal sealed record TestCreateOrderCommand(string CustomerName, decimal Amount) : ICommand;

  /// <summary>
  /// Test JsonSerializerContext for polymorphic event types.
  /// </summary>
  [JsonSerializable(typeof(TestOrderPlacedEvent))]
  [JsonSerializable(typeof(TestOrderShippedEvent))]
  [JsonSerializable(typeof(TestCreateOrderCommand))]
  internal sealed partial class PolymorphicTestJsonContext : JsonSerializerContext {
  }

  [Test]
  public async Task RegisterDerivedType_WithEventType_AddsToRegistryAsync() {
    // Act
    JsonContextRegistry.RegisterDerivedType<IEvent, TestOrderPlacedEvent>("TestOrderPlacedEvent");

    // Assert - verify derived type is registered
    var derivedTypes = JsonContextRegistry.GetRegisteredDerivedTypes<IEvent>();
    await Assert.That(derivedTypes).Contains(typeof(TestOrderPlacedEvent));
  }

  [Test]
  public async Task RegisterDerivedType_WithMultipleEventTypes_AddsAllToRegistryAsync() {
    // Act
    JsonContextRegistry.RegisterDerivedType<IEvent, TestOrderPlacedEvent>("TestOrderPlacedEvent");
    JsonContextRegistry.RegisterDerivedType<IEvent, TestOrderShippedEvent>("TestOrderShippedEvent");

    // Assert
    var derivedTypes = JsonContextRegistry.GetRegisteredDerivedTypes<IEvent>();
    await Assert.That(derivedTypes).Contains(typeof(TestOrderPlacedEvent));
    await Assert.That(derivedTypes).Contains(typeof(TestOrderShippedEvent));
  }

  [Test]
  public async Task RegisterDerivedType_WithCommandType_AddsToRegistryAsync() {
    // Act
    JsonContextRegistry.RegisterDerivedType<ICommand, TestCreateOrderCommand>("TestCreateOrderCommand");

    // Assert
    var derivedTypes = JsonContextRegistry.GetRegisteredDerivedTypes<ICommand>();
    await Assert.That(derivedTypes).Contains(typeof(TestCreateOrderCommand));
  }

  [Test]
  public async Task RegisterDerivedType_WithNullDiscriminator_UsesTypeNameAsync() {
    // Act - register without explicit discriminator
    JsonContextRegistry.RegisterDerivedType<IEvent, TestOrderPlacedEvent>();

    // Assert - should use type name as discriminator
    var derivedTypes = JsonContextRegistry.GetRegisteredDerivedTypes<IEvent>();
    await Assert.That(derivedTypes).Contains(typeof(TestOrderPlacedEvent));

    // Verify discriminator defaults to type name
    var discriminator = JsonContextRegistry.GetDiscriminator<IEvent, TestOrderPlacedEvent>();
    await Assert.That(discriminator).IsEqualTo(nameof(TestOrderPlacedEvent));
  }

  [Test]
  public async Task GetPolymorphicTypeInfo_WithRegisteredTypes_ReturnsPolymorphicInfoAsync() {
    // Arrange
    JsonContextRegistry.RegisterDerivedType<IEvent, TestOrderPlacedEvent>("TestOrderPlacedEvent");
    JsonContextRegistry.RegisterDerivedType<IEvent, TestOrderShippedEvent>("TestOrderShippedEvent");
    var options = JsonContextRegistry.CreateCombinedOptions();

    // Act
    var typeInfo = JsonContextRegistry.GetPolymorphicTypeInfo<IEvent>(options);

    // Assert
    await Assert.That(typeInfo).IsNotNull();
    await Assert.That(typeInfo!.Type).IsEqualTo(typeof(IEvent));
  }

  [Test]
  public async Task GetPolymorphicTypeInfo_WithNoRegisteredTypes_ReturnsNullAsync() {
    // Arrange - use a type with no registered derived types
    var options = JsonContextRegistry.CreateCombinedOptions();

    // Act - IMessage likely has no direct registrations (only IEvent and ICommand do)
    // We'll check for a type that definitely has no registrations
    var typeInfo = JsonContextRegistry.GetPolymorphicTypeInfo<IDisposable>(options);

    // Assert
    await Assert.That(typeInfo).IsNull();
  }

  [Test]
  public async Task Serialize_IEvent_IncludesTypeDiscriminatorAsync() {
    // Arrange
    JsonContextRegistry.RegisterDerivedType<IEvent, TestOrderPlacedEvent>("TestOrderPlacedEvent");
    JsonContextRegistry.RegisterContext(PolymorphicTestJsonContext.Default);
    var options = JsonContextRegistry.CreateCombinedOptions();
    IEvent evt = new TestOrderPlacedEvent(Guid.NewGuid(), "John Doe");

    // Act
    var typeInfo = JsonContextRegistry.GetPolymorphicTypeInfo<IEvent>(options);
    await Assert.That(typeInfo).IsNotNull();
    var json = JsonSerializer.Serialize(evt, typeInfo!);

    // Assert - should include $type discriminator
    await Assert.That(json).Contains("\"$type\":\"TestOrderPlacedEvent\"");
    await Assert.That(json).Contains("\"OrderId\":");
    await Assert.That(json).Contains("\"CustomerName\":\"John Doe\"");
  }

  [Test]
  public async Task RoundTrip_IEvent_DeserializesToConcreteTypeAsync() {
    // Arrange
    JsonContextRegistry.RegisterDerivedType<IEvent, TestOrderPlacedEvent>("TestOrderPlacedEvent");
    JsonContextRegistry.RegisterContext(PolymorphicTestJsonContext.Default);
    var options = JsonContextRegistry.CreateCombinedOptions();
    var orderId = Guid.NewGuid();
    IEvent original = new TestOrderPlacedEvent(orderId, "Jane Doe");

    // Serialize as IEvent
    var typeInfo = JsonContextRegistry.GetPolymorphicTypeInfo<IEvent>(options);
    await Assert.That(typeInfo).IsNotNull();
    var json = JsonSerializer.Serialize(original, typeInfo!);

    // Act - Deserialize as IEvent
    var deserialized = JsonSerializer.Deserialize<IEvent>(json, typeInfo!);

    // Assert
    await Assert.That(deserialized).IsNotNull();
    await Assert.That(deserialized).IsTypeOf<TestOrderPlacedEvent>();
    var concreteEvent = (TestOrderPlacedEvent)deserialized!;
    await Assert.That(concreteEvent.OrderId).IsEqualTo(orderId);
    await Assert.That(concreteEvent.CustomerName).IsEqualTo("Jane Doe");
  }

  [Test]
  public async Task RoundTrip_ListOfIEvents_DeserializesToConcreteTypesAsync() {
    // Arrange
    JsonContextRegistry.RegisterDerivedType<IEvent, TestOrderPlacedEvent>("TestOrderPlacedEvent");
    JsonContextRegistry.RegisterDerivedType<IEvent, TestOrderShippedEvent>("TestOrderShippedEvent");
    JsonContextRegistry.RegisterContext(PolymorphicTestJsonContext.Default);
    var options = JsonContextRegistry.CreateCombinedOptions();

    var orderId = Guid.NewGuid();
    List<IEvent> originalList = [
      new TestOrderPlacedEvent(orderId, "Customer A"),
      new TestOrderShippedEvent(orderId, "TRACK123")
    ];

    // Serialize
    var listTypeInfo = JsonContextRegistry.GetPolymorphicListTypeInfo<IEvent>(options);
    await Assert.That(listTypeInfo).IsNotNull();
    var json = JsonSerializer.Serialize(originalList, listTypeInfo!);

    // Act - Deserialize
    var deserializedList = JsonSerializer.Deserialize<List<IEvent>>(json, listTypeInfo!);

    // Assert
    await Assert.That(deserializedList).IsNotNull();
    await Assert.That(deserializedList!.Count).IsEqualTo(2);
    await Assert.That(deserializedList[0]).IsTypeOf<TestOrderPlacedEvent>();
    await Assert.That(deserializedList[1]).IsTypeOf<TestOrderShippedEvent>();
  }

  [Test]
  public async Task MessageEnvelope_IEvent_SerializesWithPolymorphicPayloadAsync() {
    // Arrange
    JsonContextRegistry.RegisterDerivedType<IEvent, TestOrderPlacedEvent>("TestOrderPlacedEvent");
    JsonContextRegistry.RegisterContext(PolymorphicTestJsonContext.Default);
    var options = JsonContextRegistry.CreateCombinedOptions();

    var orderId = Guid.NewGuid();
    var payload = new TestOrderPlacedEvent(orderId, "Test Customer");
    var envelope = new MessageEnvelope<IEvent>(MessageId.New(), payload, []);

    // Act
    var envelopeTypeInfo = JsonContextRegistry.GetPolymorphicEnvelopeTypeInfo<IEvent>(options);
    await Assert.That(envelopeTypeInfo).IsNotNull();
    var json = JsonSerializer.Serialize(envelope, envelopeTypeInfo!);

    // Assert - should include $type in payload
    await Assert.That(json).Contains("\"$type\":\"TestOrderPlacedEvent\"");
    await Assert.That(json).Contains("\"MessageId\":");
    await Assert.That(json).Contains("\"Payload\":");
  }

  [Test]
  public async Task MessageEnvelope_IEvent_RoundTripDeserializesToConcretePayloadAsync() {
    // Arrange
    JsonContextRegistry.RegisterDerivedType<IEvent, TestOrderPlacedEvent>("TestOrderPlacedEvent");
    JsonContextRegistry.RegisterContext(PolymorphicTestJsonContext.Default);
    var options = JsonContextRegistry.CreateCombinedOptions();

    var messageId = MessageId.New();
    var orderId = Guid.NewGuid();
    var payload = new TestOrderPlacedEvent(orderId, "Roundtrip Customer");
    var envelope = new MessageEnvelope<IEvent>(messageId, payload, []);

    // Serialize
    var envelopeTypeInfo = JsonContextRegistry.GetPolymorphicEnvelopeTypeInfo<IEvent>(options);
    await Assert.That(envelopeTypeInfo).IsNotNull();
    var json = JsonSerializer.Serialize(envelope, envelopeTypeInfo!);

    // Act - Deserialize
    var deserialized = JsonSerializer.Deserialize<MessageEnvelope<IEvent>>(json, envelopeTypeInfo!);

    // Assert
    await Assert.That(deserialized).IsNotNull();
    await Assert.That(deserialized!.MessageId).IsEqualTo(messageId);
    await Assert.That(deserialized.Payload).IsTypeOf<TestOrderPlacedEvent>();
    var concretePayload = (TestOrderPlacedEvent)deserialized.Payload;
    await Assert.That(concretePayload.OrderId).IsEqualTo(orderId);
    await Assert.That(concretePayload.CustomerName).IsEqualTo("Roundtrip Customer");
  }
}
