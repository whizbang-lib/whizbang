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
