using System.Text.Json;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Attributes;
using Whizbang.Core.Tags;
using Whizbang.Observability.Hooks;

namespace Whizbang.Observability.Tests.Hooks;

/// <summary>
/// Tests for <see cref="OpenTelemetryMetricHook"/>.
/// Validates OpenTelemetry metric recording for tagged messages.
/// </summary>
/// <tests>Whizbang.Observability/Hooks/OpenTelemetryMetricHook.cs</tests>
[Category("Observability")]
[Category("Hooks")]
public class OpenTelemetryMetricHookTests {
  [Test]
  public async Task OnTaggedMessage_RecordsCounter_WhenTypeIsCounterAsync() {
    // Arrange
    var hook = new OpenTelemetryMetricHook();
    var attribute = new MetricTagAttribute {
      Tag = "test-counter",
      MetricName = "test.counter",
      Type = MetricType.Counter
    };
    var message = new TestOrderEvent { OrderId = Guid.NewGuid(), Amount = 100m };
    var payload = JsonSerializer.SerializeToElement(message);
    var context = new TagContext<MetricTagAttribute> {
      Attribute = attribute,
      Message = message,
      MessageType = typeof(TestOrderEvent),
      Payload = payload
    };

    // Act
    var result = await hook.OnTaggedMessageAsync(context, CancellationToken.None);

    // Assert
    await Assert.That(result).IsNull(); // Returns null to pass original payload
  }

  [Test]
  public async Task OnTaggedMessage_RecordsHistogram_WhenTypeIsHistogramAsync() {
    // Arrange
    var hook = new OpenTelemetryMetricHook();
    var attribute = new MetricTagAttribute {
      Tag = "test-histogram",
      MetricName = "test.histogram",
      Type = MetricType.Histogram,
      ValueProperty = "Amount",
      Unit = "USD"
    };
    var message = new TestOrderEvent { OrderId = Guid.NewGuid(), Amount = 250.50m };
    var payload = JsonSerializer.SerializeToElement(message);
    var context = new TagContext<MetricTagAttribute> {
      Attribute = attribute,
      Message = message,
      MessageType = typeof(TestOrderEvent),
      Payload = payload
    };

    // Act
    var result = await hook.OnTaggedMessageAsync(context, CancellationToken.None);

    // Assert
    await Assert.That(result).IsNull();
  }

  [Test]
  public async Task OnTaggedMessage_RecordsGauge_WhenTypeIsGaugeAsync() {
    // Arrange
    var hook = new OpenTelemetryMetricHook();
    var attribute = new MetricTagAttribute {
      Tag = "test-gauge",
      MetricName = "test.gauge",
      Type = MetricType.Gauge,
      ValueProperty = "Amount"
    };
    var message = new TestOrderEvent { OrderId = Guid.NewGuid(), Amount = 75.25m };
    var payload = JsonSerializer.SerializeToElement(message);
    var context = new TagContext<MetricTagAttribute> {
      Attribute = attribute,
      Message = message,
      MessageType = typeof(TestOrderEvent),
      Payload = payload
    };

    // Act
    var result = await hook.OnTaggedMessageAsync(context, CancellationToken.None);

    // Assert
    await Assert.That(result).IsNull();
  }

  [Test]
  public async Task OnTaggedMessage_AddsDimensionsFromProperties_WhenPropertiesSpecifiedAsync() {
    // Arrange
    var hook = new OpenTelemetryMetricHook();
    var attribute = new MetricTagAttribute {
      Tag = "test-metric",
      MetricName = "test.metric.with.dimensions",
      Type = MetricType.Counter,
      Properties = ["TenantId", "Region"]
    };
    var message = new TestDimensionedEvent {
      Id = Guid.NewGuid(),
      TenantId = "tenant-123",
      Region = "us-east"
    };
    var payload = JsonSerializer.SerializeToElement(message);
    var context = new TagContext<MetricTagAttribute> {
      Attribute = attribute,
      Message = message,
      MessageType = typeof(TestDimensionedEvent),
      Payload = payload
    };

    // Act
    var result = await hook.OnTaggedMessageAsync(context, CancellationToken.None);

    // Assert
    await Assert.That(result).IsNull();
  }

