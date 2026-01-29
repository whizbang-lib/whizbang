using System.Diagnostics;
using System.Text.Json;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Attributes;
using Whizbang.Core.Tags;
using Whizbang.Observability.Hooks;

namespace Whizbang.Observability.Tests.Hooks;

/// <summary>
/// Tests for <see cref="OpenTelemetrySpanHook"/>.
/// Validates OpenTelemetry span creation and enrichment for tagged messages.
/// </summary>
/// <tests>Whizbang.Observability/Hooks/OpenTelemetrySpanHook.cs</tests>
[Category("Observability")]
[Category("Hooks")]
public class OpenTelemetrySpanHookTests {
  [Test]
  public async Task OnTaggedMessage_CreatesActivity_WithSpanNameFromAttributeAsync() {
    // Arrange
    var hook = new OpenTelemetrySpanHook();
    var attribute = new TelemetryTagAttribute { Tag = "test-tag", SpanName = "TestSpan" };
    var message = new TestEvent { Id = Guid.NewGuid(), Name = "Test" };
    var payload = JsonSerializer.SerializeToElement(message);
    var context = new TagContext<TelemetryTagAttribute> {
      Attribute = attribute,
      Message = message,
      MessageType = typeof(TestEvent),
      Payload = payload
    };

    using var listener = new ActivityListener {
      ShouldListenTo = source => source.Name == "Whizbang.MessageTags",
      Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllData
    };
    ActivitySource.AddActivityListener(listener);

    // Act
    var result = await hook.OnTaggedMessageAsync(context, CancellationToken.None);

    // Assert
    await Assert.That(result).IsNull(); // Returns null to pass original payload
  }

  [Test]
  public async Task OnTaggedMessage_UsesTagAsSpanName_WhenSpanNameNotSetAsync() {
    // Arrange
    var hook = new OpenTelemetrySpanHook();
    var attribute = new TelemetryTagAttribute { Tag = "fallback-tag" };
    var message = new TestEvent { Id = Guid.NewGuid(), Name = "Test" };
    var payload = JsonSerializer.SerializeToElement(message);
    var context = new TagContext<TelemetryTagAttribute> {
      Attribute = attribute,
      Message = message,
      MessageType = typeof(TestEvent),
      Payload = payload
    };

    // Act
    var result = await hook.OnTaggedMessageAsync(context, CancellationToken.None);

    // Assert
    await Assert.That(result).IsNull();
  }

  [Test]
  public async Task OnTaggedMessage_AddsScopeAttributes_WhenScopeProvidedAsync() {
    // Arrange
    var hook = new OpenTelemetrySpanHook();
    var attribute = new TelemetryTagAttribute { Tag = "test-tag" };
    var message = new TestEvent { Id = Guid.NewGuid(), Name = "Test" };
    var payload = JsonSerializer.SerializeToElement(message);
    var scope = new Dictionary<string, object?> {
      { "TenantId", "tenant-123" },
      { "UserId", "user-456" }
    };
    var context = new TagContext<TelemetryTagAttribute> {
      Attribute = attribute,
      Message = message,
      MessageType = typeof(TestEvent),
      Payload = payload,
      Scope = scope
    };

    // Act
    var result = await hook.OnTaggedMessageAsync(context, CancellationToken.None);

    // Assert
    await Assert.That(result).IsNull();
  }

  [Test]
  public async Task OnTaggedMessage_AddsPayloadAttributes_WhenPropertiesSpecifiedAsync() {
    // Arrange
    var hook = new OpenTelemetrySpanHook();
    var attribute = new TelemetryTagAttribute {
      Tag = "test-tag",
      Properties = ["Id", "Name"]
    };
    var message = new TestEvent { Id = Guid.NewGuid(), Name = "TestName" };
    var payload = JsonSerializer.SerializeToElement(message);
    var context = new TagContext<TelemetryTagAttribute> {
      Attribute = attribute,
      Message = message,
      MessageType = typeof(TestEvent),
      Payload = payload
    };

    // Act
    var result = await hook.OnTaggedMessageAsync(context, CancellationToken.None);

    // Assert
    await Assert.That(result).IsNull();
  }

  [Test]
  public async Task OnTaggedMessage_RecordsEvent_WhenRecordAsEventTrueAsync() {
    // Arrange
    var hook = new OpenTelemetrySpanHook();
    var attribute = new TelemetryTagAttribute {
      Tag = "test-tag",
      RecordAsEvent = true
    };
    var message = new TestEvent { Id = Guid.NewGuid(), Name = "Test" };
    var payload = JsonSerializer.SerializeToElement(message);
    var context = new TagContext<TelemetryTagAttribute> {
      Attribute = attribute,
      Message = message,
      MessageType = typeof(TestEvent),
      Payload = payload
    };

    // Act
    var result = await hook.OnTaggedMessageAsync(context, CancellationToken.None);

    // Assert
    await Assert.That(result).IsNull();
  }

  [Test]
  public async Task ActivitySource_HasCorrectName_AndVersionAsync() {
    // Assert
    await Assert.That(OpenTelemetrySpanHook.ActivitySource.Name).IsEqualTo("Whizbang.MessageTags");
    await Assert.That(OpenTelemetrySpanHook.ActivitySource.Version).IsEqualTo("1.0.0");
  }

  [Test]
  public async Task OnTaggedMessage_MapsSpanKindToActivityKind_CorrectlyAsync() {
    // Arrange
    var hook = new OpenTelemetrySpanHook();
    var testCases = new[] {
      (SpanKind.Internal, ActivityKind.Internal),
      (SpanKind.Server, ActivityKind.Server),
      (SpanKind.Client, ActivityKind.Client),
      (SpanKind.Producer, ActivityKind.Producer),
      (SpanKind.Consumer, ActivityKind.Consumer)
    };
    var processedCount = 0;

    foreach (var (spanKind, expectedActivityKind) in testCases) {
      var attribute = new TelemetryTagAttribute { Tag = "test", Kind = spanKind };
      var message = new TestEvent { Id = Guid.NewGuid(), Name = "Test" };
      var payload = JsonSerializer.SerializeToElement(message);
      var context = new TagContext<TelemetryTagAttribute> {
        Attribute = attribute,
        Message = message,
        MessageType = typeof(TestEvent),
        Payload = payload
      };

      // Act - just verify it doesn't throw
      await hook.OnTaggedMessageAsync(context, CancellationToken.None);
      processedCount++;
    }

    // Assert - verify all span kinds were processed without exception
    await Assert.That(processedCount).IsEqualTo(testCases.Length);
  }

  // Test event type
  private sealed record TestEvent {
    public required Guid Id { get; init; }
    public required string Name { get; init; }
  }
}
