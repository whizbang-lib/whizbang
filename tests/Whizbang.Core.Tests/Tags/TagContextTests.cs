using System;
using System.Collections.Generic;
using System.Text.Json;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Attributes;
using Whizbang.Core.Tags;

namespace Whizbang.Core.Tests.Tags;

/// <summary>
/// Tests for <see cref="TagContext{TAttribute}"/>.
/// Validates the context record passed to message tag hooks.
/// </summary>
/// <tests>Whizbang.Core/Tags/TagContext.cs</tests>
[Category("Core")]
[Category("Tags")]
public class TagContextTests {

  [Test]
  public async Task TagContext_Attribute_CanBeSetAndRetrievedAsync() {
    // Arrange
    var attribute = new NotificationTagAttribute {
      Tag = "test-tag",
      Group = "test-group"
    };

    // Act
    var context = new TagContext<NotificationTagAttribute> {
      Attribute = attribute,
      Message = new TestMessage(Guid.NewGuid()),
      MessageType = typeof(TestMessage),
      Payload = JsonSerializer.SerializeToElement(new { test = "value" })
    };

    // Assert
    await Assert.That(context.Attribute).IsEqualTo(attribute);
    await Assert.That(context.Attribute.Tag).IsEqualTo("test-tag");
    await Assert.That(context.Attribute.Group).IsEqualTo("test-group");
  }

  [Test]
  public async Task TagContext_AttributeType_ReturnsCorrectTypeAsync() {
    // Arrange
    var context = new TagContext<NotificationTagAttribute> {
      Attribute = new NotificationTagAttribute { Tag = "test" },
      Message = new TestMessage(Guid.NewGuid()),
      MessageType = typeof(TestMessage),
      Payload = JsonSerializer.SerializeToElement(new { })
    };

    // Act & Assert
    await Assert.That(context.AttributeType).IsEqualTo(typeof(NotificationTagAttribute));
  }

  [Test]
  public async Task TagContext_Message_CanBeSetAndRetrievedAsync() {
    // Arrange
    var message = new TestMessage(Guid.NewGuid());

    // Act
    var context = new TagContext<NotificationTagAttribute> {
      Attribute = new NotificationTagAttribute { Tag = "test" },
      Message = message,
      MessageType = typeof(TestMessage),
      Payload = JsonSerializer.SerializeToElement(new { })
    };

    // Assert
    await Assert.That(context.Message).IsEqualTo(message);
  }

  [Test]
  public async Task TagContext_MessageType_CanBeSetAndRetrievedAsync() {
    // Act
    var context = new TagContext<NotificationTagAttribute> {
      Attribute = new NotificationTagAttribute { Tag = "test" },
      Message = new TestMessage(Guid.NewGuid()),
      MessageType = typeof(TestMessage),
      Payload = JsonSerializer.SerializeToElement(new { })
    };

    // Assert
    await Assert.That(context.MessageType).IsEqualTo(typeof(TestMessage));
  }

  [Test]
  public async Task TagContext_Payload_CanBeSetAndRetrievedAsync() {
    // Arrange
    var payloadData = new { OrderId = Guid.NewGuid(), Status = "Completed" };
    var payload = JsonSerializer.SerializeToElement(payloadData);

    // Act
    var context = new TagContext<NotificationTagAttribute> {
      Attribute = new NotificationTagAttribute { Tag = "test" },
      Message = new TestMessage(Guid.NewGuid()),
      MessageType = typeof(TestMessage),
      Payload = payload
    };

    // Assert
    await Assert.That(context.Payload.ValueKind).IsEqualTo(JsonValueKind.Object);
    await Assert.That(context.Payload.GetProperty("Status").GetString()).IsEqualTo("Completed");
  }