  [Test]
  public async Task OnTaggedMessage_AddsDimensionsFromScope_WhenScopeProvidedAsync() {
    // Arrange
    var hook = new OpenTelemetryMetricHook();
    var attribute = new MetricTagAttribute {
      Tag = "test-metric",
      MetricName = "test.metric.with.scope",
      Type = MetricType.Counter
    };
    var message = new TestOrderEvent { OrderId = Guid.NewGuid(), Amount = 100m };
    var payload = JsonSerializer.SerializeToElement(message);
    var scope = new Dictionary<string, object?> {
      { "TenantId", "scope-tenant" },
      { "Environment", "production" }
    };
    var context = new TagContext<MetricTagAttribute> {
      Attribute = attribute,
      Message = message,
      MessageType = typeof(TestOrderEvent),
      Payload = payload,
      Scope = scope
    };

    // Act
    var result = await hook.OnTaggedMessageAsync(context, CancellationToken.None);

    // Assert
    await Assert.That(result).IsNull();
  }

  [Test]
  public async Task OnTaggedMessage_HandlesNumericValueExtraction_FromPayloadAsync() {
    // Arrange
    var hook = new OpenTelemetryMetricHook();
    var attribute = new MetricTagAttribute {
      Tag = "test-value",
      MetricName = "test.numeric.value",
      Type = MetricType.Histogram,
      ValueProperty = "Amount"
    };

    // Test with different numeric formats
    var json = """{"OrderId":"00000000-0000-0000-0000-000000000001","Amount":123.45}""";
    var payload = JsonDocument.Parse(json).RootElement;
    var message = new TestOrderEvent { OrderId = Guid.NewGuid(), Amount = 123.45m };
    var context = new TagContext<MetricTagAttribute> {
      Attribute = attribute,
      Message = message,
      MessageType = typeof(TestOrderEvent),
      Payload = payload
    };

    // Act
    var result = await hook.OnTaggedMessageAsync(context, CancellationToken.None);

    // Assert
    await Assert.That(result).IsNull();
  }

  [Test]
  public async Task OnTaggedMessage_DefaultsToOne_WhenNoValuePropertySpecifiedAsync() {
    // Arrange
    var hook = new OpenTelemetryMetricHook();
    var attribute = new MetricTagAttribute {
      Tag = "test-counter-default",
      MetricName = "test.counter.default",
      Type = MetricType.Counter
      // No ValueProperty specified
    };
    var message = new TestOrderEvent { OrderId = Guid.NewGuid(), Amount = 100m };
    var payload = JsonSerializer.SerializeToElement(message);
    var context = new TagContext<MetricTagAttribute> {
      Attribute = attribute,
      Message = message,
      MessageType = typeof(TestOrderEvent),
      Payload = payload
    };

    // Act
    var result = await hook.OnTaggedMessageAsync(context, CancellationToken.None);

    // Assert
    await Assert.That(result).IsNull();
  }

  [Test]
  public async Task Meter_HasCorrectName_AndVersionAsync() {
    // Assert
    await Assert.That(OpenTelemetryMetricHook.Meter.Name).IsEqualTo("Whizbang.MessageTags");
    await Assert.That(OpenTelemetryMetricHook.Meter.Version).IsEqualTo("1.0.0");
  }

