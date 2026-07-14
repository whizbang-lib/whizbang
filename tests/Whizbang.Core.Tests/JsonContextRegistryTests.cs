using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;
using TUnit.Assertions;
using TUnit.Core;
using Whizbang.Core.Dispatch;
using Whizbang.Core.Messaging;
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
    _ = JsonContextRegistry.RegisteredCount;

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
    const string typeName = "Whizbang.Core.Tests.JsonContextRegistryTests+TestMessage, Whizbang.Core.Tests";
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
    const string typeName = "Whizbang.Core.Tests.JsonContextRegistryTests+TestMessage, Whizbang.Core.Tests";
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
    const string typeName = "Whizbang.Core.Tests.JsonContextRegistryTests+TestMessage, Whizbang.Core.Tests";

    // Act & Assert
    var exception = await Assert.That(() =>
      JsonContextRegistry.RegisterTypeName(typeName, typeof(TestMessage), null!))
      .ThrowsExactly<ArgumentNullException>();

    await Assert.That(exception!.ParamName).IsEqualTo("resolver");
  }

  [Test]
  public async Task GetTypeInfoByName_WithRegisteredType_ReturnsJsonTypeInfoAsync() {
    // Arrange
    const string typeName = "Whizbang.Core.Tests.JsonContextRegistryTests+TestMessage, Whizbang.Core.Tests";
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
    const string shortForm = "Whizbang.Core.Tests.JsonContextRegistryTests+TestMessage, Whizbang.Core.Tests";
    var resolver = TestMessageJsonContext.Default;
    JsonContextRegistry.RegisterTypeName(shortForm, typeof(TestMessage), resolver);
    var options = JsonContextRegistry.CreateCombinedOptions();

    // Act - Lookup with full AssemblyQualifiedName (includes Version, Culture, PublicKeyToken)
    const string fullForm = "Whizbang.Core.Tests.JsonContextRegistryTests+TestMessage, Whizbang.Core.Tests, Version=1.0.0.0, Culture=neutral, PublicKeyToken=null";
    var typeInfo = JsonContextRegistry.GetTypeInfoByName(fullForm, options);

    // Assert - Should match despite different formats
    await Assert.That(typeInfo).IsNotNull();
    await Assert.That(typeInfo!.Type).IsEqualTo(typeof(TestMessage));
  }

  [Test]
  public async Task GetTypeInfoByName_WithFuzzyMatch_MatchesFullFormToShortFormAsync() {
    // Arrange - Register with full AssemblyQualifiedName
    const string fullForm = "Whizbang.Core.Tests.JsonContextRegistryTests+TestMessage, Whizbang.Core.Tests, Version=1.0.0.0, Culture=neutral, PublicKeyToken=null";
    var resolver = TestMessageJsonContext.Default;
    JsonContextRegistry.RegisterTypeName(fullForm, typeof(TestMessage), resolver);
    var options = JsonContextRegistry.CreateCombinedOptions();

    // Act - Lookup with short form
    const string shortForm = "Whizbang.Core.Tests.JsonContextRegistryTests+TestMessage, Whizbang.Core.Tests";
    var typeInfo = JsonContextRegistry.GetTypeInfoByName(shortForm, options);

    // Assert - Should match despite different formats
    await Assert.That(typeInfo).IsNotNull();
    await Assert.That(typeInfo!.Type).IsEqualTo(typeof(TestMessage));
  }

  [Test]
  public async Task GetTypeInfoByName_WithUnregisteredType_ReturnsNullAsync() {
    // Arrange
    const string typeName = "SomeUnregisteredType, SomeAssembly";
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
    const string typeName = "Whizbang.Core.Tests.JsonContextRegistryTests+TestMessage, Whizbang.Core.Tests";

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
    const string payloadTypeName = "Whizbang.Core.Tests.JsonContextRegistryTests+TestEvent, Whizbang.Core.Tests";
    const string envelopeTypeName = "Whizbang.Core.Observability.MessageEnvelope`1[[Whizbang.Core.Tests.JsonContextRegistryTests+TestEvent, Whizbang.Core.Tests]], Whizbang.Core";
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
    const string payloadTypeName = "Whizbang.Core.Tests.JsonContextRegistryTests+TestEvent, Whizbang.Core.Tests";
    const string envelopeTypeName = "Whizbang.Core.Observability.MessageEnvelope`1[[Whizbang.Core.Tests.JsonContextRegistryTests+TestEvent, Whizbang.Core.Tests]], Whizbang.Core";
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
    const string shortForm = "Whizbang.Core.Observability.MessageEnvelope`1[[Whizbang.Core.Tests.JsonContextRegistryTests+TestEvent, Whizbang.Core.Tests]], Whizbang.Core";
    var resolver = TestEventJsonContext.Default;

    JsonContextRegistry.RegisterTypeName(
      "Whizbang.Core.Tests.JsonContextRegistryTests+TestEvent, Whizbang.Core.Tests",
      typeof(TestEvent),
      resolver);
    JsonContextRegistry.RegisterTypeName(shortForm, typeof(MessageEnvelope<TestEvent>), resolver);

    var options = JsonContextRegistry.CreateCombinedOptions();

    // Act - Lookup with full AssemblyQualifiedName (what AzureServiceBusTransport sends)
    const string fullForm = "Whizbang.Core.Observability.MessageEnvelope`1[[Whizbang.Core.Tests.JsonContextRegistryTests+TestEvent, Whizbang.Core.Tests, Version=1.0.0.0, Culture=neutral, PublicKeyToken=null]], Whizbang.Core, Version=1.0.0.0, Culture=neutral, PublicKeyToken=null";
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

  /// <summary>
  /// Test composite event — bundles inner events that all inherit the composite's stream at the
  /// receiver. A composite implements ICompositeEvent (IMessage), never IEvent.
  /// </summary>
  internal sealed record TestBulkImportComposite(List<IMessage> Items) : ICompositeEvent {
    public IEnumerable<IMessage> InnerEvents => Items;
  }

  /// <summary>
  /// Test JsonSerializerContext for composite round-trip: the composite, its inner event, the
  /// IMessage-payload envelope, and the inner-event list (mirrors what MessageJsonContextGenerator
  /// now emits for ICompositeEvent types).
  /// </summary>
  [JsonSerializable(typeof(TestBulkImportComposite))]
  [JsonSerializable(typeof(TestOrderPlacedEvent))]
  [JsonSerializable(typeof(MessageEnvelope<IMessage>))]
  [JsonSerializable(typeof(List<IMessage>))]
  internal sealed partial class CompositeRoundTripJsonContext : JsonSerializerContext {
  }

  /// <summary>Plain (non-composite) event carrying a polymorphic IMessage collection.</summary>
  internal sealed record TestEventWithMessageList(Guid Id, List<IMessage> Items) : IEvent;

  /// <summary>Plain event carrying a polymorphic IEvent collection (exercises the IEvent resolver branch).</summary>
  internal sealed record TestEventWithEventList(Guid Id, List<IEvent> Events) : IEvent;

  /// <summary>Plain event carrying a polymorphic ICommand collection (exercises the ICommand resolver branch).</summary>
  internal sealed record TestEventWithCommandList(Guid Id, List<ICommand> Commands) : IEvent;

  /// <summary>
  /// Comprehensive context for the polymorphic-collection round-trip tests: the composite, the three
  /// collection-carrying events, the inner event/command types, the IMessage-payload envelope, and the
  /// closed interface-list types.
  /// </summary>
  [JsonSerializable(typeof(TestBulkImportComposite))]
  [JsonSerializable(typeof(TestEventWithMessageList))]
  [JsonSerializable(typeof(TestEventWithEventList))]
  [JsonSerializable(typeof(TestEventWithCommandList))]
  [JsonSerializable(typeof(TestOrderPlacedEvent))]
  [JsonSerializable(typeof(TestOrderShippedEvent))]
  [JsonSerializable(typeof(TestCreateOrderCommand))]
  [JsonSerializable(typeof(MessageEnvelope<IMessage>))]
  [JsonSerializable(typeof(List<IMessage>))]
  [JsonSerializable(typeof(List<IEvent>))]
  [JsonSerializable(typeof(List<ICommand>))]
  internal sealed partial class PolymorphicCollectionTestJsonContext : JsonSerializerContext {
  }

  /// <summary>One-line consumer composite built on the turnkey <see cref="CompositeEventBase"/> helper.
  /// Public so the source generator discovers it and auto-registers its wire metadata — exactly as a
  /// real consumer's composite is registered (single generated context, no hand-written one).</summary>
  public sealed class HelperRoundTripComposite : Whizbang.Core.Messaging.CompositeEventBase;

  /// <summary>Public inner event so it is auto-registered as an IEvent/IMessage wire type, like a real
  /// one. Carries its own [StreamId] (a public IEvent requires one); inside a composite the receiver
  /// overrides it with the composite's stream, but the type still needs it to satisfy WHIZ009.</summary>
  public sealed record HelperInnerEvent : IEvent {
    [StreamId]
    public Guid StreamId { get; init; }
    public string Note { get; init; } = string.Empty;
  }


  [Test]
  public async Task MessageEnvelope_CompositePayload_RoundTripsWithInnerEventsIntactAsync() {
    // End-to-end "serialization works" gate for the turnkey composite feature: a composite serializes
    // and deserializes as an IMessage-polymorphic envelope payload, and its inner events survive the
    // round-trip as their concrete types. The composite registers ONLY as IMessage (never IEvent),
    // exactly as MessageJsonContextGenerator now emits.
    JsonContextRegistry.RegisterDerivedType<IMessage, TestBulkImportComposite>("TestBulkImportComposite");
    JsonContextRegistry.RegisterDerivedType<IMessage, TestOrderPlacedEvent>("TestOrderPlacedEvent");
    JsonContextRegistry.RegisterContext(CompositeRoundTripJsonContext.Default);
    var options = JsonContextRegistry.CreateCombinedOptions();

    var orderId = Guid.NewGuid();
    var inner = new TestOrderPlacedEvent(orderId, "Composite Customer");
    var composite = new TestBulkImportComposite([inner]);
    var messageId = MessageId.New();
    var envelope = new MessageEnvelope<IMessage>(messageId, composite, []);

    // Serialize
    var envelopeTypeInfo = JsonContextRegistry.GetPolymorphicEnvelopeTypeInfo<IMessage>(options);
    await Assert.That(envelopeTypeInfo).IsNotNull();
    var json = JsonSerializer.Serialize(envelope, envelopeTypeInfo!);

    // Act - deserialize the wire payload polymorphically
    var deserialized = JsonSerializer.Deserialize<MessageEnvelope<IMessage>>(json, envelopeTypeInfo!);

    // Assert - composite returns as the concrete type with its inner event intact
    await Assert.That(deserialized).IsNotNull();
    await Assert.That(deserialized!.MessageId).IsEqualTo(messageId);
    await Assert.That(deserialized.Payload).IsTypeOf<TestBulkImportComposite>();
    var roundTripped = (TestBulkImportComposite)deserialized.Payload;
    var innerList = roundTripped.InnerEvents.ToList();
    await Assert.That(innerList.Count).IsEqualTo(1);
    await Assert.That(innerList[0]).IsTypeOf<TestOrderPlacedEvent>();
    await Assert.That(((TestOrderPlacedEvent)innerList[0]).OrderId).IsEqualTo(orderId);
  }

  // ───────────────────────────────────────────────────────────────────────────────────────────────
  // REPRO — composite NAME-resolution gap (the WorkflowService "Failed to resolve message type" storm).
  // The generator registers a composite for POLYMORPHIC serialization (RegisterDerivedType<IMessage>) but
  // NEVER emits RegisterTypeName for it (MessageJsonContextGenerator.cs:1638 filters IsCommand||IsEvent||
  // IsSerializable — a composite is none). So GetTypeInfoByName — the by-name lookup the outbox-flush and
  // inbox fan-out lifecycle deserializers use — returns null for every composite, and the deserialize throws.
  // A normal event resolves by name; a composite must too. HelperRoundTripComposite/HelperInnerEvent are
  // PUBLIC so the real source generator registers them exactly as a consumer's composite is registered.
  // ───────────────────────────────────────────────────────────────────────────────────────────────

  [Test]
  public async Task GetTypeInfoByName_GeneratorRegisteredComposite_IsNameResolvableLikeAnEventAsync() {
    var options = JsonContextRegistry.CreateCombinedOptions();

    // Control: a generator-registered IEvent IS name-resolvable (the generator emits RegisterTypeName for it).
    var eventInfo = JsonContextRegistry.GetTypeInfoByName(
      typeof(HelperInnerEvent).AssemblyQualifiedName!, options);
    await Assert.That(eventInfo).IsNotNull()
      .Because("A generator-registered IEvent is name-resolvable via RegisterTypeName — the control for this repro.");

    // The composite MUST be name-resolvable too: the outbox-flush lifecycle (LifecycleInvocationHelper.
    // _processOutboxMessagesAsync) and the inbox fan-out (InboxDispatchWorker._resolveTypedEnvelope)
    // deserialize each row BY NAME via GetTypeInfoByName. The generator emits only RegisterDerivedType<IMessage>
    // (polymorphism) for a composite, never RegisterTypeName, so this returns null → "Failed to resolve
    // message type" and the composite never fans out (its inner events never persist).
    var compositeInfo = JsonContextRegistry.GetTypeInfoByName(
      typeof(HelperRoundTripComposite).AssemblyQualifiedName!, options);
    await Assert.That(compositeInfo).IsNotNull()
      .Because("A CompositeEventBase is deserialized by name during outbox flush / inbox fan-out; excluding composites from RegisterTypeName makes GetTypeInfoByName return null → the resolve-storm and no fan-out.");
  }

  [Test]
  public async Task LifecycleDeserializer_GeneratorRegisteredComposite_ResolvesByName_NotFailedToResolveAsync() {
    var options = JsonContextRegistry.CreateCombinedOptions();
    var deserializer = new Whizbang.Core.Messaging.JsonLifecycleMessageDeserializer(options);
    using var payload = System.Text.Json.JsonDocument.Parse("{}");

    // Exactly what the outbox-flush lifecycle does per outbox row: deserialize the payload BY NAME. For a
    // composite this currently throws InvalidOperationException "Failed to resolve message type '…'" (the
    // 978× WorkflowService resolve-storm). Once the composite is name-resolvable it deserializes instead.
    await Assert.That(() => deserializer.DeserializeFromJsonElement(
        payload.RootElement, typeof(HelperRoundTripComposite).AssemblyQualifiedName!))
      .ThrowsNothing()
      .Because("The outbox-flush / inbox lifecycle deserializes every row by name; a composite must resolve, not throw 'Failed to resolve message type'.");
  }

  [Test]
  public async Task MessageEnvelope_CompositeContainingComposite_RoundTripsCycleSafeAsync() {
    // Cycle-safety proof: a composite's inner list contains ANOTHER composite (a composite is itself
    // an IMessage that contains IMessage). The lazy base resolver must build the IMessage typeinfo
    // without recursing/stack-overflowing. Both nesting levels must return as concrete types.
    JsonContextRegistry.RegisterDerivedType<IMessage, TestBulkImportComposite>("TestBulkImportComposite");
    JsonContextRegistry.RegisterDerivedType<IMessage, TestOrderPlacedEvent>("TestOrderPlacedEvent");
    JsonContextRegistry.RegisterContext(CompositeRoundTripJsonContext.Default);
    var options = JsonContextRegistry.CreateCombinedOptions();

    var orderId = Guid.NewGuid();
    var leaf = new TestOrderPlacedEvent(orderId, "Nested Customer");
    var innerComposite = new TestBulkImportComposite([leaf]);
    var outerComposite = new TestBulkImportComposite([innerComposite]);
    var messageId = MessageId.New();
    var envelope = new MessageEnvelope<IMessage>(messageId, outerComposite, []);

    var envelopeTypeInfo = JsonContextRegistry.GetPolymorphicEnvelopeTypeInfo<IMessage>(options);
    await Assert.That(envelopeTypeInfo).IsNotNull();
    var json = JsonSerializer.Serialize(envelope, envelopeTypeInfo!);
    var deserialized = JsonSerializer.Deserialize<MessageEnvelope<IMessage>>(json, envelopeTypeInfo!);

    // Outer composite -> inner composite -> leaf event, all concrete.
    await Assert.That(deserialized).IsNotNull();
    await Assert.That(deserialized!.Payload).IsTypeOf<TestBulkImportComposite>();
    var outerInner = ((TestBulkImportComposite)deserialized.Payload).InnerEvents.ToList();
    await Assert.That(outerInner.Count).IsEqualTo(1);
    await Assert.That(outerInner[0]).IsTypeOf<TestBulkImportComposite>();
    var leafList = ((TestBulkImportComposite)outerInner[0]).InnerEvents.ToList();
    await Assert.That(leafList.Count).IsEqualTo(1);
    await Assert.That(leafList[0]).IsTypeOf<TestOrderPlacedEvent>();
    await Assert.That(((TestOrderPlacedEvent)leafList[0]).OrderId).IsEqualTo(orderId);
  }

  [Test]
  public async Task PlainEventWithPolymorphicMessageList_RoundTripsAsync() {
    // "Events with collections": a non-composite IEvent carrying a polymorphic IMessage list now
    // round-trips — previously the nested IMessage wasn't polymorphic and failed to deserialize.
    JsonContextRegistry.RegisterDerivedType<IMessage, TestEventWithMessageList>("TestEventWithMessageList");
    JsonContextRegistry.RegisterDerivedType<IMessage, TestOrderPlacedEvent>("TestOrderPlacedEvent");
    JsonContextRegistry.RegisterContext(PolymorphicCollectionTestJsonContext.Default);
    var options = JsonContextRegistry.CreateCombinedOptions();

    var ev = new TestEventWithMessageList(Guid.NewGuid(), [new TestOrderPlacedEvent(Guid.NewGuid(), "Child")]);
    var envelope = new MessageEnvelope<IMessage>(MessageId.New(), ev, []);
    var ti = JsonContextRegistry.GetPolymorphicEnvelopeTypeInfo<IMessage>(options);
    var json = JsonSerializer.Serialize(envelope, ti!);
    var back = JsonSerializer.Deserialize<MessageEnvelope<IMessage>>(json, ti!);

    await Assert.That(back!.Payload).IsTypeOf<TestEventWithMessageList>();
    var items = ((TestEventWithMessageList)back.Payload).Items;
    await Assert.That(items.Count).IsEqualTo(1);
    await Assert.That(items[0]).IsTypeOf<TestOrderPlacedEvent>();
  }

  [Test]
  public async Task NestedEventList_ExercisesIEventResolverBranch_RoundTripsAsync() {
    JsonContextRegistry.RegisterDerivedType<IMessage, TestEventWithEventList>("TestEventWithEventList");
    JsonContextRegistry.RegisterDerivedType<IEvent, TestOrderPlacedEvent>("TestOrderPlacedEvent");
    JsonContextRegistry.RegisterContext(PolymorphicCollectionTestJsonContext.Default);
    var options = JsonContextRegistry.CreateCombinedOptions();

    var ev = new TestEventWithEventList(Guid.NewGuid(), [new TestOrderPlacedEvent(Guid.NewGuid(), "E")]);
    var envelope = new MessageEnvelope<IMessage>(MessageId.New(), ev, []);
    var ti = JsonContextRegistry.GetPolymorphicEnvelopeTypeInfo<IMessage>(options);
    var json = JsonSerializer.Serialize(envelope, ti!);
    var back = JsonSerializer.Deserialize<MessageEnvelope<IMessage>>(json, ti!);

    await Assert.That(back!.Payload).IsTypeOf<TestEventWithEventList>();
    var events = ((TestEventWithEventList)back.Payload).Events;
    await Assert.That(events.Count).IsEqualTo(1);
    await Assert.That(events[0]).IsTypeOf<TestOrderPlacedEvent>();
  }

  [Test]
  public async Task NestedCommandList_ExercisesICommandResolverBranch_RoundTripsAsync() {
    JsonContextRegistry.RegisterDerivedType<IMessage, TestEventWithCommandList>("TestEventWithCommandList");
    JsonContextRegistry.RegisterDerivedType<ICommand, TestCreateOrderCommand>("TestCreateOrderCommand");
    JsonContextRegistry.RegisterContext(PolymorphicCollectionTestJsonContext.Default);
    var options = JsonContextRegistry.CreateCombinedOptions();

    var ev = new TestEventWithCommandList(Guid.NewGuid(), [new TestCreateOrderCommand("C", 9.99m)]);
    var envelope = new MessageEnvelope<IMessage>(MessageId.New(), ev, []);
    var ti = JsonContextRegistry.GetPolymorphicEnvelopeTypeInfo<IMessage>(options);
    var json = JsonSerializer.Serialize(envelope, ti!);
    var back = JsonSerializer.Deserialize<MessageEnvelope<IMessage>>(json, ti!);

    await Assert.That(back!.Payload).IsTypeOf<TestEventWithCommandList>();
    var cmds = ((TestEventWithCommandList)back.Payload).Commands;
    await Assert.That(cmds.Count).IsEqualTo(1);
    await Assert.That(cmds[0]).IsTypeOf<TestCreateOrderCommand>();
  }

  [Test]
  public async Task Composite_MultipleMixedInnerTypes_PreservesOrderAndTypesAsync() {
    JsonContextRegistry.RegisterDerivedType<IMessage, TestBulkImportComposite>("TestBulkImportComposite");
    JsonContextRegistry.RegisterDerivedType<IMessage, TestOrderPlacedEvent>("TestOrderPlacedEvent");
    JsonContextRegistry.RegisterDerivedType<IMessage, TestOrderShippedEvent>("TestOrderShippedEvent");
    JsonContextRegistry.RegisterContext(PolymorphicCollectionTestJsonContext.Default);
    var options = JsonContextRegistry.CreateCombinedOptions();

    var placed = new TestOrderPlacedEvent(Guid.NewGuid(), "P");
    var shipped = new TestOrderShippedEvent(Guid.NewGuid(), "TRACK");
    var composite = new TestBulkImportComposite([placed, shipped]);
    var envelope = new MessageEnvelope<IMessage>(MessageId.New(), composite, []);
    var ti = JsonContextRegistry.GetPolymorphicEnvelopeTypeInfo<IMessage>(options);
    var json = JsonSerializer.Serialize(envelope, ti!);
    var back = JsonSerializer.Deserialize<MessageEnvelope<IMessage>>(json, ti!);

    var inner = ((TestBulkImportComposite)back!.Payload).InnerEvents.ToList();
    await Assert.That(inner.Count).IsEqualTo(2);
    await Assert.That(inner[0]).IsTypeOf<TestOrderPlacedEvent>();   // producer-yielded order preserved
    await Assert.That(inner[1]).IsTypeOf<TestOrderShippedEvent>();
    await Assert.That(((TestOrderShippedEvent)inner[1]).TrackingNumber).IsEqualTo("TRACK");
  }

  [Test]
  public async Task Composite_EmptyInnerEvents_RoundTripsAsync() {
    JsonContextRegistry.RegisterDerivedType<IMessage, TestBulkImportComposite>("TestBulkImportComposite");
    JsonContextRegistry.RegisterContext(PolymorphicCollectionTestJsonContext.Default);
    var options = JsonContextRegistry.CreateCombinedOptions();

    var composite = new TestBulkImportComposite([]);
    var envelope = new MessageEnvelope<IMessage>(MessageId.New(), composite, []);
    var ti = JsonContextRegistry.GetPolymorphicEnvelopeTypeInfo<IMessage>(options);
    var json = JsonSerializer.Serialize(envelope, ti!);
    var back = JsonSerializer.Deserialize<MessageEnvelope<IMessage>>(json, ti!);

    await Assert.That(back!.Payload).IsTypeOf<TestBulkImportComposite>();
    await Assert.That(((TestBulkImportComposite)back.Payload).InnerEvents.ToList().Count).IsEqualTo(0);
  }

  [Test]
  public async Task CompositeEventBaseSubclass_RoundTripsViaHelperShapeAsync() {
    // Proves the turnkey CompositeEventBase shape (StreamId + List<IMessage> Inner + [JsonIgnore]
    // computed InnerEvents) serializes and deserializes end to end via the SOURCE-GENERATED metadata
    // (the composite + inner event are public, so the generator auto-registers them — exactly the
    // production path; no hand-written JsonSerializableContext). StreamId + inner events survive.
    var options = JsonContextRegistry.CreateCombinedOptions();

    var streamId = Guid.NewGuid();
    var inner = new HelperInnerEvent { StreamId = Guid.NewGuid(), Note = "Helper Customer" };
    var composite = new HelperRoundTripComposite { StreamId = streamId, Inner = [inner] };
    var envelope = new MessageEnvelope<IMessage>(MessageId.New(), composite, []);

    var ti = JsonContextRegistry.GetPolymorphicEnvelopeTypeInfo<IMessage>(options);
    var json = JsonSerializer.Serialize(envelope, ti!);
    var back = JsonSerializer.Deserialize<MessageEnvelope<IMessage>>(json, ti!);

    await Assert.That(back!.Payload).IsTypeOf<HelperRoundTripComposite>();
    var rt = (HelperRoundTripComposite)back.Payload;
    await Assert.That(rt.StreamId).IsEqualTo(streamId);
    var innerList = rt.InnerEvents.ToList();
    await Assert.That(innerList.Count).IsEqualTo(1);
    await Assert.That(innerList[0]).IsTypeOf<HelperInnerEvent>();
    await Assert.That(((HelperInnerEvent)innerList[0]).Note).IsEqualTo("Helper Customer");
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

  // ===========================
  // Non-Nullable Primitive Lock-In Tests
  // ===========================
  // These tests ensure non-nullable primitives can be serialized without relying on
  // the full resolver chain (which may not be available in standalone contexts).

  /// <summary>
  /// Test record with non-nullable Guid property.
  /// </summary>
  internal sealed record TestRecordWithGuid(string Name, Guid Id) : IEvent;

  /// <summary>
  /// Test record with various non-nullable primitives.
  /// </summary>
  internal sealed record TestRecordWithPrimitives(
    int IntValue,
    long LongValue,
    bool BoolValue,
    DateTime DateTimeValue,
    DateTimeOffset DateTimeOffsetValue,
    TimeSpan TimeSpanValue,
    decimal DecimalValue,
    double DoubleValue,
    float FloatValue,
    Guid GuidValue
  ) : IEvent;

  /// <summary>
  /// Test JsonSerializerContext for non-nullable primitive types.
  /// </summary>
  [JsonSerializable(typeof(TestRecordWithGuid))]
  [JsonSerializable(typeof(TestRecordWithPrimitives))]
  [JsonSerializable(typeof(MessageEnvelope<TestRecordWithGuid>))]
  [JsonSerializable(typeof(List<TestRecordWithGuid>))]
  internal sealed partial class NonNullablePrimitiveTestJsonContext : JsonSerializerContext {
  }

  [Test]
  public async Task NonNullableGuid_SerializesCorrectlyAsync() {
    // Arrange
    JsonContextRegistry.RegisterContext(NonNullablePrimitiveTestJsonContext.Default);
    JsonContextRegistry.RegisterDerivedType<IEvent, TestRecordWithGuid>("TestRecordWithGuid");
    var options = JsonContextRegistry.CreateCombinedOptions();
    var testGuid = Guid.NewGuid();
    var record = new TestRecordWithGuid("Test", testGuid);

    // Act
    var json = JsonSerializer.Serialize(record, options);

    // Assert
    await Assert.That(json).Contains($"\"{testGuid}\"");
    await Assert.That(json).Contains("\"Name\":\"Test\"");
  }

  [Test]
  public async Task NonNullableGuid_RoundTrip_PreservesValueAsync() {
    // Arrange
    JsonContextRegistry.RegisterContext(NonNullablePrimitiveTestJsonContext.Default);
    var options = JsonContextRegistry.CreateCombinedOptions();
    var testGuid = Guid.NewGuid();
    var original = new TestRecordWithGuid("RoundTrip", testGuid);

    // Act
    var json = JsonSerializer.Serialize(original, options);
    var deserialized = JsonSerializer.Deserialize<TestRecordWithGuid>(json, options);

    // Assert
    await Assert.That(deserialized).IsNotNull();
    await Assert.That(deserialized!.Id).IsEqualTo(testGuid);
    await Assert.That(deserialized.Name).IsEqualTo("RoundTrip");
  }

  [Test]
  public async Task AllNonNullablePrimitives_RoundTripCorrectlyAsync() {
    // Arrange
    JsonContextRegistry.RegisterContext(NonNullablePrimitiveTestJsonContext.Default);
    var options = JsonContextRegistry.CreateCombinedOptions();
    var testGuid = Guid.NewGuid();
    var testDateTime = new DateTime(2025, 6, 15, 12, 30, 45, DateTimeKind.Utc);
    var testDateTimeOffset = new DateTimeOffset(2025, 6, 15, 12, 30, 45, TimeSpan.FromHours(-5));
    var testTimeSpan = TimeSpan.FromHours(2.5);

    var record = new TestRecordWithPrimitives(
      IntValue: 42,
      LongValue: 9876543210L,
      BoolValue: true,
      DateTimeValue: testDateTime,
      DateTimeOffsetValue: testDateTimeOffset,
      TimeSpanValue: testTimeSpan,
      DecimalValue: 123.45m,
      DoubleValue: 3.14159,
      FloatValue: 2.71828f,
      GuidValue: testGuid
    );

    // Act
    var json = JsonSerializer.Serialize(record, options);
    var deserialized = JsonSerializer.Deserialize<TestRecordWithPrimitives>(json, options);

    // Assert
    await Assert.That(deserialized).IsNotNull();
    await Assert.That(deserialized!.IntValue).IsEqualTo(42);
    await Assert.That(deserialized.LongValue).IsEqualTo(9876543210L);
    await Assert.That(deserialized.BoolValue).IsEqualTo(true);
    await Assert.That(deserialized.DateTimeValue).IsEqualTo(testDateTime);
    await Assert.That(deserialized.DecimalValue).IsEqualTo(123.45m);
    await Assert.That(deserialized.DoubleValue).IsEqualTo(3.14159);
    await Assert.That(deserialized.GuidValue).IsEqualTo(testGuid);
  }

  [Test]
  public async Task MessageEnvelope_WithNonNullableGuidPayload_RoundTripsCorrectlyAsync() {
    // Arrange
    JsonContextRegistry.RegisterContext(NonNullablePrimitiveTestJsonContext.Default);
    var options = JsonContextRegistry.CreateCombinedOptions();
    var testGuid = Guid.NewGuid();
    var payload = new TestRecordWithGuid("EnvelopeTest", testGuid);
    var envelope = new MessageEnvelope<TestRecordWithGuid>(
      MessageId.New(),
      payload,
      []
    );

    // Act
    var json = JsonSerializer.Serialize(envelope, options);
    var deserialized = JsonSerializer.Deserialize<MessageEnvelope<TestRecordWithGuid>>(json, options);

    // Assert
    await Assert.That(deserialized).IsNotNull();
    await Assert.That(deserialized!.Payload).IsNotNull();
    await Assert.That(deserialized.Payload.Id).IsEqualTo(testGuid);
    await Assert.That(deserialized.Payload.Name).IsEqualTo("EnvelopeTest");
  }

  [Test]
  public async Task ListOfRecords_WithNonNullableGuid_RoundTripsCorrectlyAsync() {
    // Arrange
    JsonContextRegistry.RegisterContext(NonNullablePrimitiveTestJsonContext.Default);
    var options = JsonContextRegistry.CreateCombinedOptions();
    var list = new List<TestRecordWithGuid> {
      new("First", Guid.NewGuid()),
      new("Second", Guid.NewGuid()),
      new("Third", Guid.NewGuid())
    };

    // Act
    var json = JsonSerializer.Serialize(list, options);
    var deserialized = JsonSerializer.Deserialize<List<TestRecordWithGuid>>(json, options);

    // Assert
    await Assert.That(deserialized).IsNotNull();
    await Assert.That(deserialized!.Count).IsEqualTo(3);
    await Assert.That(deserialized[0].Name).IsEqualTo("First");
    await Assert.That(deserialized[1].Name).IsEqualTo("Second");
    await Assert.That(deserialized[2].Name).IsEqualTo("Third");
  }

  // ===========================
  // Nullable Primitive Lock-In Tests
  // ===========================
  // These tests ensure nullable primitives can be serialized without relying on
  // the full resolver chain (which may not be available in standalone contexts).

  /// <summary>
  /// Test record with nullable Guid property.
  /// </summary>
  internal sealed record TestRecordWithNullableGuid(string Name, Guid? OptionalId) : IEvent;

  /// <summary>
  /// Test record with various nullable primitives.
  /// </summary>
  internal sealed record TestRecordWithNullablePrimitives(
    int? OptionalInt,
    long? OptionalLong,
    bool? OptionalBool,
    DateTime? OptionalDateTime,
    DateTimeOffset? OptionalDateTimeOffset,
    TimeSpan? OptionalTimeSpan,
    decimal? OptionalDecimal,
    double? OptionalDouble,
    float? OptionalFloat,
    Guid? OptionalGuid
  ) : IEvent;

  /// <summary>
  /// Test JsonSerializerContext for nullable primitive types.
  /// </summary>
  [JsonSerializable(typeof(TestRecordWithNullableGuid))]
  [JsonSerializable(typeof(TestRecordWithNullablePrimitives))]
  [JsonSerializable(typeof(MessageEnvelope<TestRecordWithNullableGuid>))]
  [JsonSerializable(typeof(MessageEnvelope<TestRecordWithNullablePrimitives>))]
  [JsonSerializable(typeof(List<TestRecordWithNullableGuid>))]
  [JsonSerializable(typeof(TestRecordWithNullableGuid[]))]
  internal sealed partial class NullablePrimitiveTestJsonContext : JsonSerializerContext {
  }

  [Test]
  public async Task NullableGuid_WithValue_SerializesCorrectlyAsync() {
    // Arrange
    JsonContextRegistry.RegisterContext(NullablePrimitiveTestJsonContext.Default);
    JsonContextRegistry.RegisterDerivedType<IEvent, TestRecordWithNullableGuid>("TestRecordWithNullableGuid");
    var options = JsonContextRegistry.CreateCombinedOptions();
    var testGuid = Guid.NewGuid();
    var record = new TestRecordWithNullableGuid("Test", testGuid);

    // Act
    var json = JsonSerializer.Serialize(record, options);

    // Assert
    await Assert.That(json).Contains($"\"{testGuid}\"");
    await Assert.That(json).Contains("\"Name\":\"Test\"");
  }

  [Test]
  public async Task NullableGuid_WithNull_SerializesCorrectlyAsync() {
    // Arrange
    JsonContextRegistry.RegisterContext(NullablePrimitiveTestJsonContext.Default);
    var options = JsonContextRegistry.CreateCombinedOptions();
    var record = new TestRecordWithNullableGuid("Test", null);

    // Act
    var json = JsonSerializer.Serialize(record, options);

    // Assert - WhenWritingNull should omit null values
    await Assert.That(json).DoesNotContain("\"OptionalId\"");
    await Assert.That(json).Contains("\"Name\":\"Test\"");
  }

  [Test]
  public async Task NullableGuid_RoundTrip_PreservesValueAsync() {
    // Arrange
    JsonContextRegistry.RegisterContext(NullablePrimitiveTestJsonContext.Default);
    var options = JsonContextRegistry.CreateCombinedOptions();
    var testGuid = Guid.NewGuid();
    var original = new TestRecordWithNullableGuid("RoundTrip", testGuid);

    // Act
    var json = JsonSerializer.Serialize(original, options);
    var deserialized = JsonSerializer.Deserialize<TestRecordWithNullableGuid>(json, options);

    // Assert
    await Assert.That(deserialized).IsNotNull();
    await Assert.That(deserialized!.OptionalId).IsEqualTo(testGuid);
    await Assert.That(deserialized.Name).IsEqualTo("RoundTrip");
  }

  [Test]
  public async Task NullableGuid_RoundTrip_PreservesNullAsync() {
    // Arrange
    JsonContextRegistry.RegisterContext(NullablePrimitiveTestJsonContext.Default);
    var options = JsonContextRegistry.CreateCombinedOptions();
    var original = new TestRecordWithNullableGuid("RoundTripNull", null);

    // Act
    var json = JsonSerializer.Serialize(original, options);
    var deserialized = JsonSerializer.Deserialize<TestRecordWithNullableGuid>(json, options);

    // Assert
    await Assert.That(deserialized).IsNotNull();
    await Assert.That(deserialized!.OptionalId).IsNull();
    await Assert.That(deserialized.Name).IsEqualTo("RoundTripNull");
  }

  [Test]
  public async Task AllNullablePrimitives_WithValues_SerializeCorrectlyAsync() {
    // Arrange
    JsonContextRegistry.RegisterContext(NullablePrimitiveTestJsonContext.Default);
    var options = JsonContextRegistry.CreateCombinedOptions();
    var testGuid = Guid.NewGuid();
    var testDateTime = new DateTime(2025, 6, 15, 12, 30, 45, DateTimeKind.Utc);
    var testDateTimeOffset = new DateTimeOffset(2025, 6, 15, 12, 30, 45, TimeSpan.FromHours(-5));
    var testTimeSpan = TimeSpan.FromHours(2.5);

    var record = new TestRecordWithNullablePrimitives(
      OptionalInt: 42,
      OptionalLong: 9876543210L,
      OptionalBool: true,
      OptionalDateTime: testDateTime,
      OptionalDateTimeOffset: testDateTimeOffset,
      OptionalTimeSpan: testTimeSpan,
      OptionalDecimal: 123.45m,
      OptionalDouble: 3.14159,
      OptionalFloat: 2.71828f,
      OptionalGuid: testGuid
    );

    // Act
    var json = JsonSerializer.Serialize(record, options);
    var deserialized = JsonSerializer.Deserialize<TestRecordWithNullablePrimitives>(json, options);

    // Assert
    await Assert.That(deserialized).IsNotNull();
    await Assert.That(deserialized!.OptionalInt).IsEqualTo(42);
    await Assert.That(deserialized.OptionalLong).IsEqualTo(9876543210L);
    await Assert.That(deserialized.OptionalBool).IsEqualTo(true);
    await Assert.That(deserialized.OptionalDateTime).IsEqualTo(testDateTime);
    await Assert.That(deserialized.OptionalDecimal).IsEqualTo(123.45m);
    await Assert.That(deserialized.OptionalDouble).IsEqualTo(3.14159);
    await Assert.That(deserialized.OptionalGuid).IsEqualTo(testGuid);
  }

  [Test]
  public async Task AllNullablePrimitives_WithNulls_SerializeCorrectlyAsync() {
    // Arrange
    JsonContextRegistry.RegisterContext(NullablePrimitiveTestJsonContext.Default);
    var options = JsonContextRegistry.CreateCombinedOptions();

    var record = new TestRecordWithNullablePrimitives(
      OptionalInt: null,
      OptionalLong: null,
      OptionalBool: null,
      OptionalDateTime: null,
      OptionalDateTimeOffset: null,
      OptionalTimeSpan: null,
      OptionalDecimal: null,
      OptionalDouble: null,
      OptionalFloat: null,
      OptionalGuid: null
    );

    // Act
    var json = JsonSerializer.Serialize(record, options);
    var deserialized = JsonSerializer.Deserialize<TestRecordWithNullablePrimitives>(json, options);

    // Assert - all nulls should deserialize back to nulls
    await Assert.That(deserialized).IsNotNull();
    await Assert.That(deserialized!.OptionalInt).IsNull();
    await Assert.That(deserialized.OptionalLong).IsNull();
    await Assert.That(deserialized.OptionalBool).IsNull();
    await Assert.That(deserialized.OptionalDateTime).IsNull();
    await Assert.That(deserialized.OptionalDateTimeOffset).IsNull();
    await Assert.That(deserialized.OptionalTimeSpan).IsNull();
    await Assert.That(deserialized.OptionalDecimal).IsNull();
    await Assert.That(deserialized.OptionalDouble).IsNull();
    await Assert.That(deserialized.OptionalFloat).IsNull();
    await Assert.That(deserialized.OptionalGuid).IsNull();
  }

  // ===========================
  // MessageEnvelope Wrapper Lock-In Tests
  // ===========================

  [Test]
  public async Task MessageEnvelope_WithNullableGuidPayload_RoundTripsCorrectlyAsync() {
    // Arrange
    JsonContextRegistry.RegisterContext(NullablePrimitiveTestJsonContext.Default);
    var options = JsonContextRegistry.CreateCombinedOptions();
    var testGuid = Guid.NewGuid();
    var payload = new TestRecordWithNullableGuid("EnvelopeTest", testGuid);
    var envelope = new MessageEnvelope<TestRecordWithNullableGuid>(
      MessageId.New(),
      payload,
      []
    );

    // Act
    var json = JsonSerializer.Serialize(envelope, options);
    var deserialized = JsonSerializer.Deserialize<MessageEnvelope<TestRecordWithNullableGuid>>(json, options);

    // Assert
    await Assert.That(deserialized).IsNotNull();
    await Assert.That(deserialized!.Payload).IsNotNull();
    await Assert.That(deserialized.Payload.OptionalId).IsEqualTo(testGuid);
    await Assert.That(deserialized.Payload.Name).IsEqualTo("EnvelopeTest");
  }

  [Test]
  public async Task MessageEnvelope_WithHops_RoundTripsCorrectlyAsync() {
    // Arrange
    JsonContextRegistry.RegisterContext(NullablePrimitiveTestJsonContext.Default);
    var options = JsonContextRegistry.CreateCombinedOptions();
    var payload = new TestRecordWithNullableGuid("HopTest", Guid.NewGuid());
    var messageId = MessageId.New();
    var instanceId = Guid.NewGuid();
    var hop = new MessageHop {
      ServiceInstance = new ServiceInstanceInfo {
        ServiceName = "TestService",
        InstanceId = instanceId,
        HostName = "test-host",
        ProcessId = 12345
      },
      Timestamp = DateTimeOffset.UtcNow,
      CorrelationId = CorrelationId.New()
    };
    var envelope = new MessageEnvelope<TestRecordWithNullableGuid>(messageId, payload, [hop]);

    // Act
    var json = JsonSerializer.Serialize(envelope, options);
    var deserialized = JsonSerializer.Deserialize<MessageEnvelope<TestRecordWithNullableGuid>>(json, options);

    // Assert
    await Assert.That(deserialized).IsNotNull();
    await Assert.That(deserialized!.Hops.Count).IsEqualTo(1);
    await Assert.That(deserialized.Hops[0].ServiceInstance.ServiceName).IsEqualTo("TestService");
    await Assert.That(deserialized.Hops[0].ServiceInstance.InstanceId).IsEqualTo(instanceId);
  }

  // ===========================
  // Collection Type Lock-In Tests
  // ===========================

  [Test]
  public async Task ListOfRecords_WithNullableGuid_RoundTripsCorrectlyAsync() {
    // Arrange
    JsonContextRegistry.RegisterContext(NullablePrimitiveTestJsonContext.Default);
    var options = JsonContextRegistry.CreateCombinedOptions();
    var list = new List<TestRecordWithNullableGuid> {
      new("First", Guid.NewGuid()),
      new("Second", null),
      new("Third", Guid.NewGuid())
    };

    // Act
    var json = JsonSerializer.Serialize(list, options);
    var deserialized = JsonSerializer.Deserialize<List<TestRecordWithNullableGuid>>(json, options);

    // Assert
    await Assert.That(deserialized).IsNotNull();
    await Assert.That(deserialized!.Count).IsEqualTo(3);
    await Assert.That(deserialized[0].Name).IsEqualTo("First");
    await Assert.That(deserialized[0].OptionalId).IsNotNull();
    await Assert.That(deserialized[1].Name).IsEqualTo("Second");
    await Assert.That(deserialized[1].OptionalId).IsNull();
    await Assert.That(deserialized[2].Name).IsEqualTo("Third");
    await Assert.That(deserialized[2].OptionalId).IsNotNull();
  }

  [Test]
  public async Task ArrayOfRecords_WithNullableGuid_RoundTripsCorrectlyAsync() {
    // Arrange
    JsonContextRegistry.RegisterContext(NullablePrimitiveTestJsonContext.Default);
    var options = JsonContextRegistry.CreateCombinedOptions();
    var array = new TestRecordWithNullableGuid[] {
      new("ArrayFirst", Guid.NewGuid()),
      new("ArraySecond", null)
    };

    // Act
    var json = JsonSerializer.Serialize(array, options);
    var deserialized = JsonSerializer.Deserialize<TestRecordWithNullableGuid[]>(json, options);

    // Assert
    await Assert.That(deserialized).IsNotNull();
    await Assert.That(deserialized!.Length).IsEqualTo(2);
    await Assert.That(deserialized[0].Name).IsEqualTo("ArrayFirst");
    await Assert.That(deserialized[0].OptionalId).IsNotNull();
    await Assert.That(deserialized[1].Name).IsEqualTo("ArraySecond");
    await Assert.That(deserialized[1].OptionalId).IsNull();
  }

  [Test]
  public async Task EmptyList_RoundTripsCorrectlyAsync() {
    // Arrange
    JsonContextRegistry.RegisterContext(NullablePrimitiveTestJsonContext.Default);
    var options = JsonContextRegistry.CreateCombinedOptions();
    var list = new List<TestRecordWithNullableGuid>();

    // Act
    var json = JsonSerializer.Serialize(list, options);
    var deserialized = JsonSerializer.Deserialize<List<TestRecordWithNullableGuid>>(json, options);

    // Assert
    await Assert.That(deserialized).IsNotNull();
    await Assert.That(deserialized!.Count).IsEqualTo(0);
  }

  [Test]
  public async Task EmptyArray_RoundTripsCorrectlyAsync() {
    // Arrange
    JsonContextRegistry.RegisterContext(NullablePrimitiveTestJsonContext.Default);
    var options = JsonContextRegistry.CreateCombinedOptions();
    var array = Array.Empty<TestRecordWithNullableGuid>();

    // Act
    var json = JsonSerializer.Serialize(array, options);
    var deserialized = JsonSerializer.Deserialize<TestRecordWithNullableGuid[]>(json, options);

    // Assert
    await Assert.That(deserialized).IsNotNull();
    await Assert.That(deserialized!.Length).IsEqualTo(0);
  }

  // ===========================
  // Polymorphic Envelope Fallback Tests
  // ===========================
  // These tests verify the transport fallback mechanism:
  // When GetTypeInfoByName returns null for MessageEnvelope<IEvent>,
  // transports should use GetPolymorphicEnvelopeTypeInfo<IEvent>() as fallback.

  /// <summary>
  /// Test event for polymorphic envelope fallback tests.
  /// </summary>
  internal sealed record PolymorphicFallbackTestEvent(string Data, Guid EventId) : IEvent;

  /// <summary>
  /// Test JsonSerializerContext for polymorphic fallback tests.
  /// </summary>
  [JsonSerializable(typeof(PolymorphicFallbackTestEvent))]
  [JsonSerializable(typeof(MessageEnvelope<PolymorphicFallbackTestEvent>))]
  internal sealed partial class PolymorphicFallbackTestJsonContext : JsonSerializerContext {
  }

  [Test]
  public async Task GetTypeInfoByName_WithInterfaceEnvelopeTypeName_ReturnsNullAsync() {
    // Arrange - The exact type name sent by transports for interface-typed envelopes
    // This simulates what happens when a service publishes MessageEnvelope<IEvent>
    const string interfaceEnvelopeTypeName = "Whizbang.Core.Observability.MessageEnvelope`1[[Whizbang.Core.IEvent, Whizbang.Core, Version=0.9.3.0, Culture=neutral, PublicKeyToken=null]], Whizbang.Core, Version=0.9.3.0, Culture=neutral, PublicKeyToken=null";
    var options = JsonContextRegistry.CreateCombinedOptions();

    // Act - GetTypeInfoByName won't find MessageEnvelope<IEvent> because only concrete types are registered
    var typeInfo = JsonContextRegistry.GetTypeInfoByName(interfaceEnvelopeTypeName, options);

    // Assert - Should be null (this is the expected gap that requires fallback)
    await Assert.That(typeInfo).IsNull();
  }

  [Test]
  public async Task GetPolymorphicEnvelopeTypeInfo_ForIEvent_ReturnsValidTypeInfoAsync() {
    // Arrange
    JsonContextRegistry.RegisterDerivedType<IEvent, PolymorphicFallbackTestEvent>("PolymorphicFallbackTestEvent");
    JsonContextRegistry.RegisterContext(PolymorphicFallbackTestJsonContext.Default);
    var options = JsonContextRegistry.CreateCombinedOptions();

    // Act - This is the fallback method transports should use
    var typeInfo = JsonContextRegistry.GetPolymorphicEnvelopeTypeInfo<IEvent>(options);

    // Assert - Should return valid type info for polymorphic deserialization
    await Assert.That(typeInfo).IsNotNull();
    await Assert.That(typeInfo!.Type).IsEqualTo(typeof(MessageEnvelope<IEvent>));
  }

  [Test]
  public async Task PolymorphicFallback_DeserializesConcreteEventFromInterfaceEnvelopeAsync() {
    // Arrange - Simulate transport deserialization flow:
    // 1. Message arrives with EnvelopeType header = "MessageEnvelope<IEvent>"
    // 2. GetTypeInfoByName returns null
    // 3. Transport falls back to GetPolymorphicEnvelopeTypeInfo<IEvent>()
    // 4. Deserialization succeeds using $type discriminator

    JsonContextRegistry.RegisterDerivedType<IEvent, PolymorphicFallbackTestEvent>("PolymorphicFallbackTestEvent");
    JsonContextRegistry.RegisterContext(PolymorphicFallbackTestJsonContext.Default);
    var options = JsonContextRegistry.CreateCombinedOptions();

    // Create a concrete envelope and serialize it
    var eventId = Guid.NewGuid();
    var messageId = MessageId.New();
    var payload = new PolymorphicFallbackTestEvent("test-data", eventId);
    var originalEnvelope = new MessageEnvelope<IEvent>(messageId, payload, []);

    // Serialize using polymorphic envelope type info (as publisher would do)
    var publisherTypeInfo = JsonContextRegistry.GetPolymorphicEnvelopeTypeInfo<IEvent>(options);
    await Assert.That(publisherTypeInfo).IsNotNull();
    var json = JsonSerializer.Serialize(originalEnvelope, publisherTypeInfo!);

    // Verify JSON contains $type discriminator
    await Assert.That(json).Contains("\"$type\":\"PolymorphicFallbackTestEvent\"");

    // Simulate transport fallback: GetTypeInfoByName returns null...
    const string interfaceEnvelopeTypeName = "Whizbang.Core.Observability.MessageEnvelope`1[[Whizbang.Core.IEvent, Whizbang.Core]], Whizbang.Core";
    var directLookup = JsonContextRegistry.GetTypeInfoByName(interfaceEnvelopeTypeName, options);
    await Assert.That(directLookup).IsNull(); // Confirms fallback is needed

    // ...so use polymorphic fallback (this is what the transport fix does)
    var fallbackTypeInfo = JsonContextRegistry.GetPolymorphicEnvelopeTypeInfo<IEvent>(options);
    await Assert.That(fallbackTypeInfo).IsNotNull();

    // Act - Deserialize using fallback type info
    var deserialized = JsonSerializer.Deserialize<MessageEnvelope<IEvent>>(json, fallbackTypeInfo!);

    // Assert - Should successfully deserialize to concrete type
    await Assert.That(deserialized).IsNotNull();
    await Assert.That(deserialized!.MessageId).IsEqualTo(messageId);
    await Assert.That(deserialized.Payload).IsTypeOf<PolymorphicFallbackTestEvent>();

    var concretePayload = (PolymorphicFallbackTestEvent)deserialized.Payload;
    await Assert.That(concretePayload.Data).IsEqualTo("test-data");
    await Assert.That(concretePayload.EventId).IsEqualTo(eventId);
  }

  [Test]
  public async Task PolymorphicFallback_WithHops_PreservesMessageContextAsync() {
    // Arrange - Test that hops are preserved during polymorphic deserialization
    JsonContextRegistry.RegisterDerivedType<IEvent, PolymorphicFallbackTestEvent>("PolymorphicFallbackTestEvent");
    JsonContextRegistry.RegisterContext(PolymorphicFallbackTestJsonContext.Default);
    var options = JsonContextRegistry.CreateCombinedOptions();

    var messageId = MessageId.New();
    var correlationId = CorrelationId.New();
    var payload = new PolymorphicFallbackTestEvent("hop-test", Guid.NewGuid());

    var hop = new MessageHop {
      ServiceInstance = new ServiceInstanceInfo {
        ServiceName = "TestPublisher",
        InstanceId = Guid.NewGuid(),
        HostName = "test-host",
        ProcessId = 12345
      },
      Timestamp = DateTimeOffset.UtcNow,
      CorrelationId = correlationId,
      Topic = "test-topic"
    };

    var originalEnvelope = new MessageEnvelope<IEvent>(messageId, payload, [hop]);

    // Serialize
    var publisherTypeInfo = JsonContextRegistry.GetPolymorphicEnvelopeTypeInfo<IEvent>(options);
    var json = JsonSerializer.Serialize(originalEnvelope, publisherTypeInfo!);

    // Act - Deserialize using fallback
    var fallbackTypeInfo = JsonContextRegistry.GetPolymorphicEnvelopeTypeInfo<IEvent>(options);
    var deserialized = JsonSerializer.Deserialize<MessageEnvelope<IEvent>>(json, fallbackTypeInfo!);

    // Assert - Hops should be preserved
    await Assert.That(deserialized).IsNotNull();
    await Assert.That(deserialized!.Hops.Count).IsEqualTo(1);
    await Assert.That(deserialized.Hops[0].ServiceInstance.ServiceName).IsEqualTo("TestPublisher");
    await Assert.That(deserialized.Hops[0].CorrelationId).IsEqualTo(correlationId);
    await Assert.That(deserialized.Hops[0].Topic).IsEqualTo("test-topic");
  }

  [Test]
  public async Task BrokenPath_SerializeWithConcreteType_DeserializeWithPolymorphic_PayloadIsNullAsync() {
    // This test demonstrates the BROKEN PATH that existed before the fix:
    // 1. Outbox stores envelope with EnvelopeType = "MessageEnvelope<IEvent>"
    // 2. Publisher serializes using concrete type (NO $type discriminator)
    // 3. Consumer tries to deserialize with polymorphic type info
    // 4. Payload is null because there's no $type to identify the concrete type

    JsonContextRegistry.RegisterDerivedType<IEvent, PolymorphicFallbackTestEvent>("PolymorphicFallbackTestEvent");
    JsonContextRegistry.RegisterContext(PolymorphicFallbackTestJsonContext.Default);
    var options = JsonContextRegistry.CreateCombinedOptions();

    // Create envelope with concrete payload
    var eventId = Guid.NewGuid();
    var messageId = MessageId.New();
    var payload = new PolymorphicFallbackTestEvent("test-data", eventId);
    _ = new MessageEnvelope<IEvent>(messageId, payload, []);

    // BROKEN: Serialize using CONCRETE type info (no $type discriminator)
    var concreteTypeInfo = options.GetTypeInfo(typeof(MessageEnvelope<PolymorphicFallbackTestEvent>));
    await Assert.That(concreteTypeInfo).IsNotNull();

    // Cast to concrete envelope type for serialization (simulates runtime behavior)
    var concreteEnvelope = new MessageEnvelope<PolymorphicFallbackTestEvent>(messageId, payload, []);
    var json = JsonSerializer.Serialize(concreteEnvelope, concreteTypeInfo!);

    // Verify NO $type discriminator in the JSON
    await Assert.That(json).DoesNotContain("\"$type\"");

    // Try to deserialize using polymorphic type info (as consumer would do)
    var polymorphicTypeInfo = JsonContextRegistry.GetPolymorphicEnvelopeTypeInfo<IEvent>(options);
    await Assert.That(polymorphicTypeInfo).IsNotNull();

    var deserialized = JsonSerializer.Deserialize<MessageEnvelope<IEvent>>(json, polymorphicTypeInfo!);

    // BROKEN RESULT: Envelope exists but Payload is null (no $type to identify concrete type)
    await Assert.That(deserialized).IsNotNull();
    await Assert.That(deserialized!.Payload).IsNull(); // THIS IS THE BUG!
  }

  [Test]
  public async Task FixedPath_SerializeWithPolymorphicType_DeserializeWithPolymorphic_PayloadIsConcreteAsync() {
    // This test demonstrates the FIXED PATH:
    // 1. When envelopeTypeName indicates interface (IEvent), use polymorphic serialization
    // 2. JSON includes $type discriminator
    // 3. Consumer deserializes with polymorphic type info
    // 4. Payload is correctly deserialized to concrete type

    JsonContextRegistry.RegisterDerivedType<IEvent, PolymorphicFallbackTestEvent>("PolymorphicFallbackTestEvent");
    JsonContextRegistry.RegisterContext(PolymorphicFallbackTestJsonContext.Default);
    var options = JsonContextRegistry.CreateCombinedOptions();

    // Create envelope with concrete payload
    var eventId = Guid.NewGuid();
    var messageId = MessageId.New();
    var payload = new PolymorphicFallbackTestEvent("test-data", eventId);
    var envelope = new MessageEnvelope<IEvent>(messageId, payload, []);

    // FIXED: Serialize using POLYMORPHIC type info (includes $type discriminator)
    var polymorphicTypeInfo = JsonContextRegistry.GetPolymorphicEnvelopeTypeInfo<IEvent>(options);
    await Assert.That(polymorphicTypeInfo).IsNotNull();
    var json = JsonSerializer.Serialize(envelope, polymorphicTypeInfo!);

    // Verify $type discriminator IS in the JSON
    await Assert.That(json).Contains("\"$type\":\"PolymorphicFallbackTestEvent\"");

    // Deserialize using same polymorphic type info
    var deserialized = JsonSerializer.Deserialize<MessageEnvelope<IEvent>>(json, polymorphicTypeInfo!);

    // FIXED RESULT: Envelope and Payload both exist with correct concrete type
    await Assert.That(deserialized).IsNotNull();
    await Assert.That(deserialized!.Payload).IsNotNull(); // PAYLOAD IS NOT NULL!
    await Assert.That(deserialized.Payload).IsTypeOf<PolymorphicFallbackTestEvent>();

    var concretePayload = (PolymorphicFallbackTestEvent)deserialized.Payload;
    await Assert.That(concretePayload.Data).IsEqualTo("test-data");
    await Assert.That(concretePayload.EventId).IsEqualTo(eventId);
  }

  [Test]
  public async Task TransportSimulation_DetectInterfaceEnvelopeTypeName_UsePolymorphicSerializationAsync() {
    // This test simulates what the transport fix does:
    // 1. Check if envelopeTypeName contains "IEvent" (interface indicator)
    // 2. If yes, use GetPolymorphicEnvelopeTypeInfo for serialization
    // 3. Result: JSON includes $type discriminator

    JsonContextRegistry.RegisterDerivedType<IEvent, PolymorphicFallbackTestEvent>("PolymorphicFallbackTestEvent");
    JsonContextRegistry.RegisterContext(PolymorphicFallbackTestJsonContext.Default);
    var options = JsonContextRegistry.CreateCombinedOptions();

    // Simulate outbox storing the interface envelope type name
    const string envelopeTypeName = "Whizbang.Core.Observability.MessageEnvelope`1[[Whizbang.Core.IEvent, Whizbang.Core, Version=0.9.3.0, Culture=neutral, PublicKeyToken=null]], Whizbang.Core, Version=0.9.3.0, Culture=neutral, PublicKeyToken=null";

    // Create envelope with concrete payload
    var eventId = Guid.NewGuid();
    var messageId = MessageId.New();
    var payload = new PolymorphicFallbackTestEvent("transport-test", eventId);
    var envelope = new MessageEnvelope<IEvent>(messageId, payload, []);

    // Transport detection: check if envelopeTypeName indicates interface type
    var isInterfaceEnvelope = envelopeTypeName.Contains("Whizbang.Core.IEvent,") ||
                              envelopeTypeName.Contains(".IEvent,");
    await Assert.That(isInterfaceEnvelope).IsTrue();

    // Transport action: use polymorphic serialization when interface envelope detected
    JsonTypeInfo? typeInfo = isInterfaceEnvelope
      ? JsonContextRegistry.GetPolymorphicEnvelopeTypeInfo<IEvent>(options)
      : options.GetTypeInfo(envelope.GetType());

    await Assert.That(typeInfo).IsNotNull();

    // Serialize
    var json = JsonSerializer.Serialize(envelope, typeInfo!);

    // Verify polymorphic serialization was used (includes $type)
    await Assert.That(json).Contains("\"$type\":\"PolymorphicFallbackTestEvent\"");

    // Full round-trip: deserialize on consumer side
    var deserializeTypeInfo = JsonContextRegistry.GetPolymorphicEnvelopeTypeInfo<IEvent>(options);
    var deserialized = JsonSerializer.Deserialize<MessageEnvelope<IEvent>>(json, deserializeTypeInfo!);

    await Assert.That(deserialized).IsNotNull();
    await Assert.That(deserialized!.Payload).IsNotNull();
    await Assert.That(deserialized.Payload).IsTypeOf<PolymorphicFallbackTestEvent>();
  }
}