  [Test]
  public async Task TagContext_Scope_IsNullByDefaultAsync() {
    // Act
    var context = new TagContext<NotificationTagAttribute> {
      Attribute = new NotificationTagAttribute { Tag = "test" },
      Message = new TestMessage(Guid.NewGuid()),
      MessageType = typeof(TestMessage),
      Payload = JsonSerializer.SerializeToElement(new { })
    };

    // Assert
    await Assert.That(context.Scope is null).IsTrue();
  }

  [Test]
  public async Task TagContext_Scope_CanBeSetWithDictionaryAsync() {
    // Arrange
    var scope = new Dictionary<string, object?> {
      ["TenantId"] = "tenant-123",
      ["UserId"] = "user-456",
      ["UserName"] = "John Doe"
    };

    // Act
    var context = new TagContext<NotificationTagAttribute> {
      Attribute = new NotificationTagAttribute { Tag = "test" },
      Message = new TestMessage(Guid.NewGuid()),
      MessageType = typeof(TestMessage),
      Payload = JsonSerializer.SerializeToElement(new { }),
      Scope = scope
    };

    // Assert
    await Assert.That(context.Scope).IsNotNull();
    await Assert.That(context.Scope!["TenantId"]).IsEqualTo("tenant-123");
    await Assert.That(context.Scope!["UserId"]).IsEqualTo("user-456");
    await Assert.That(context.Scope!["UserName"]).IsEqualTo("John Doe");
  }

  [Test]
  public async Task TagContext_Scope_CanContainNullValuesAsync() {
    // Arrange
    var scope = new Dictionary<string, object?> {
      ["TenantId"] = "tenant-123",
      ["UserId"] = null
    };

    // Act
    var context = new TagContext<NotificationTagAttribute> {
      Attribute = new NotificationTagAttribute { Tag = "test" },
      Message = new TestMessage(Guid.NewGuid()),
      MessageType = typeof(TestMessage),
      Payload = JsonSerializer.SerializeToElement(new { }),
      Scope = scope
    };

    // Assert
    await Assert.That(context.Scope).IsNotNull();
    await Assert.That(context.Scope!["TenantId"]).IsEqualTo("tenant-123");
    await Assert.That(context.Scope!["UserId"]).IsNull();
  }

  [Test]
  public async Task TagContext_WithDifferentAttributeTypes_WorksCorrectlyAsync() {
    // Arrange - TelemetryTagAttribute context
    var telemetryContext = new TagContext<TelemetryTagAttribute> {
      Attribute = new TelemetryTagAttribute {
        Tag = "telemetry-test",
        SpanName = "TestSpan",
        Kind = SpanKind.Internal
      },
      Message = new TestMessage(Guid.NewGuid()),
      MessageType = typeof(TestMessage),
      Payload = JsonSerializer.SerializeToElement(new { })
    };

    // Assert
    await Assert.That(telemetryContext.AttributeType).IsEqualTo(typeof(TelemetryTagAttribute));
    await Assert.That(telemetryContext.Attribute.SpanName).IsEqualTo("TestSpan");
    await Assert.That(telemetryContext.Attribute.Kind).IsEqualTo(SpanKind.Internal);
  }

  [Test]
  public async Task TagContext_PayloadWithEventKey_CanBeAccessedAsync() {
    // Arrange - Simulate payload with __event key (IncludeEvent = true)
    var eventData = new TestMessage(Guid.NewGuid());
    var payloadData = new Dictionary<string, object> {
      ["OrderId"] = eventData.Id,
      ["__event"] = eventData
    };
    var payload = JsonSerializer.SerializeToElement(payloadData);

    // Act
    var context = new TagContext<NotificationTagAttribute> {
      Attribute = new NotificationTagAttribute { Tag = "test", IncludeEvent = true },
      Message = eventData,
      MessageType = typeof(TestMessage),
      Payload = payload
    };

    // Assert
    await Assert.That(context.Payload.TryGetProperty("__event", out _)).IsTrue();
  }

  // Test helper type
  private sealed record TestMessage(Guid Id);
}