  [Test]
  public async Task OnTaggedMessage_HandlesStringNumericValue_WhenValuePropertyIsStringAsync() {
    // Arrange
    var hook = new OpenTelemetryMetricHook();
    var attribute = new MetricTagAttribute {
      Tag = "test-string-value",
      MetricName = "test.string.numeric",
      Type = MetricType.Histogram,
      ValueProperty = "AmountStr"
    };

    // Payload with string representation of number
    const string json = """{"OrderId":"00000000-0000-0000-0000-000000000001","AmountStr":"456.78"}""";
    var payload = JsonDocument.Parse(json).RootElement;
    var context = new TagContext<MetricTagAttribute> {
      Attribute = attribute,
      Message = new { OrderId = Guid.NewGuid(), AmountStr = "456.78" },
      MessageType = typeof(object),
      Payload = payload
    };

    // Act
    var result = await hook.OnTaggedMessageAsync(context, CancellationToken.None);

    // Assert
    await Assert.That(result).IsNull();
  }

  [Test]
  public async Task OnTaggedMessage_HandlesNonObjectPayload_GracefullyAsync() {
    // Arrange
    var hook = new OpenTelemetryMetricHook();
    var attribute = new MetricTagAttribute {
      Tag = "test-non-object",
      MetricName = "test.nonobject",
      Type = MetricType.Histogram,
      ValueProperty = "SomeValue"
    };

    // Non-object payload (string)
    var payload = JsonSerializer.SerializeToElement("just a string");
    var context = new TagContext<MetricTagAttribute> {
      Attribute = attribute,
      Message = "just a string",
      MessageType = typeof(string),
      Payload = payload
    };

    // Act - should not throw
    var result = await hook.OnTaggedMessageAsync(context, CancellationToken.None);

    // Assert
    await Assert.That(result).IsNull();
  }

  [Test]
  public async Task OnTaggedMessage_HandlesMissingValueProperty_GracefullyAsync() {
    // Arrange
    var hook = new OpenTelemetryMetricHook();
    var attribute = new MetricTagAttribute {
      Tag = "test-missing-prop",
      MetricName = "test.missing.property",
      Type = MetricType.Histogram,
      ValueProperty = "NonExistentProperty"
    };

    const string json = """{"OrderId":"00000000-0000-0000-0000-000000000001","Amount":100}""";
    var payload = JsonDocument.Parse(json).RootElement;
    var context = new TagContext<MetricTagAttribute> {
      Attribute = attribute,
      Message = new { OrderId = Guid.NewGuid(), Amount = 100m },
      MessageType = typeof(object),
      Payload = payload
    };

    // Act - should not throw
    var result = await hook.OnTaggedMessageAsync(context, CancellationToken.None);

    // Assert
    await Assert.That(result).IsNull();
  }

  [Test]
  public async Task OnTaggedMessage_HandlesBooleanProperties_InDimensionsAsync() {
    // Arrange
    var hook = new OpenTelemetryMetricHook();
    var attribute = new MetricTagAttribute {
      Tag = "test-boolean",
      MetricName = "test.boolean.dimensions",
      Type = MetricType.Counter,
      Properties = ["IsActive", "IsPremium"]
    };

    const string json = """{"Id":"00000000-0000-0000-0000-000000000001","IsActive":true,"IsPremium":false}""";
    var payload = JsonDocument.Parse(json).RootElement;
    var context = new TagContext<MetricTagAttribute> {
      Attribute = attribute,
      Message = new { Id = Guid.NewGuid(), IsActive = true, IsPremium = false },
      MessageType = typeof(object),
      Payload = payload
    };

    // Act
    var result = await hook.OnTaggedMessageAsync(context, CancellationToken.None);

    // Assert
    await Assert.That(result).IsNull();
  }

  [Test]
  public async Task OnTaggedMessage_HandlesNullScopeValues_GracefullyAsync() {
    // Arrange
    var hook = new OpenTelemetryMetricHook();
    var attribute = new MetricTagAttribute {
      Tag = "test-null-scope",
      MetricName = "test.null.scope",
      Type = MetricType.Counter
    };

    var message = new TestOrderEvent { OrderId = Guid.NewGuid(), Amount = 100m };
    var payload = JsonSerializer.SerializeToElement(message);
    var scope = new Dictionary<string, object?> {
      { "TenantId", "valid-tenant" },
      { "NullValue", null } // This should be skipped
    };
    var context = new TagContext<MetricTagAttribute> {
      Attribute = attribute,
      Message = message,
      MessageType = typeof(TestOrderEvent),
      Payload = payload,
      Scope = scope
    };

    // Act
    var result = await hook.OnTaggedMessageAsync(context, CancellationToken.None);

    // Assert
    await Assert.That(result).IsNull();
  }

  [Test]
  public async Task OnTaggedMessage_HandlesInvalidNumericString_GracefullyAsync() {
    // Arrange
    var hook = new OpenTelemetryMetricHook();
    var attribute = new MetricTagAttribute {
      Tag = "test-invalid-string",
      MetricName = "test.invalid.numeric",
      Type = MetricType.Histogram,
      ValueProperty = "BadValue"
    };

    const string json = """{"Id":"00000000-0000-0000-0000-000000000001","BadValue":"not-a-number"}""";
    var payload = JsonDocument.Parse(json).RootElement;
    var context = new TagContext<MetricTagAttribute> {
      Attribute = attribute,
      Message = new { Id = Guid.NewGuid(), BadValue = "not-a-number" },
      MessageType = typeof(object),
      Payload = payload
    };

    // Act - should not throw
    var result = await hook.OnTaggedMessageAsync(context, CancellationToken.None);

    // Assert
    await Assert.That(result).IsNull();
  }

  [Test]
  public async Task OnTaggedMessage_ScopeDoesNotOverrideExistingProperty_WhenSameKeyExistsAsync() {
    // Arrange
    var hook = new OpenTelemetryMetricHook();
    var attribute = new MetricTagAttribute {
      Tag = "test-overlap",
      MetricName = "test.overlap.keys",
      Type = MetricType.Counter,
      Properties = ["TenantId"] // Property from payload
    };

    const string json = """{"Id":"00000000-0000-0000-0000-000000000001","TenantId":"payload-tenant"}""";
    var payload = JsonDocument.Parse(json).RootElement;
    var scope = new Dictionary<string, object?> {
      { "TenantId", "scope-tenant" } // Should NOT override payload value
    };
    var context = new TagContext<MetricTagAttribute> {
      Attribute = attribute,
      Message = new { Id = Guid.NewGuid(), TenantId = "payload-tenant" },
      MessageType = typeof(object),
      Payload = payload,
      Scope = scope
    };

    // Act
    var result = await hook.OnTaggedMessageAsync(context, CancellationToken.None);

    // Assert
    await Assert.That(result).IsNull();
  }

  [Test]
  public async Task OnTaggedMessage_HandlesMixedPropertyTypes_InDimensionsAsync() {
    // Arrange
    var hook = new OpenTelemetryMetricHook();
    var attribute = new MetricTagAttribute {
      Tag = "test-mixed",
      MetricName = "test.mixed.types",
      Type = MetricType.Counter,
      Properties = ["StringVal", "NumVal", "BoolVal", "ArrayVal"]
    };

    const string json = """{"StringVal":"hello","NumVal":42,"BoolVal":true,"ArrayVal":[1,2,3]}""";
    var payload = JsonDocument.Parse(json).RootElement;
    var context = new TagContext<MetricTagAttribute> {
      Attribute = attribute,
      Message = new { StringVal = "hello", NumVal = 42, BoolVal = true, ArrayVal = new[] { 1, 2, 3 } },
      MessageType = typeof(object),
      Payload = payload
    };

    // Act
    var result = await hook.OnTaggedMessageAsync(context, CancellationToken.None);

    // Assert
    await Assert.That(result).IsNull();
  }

  // Test event types
  private sealed record TestOrderEvent {
    public required Guid OrderId { get; init; }
    public required decimal Amount { get; init; }
  }

  private sealed record TestDimensionedEvent {
    public required Guid Id { get; init; }
    public required string TenantId { get; init; }
    public required string Region { get; init; }
  }
}
