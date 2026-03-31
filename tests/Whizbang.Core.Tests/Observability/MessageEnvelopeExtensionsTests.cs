#pragma warning disable CA1707

using System.Text.Json;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Dispatch;
using Whizbang.Core.Messaging;
using Whizbang.Core.Observability;
using Whizbang.Core.ValueObjects;

namespace Whizbang.Core.Tests.Observability;

/// <summary>
/// Tests for MessageEnvelopeExtensions covering ReconstructWithPayload (generic and non-generic),
/// null argument handling, and metadata preservation.
/// </summary>
/// <tests>src/Whizbang.Core/Observability/MessageEnvelopeExtensions.cs</tests>
public class MessageEnvelopeExtensionsTests {
  #region Test Helpers

  private static ServiceInstanceInfo _createServiceInstance() =>
    new() {
      ServiceName = "TestService",
      InstanceId = Guid.NewGuid(),
      HostName = "localhost",
      ProcessId = 1234
    };

  private static MessageEnvelope<JsonElement> _createJsonEnvelope(
      object payload,
      List<MessageHop>? hops = null) {
    var jsonPayload = JsonSerializer.SerializeToElement(payload);
    return new MessageEnvelope<JsonElement> {
      MessageId = MessageId.New(),
      Payload = jsonPayload,
      Hops = hops ?? [
        new MessageHop {
          ServiceInstance = _createServiceInstance(),
          Timestamp = DateTimeOffset.UtcNow,
          Topic = "test-topic"
        }
      ],
      DispatchContext = new MessageDispatchContext { Mode = DispatchModes.Local, Source = MessageSource.Local }
    };
  }

  private sealed record TestOrder(string OrderId, decimal Amount);

  #endregion

  #region ReconstructWithPayload (non-generic) Tests

  [Test]
  public async Task ReconstructWithPayload_NonGeneric_PreservesMessageIdAsync() {
    // Arrange
    var jsonEnvelope = _createJsonEnvelope(new { Name = "test" });
    var deserializedPayload = new object();

    // Act
    var result = jsonEnvelope.ReconstructWithPayload(deserializedPayload);

    // Assert
    await Assert.That(result.MessageId).IsEqualTo(jsonEnvelope.MessageId);
  }

  [Test]
  public async Task ReconstructWithPayload_NonGeneric_PreservesHopsAsync() {
    // Arrange
    var hops = new List<MessageHop> {
      new() {
        ServiceInstance = _createServiceInstance(),
        Timestamp = DateTimeOffset.UtcNow,
        Topic = "topic-1"
      },
      new() {
        ServiceInstance = _createServiceInstance(),
        Timestamp = DateTimeOffset.UtcNow,
        Topic = "topic-2"
      }
    };
    var jsonEnvelope = _createJsonEnvelope(new { Name = "test" }, hops);
    var deserializedPayload = new object();

    // Act
    var result = jsonEnvelope.ReconstructWithPayload(deserializedPayload);

    // Assert
    await Assert.That(result.Hops).Count().IsEqualTo(2);
    await Assert.That(result.Hops[0].Topic).IsEqualTo("topic-1");
    await Assert.That(result.Hops[1].Topic).IsEqualTo("topic-2");
  }

  [Test]
  public async Task ReconstructWithPayload_NonGeneric_SetsDeserializedPayloadAsync() {
    // Arrange
    var jsonEnvelope = _createJsonEnvelope(new { Name = "test" });
    var deserializedPayload = new TestOrder("ORD-001", 99.99m);

    // Act
    var result = jsonEnvelope.ReconstructWithPayload((object)deserializedPayload);

    // Assert
    await Assert.That(result.Payload).IsEqualTo(deserializedPayload);
  }

  [Test]
  public async Task ReconstructWithPayload_NonGeneric_ThrowsOnNullEnvelopeAsync() {
    // Arrange
    IMessageEnvelope<JsonElement>? nullEnvelope = null;

    // Act & Assert
    await Assert.That(() =>
      nullEnvelope!.ReconstructWithPayload(new object())
    ).ThrowsExactly<ArgumentNullException>();
  }

  [Test]
  public async Task ReconstructWithPayload_NonGeneric_ThrowsOnNullPayloadAsync() {
    // Arrange
    var jsonEnvelope = _createJsonEnvelope(new { Name = "test" });

    // Act & Assert
    await Assert.That(() =>
      jsonEnvelope.ReconstructWithPayload((object)null!)
    ).ThrowsExactly<ArgumentNullException>();
  }

  #endregion

  #region ReconstructWithPayload<T> (generic) Tests

  [Test]
  public async Task ReconstructWithPayload_Generic_PreservesMessageIdAsync() {
    // Arrange
    var jsonEnvelope = _createJsonEnvelope(new { OrderId = "ORD-001", Amount = 99.99 });
    var order = new TestOrder("ORD-001", 99.99m);

    // Act
    var result = jsonEnvelope.ReconstructWithPayload(order);

    // Assert
    await Assert.That(result.MessageId).IsEqualTo(jsonEnvelope.MessageId);
  }

  [Test]
  public async Task ReconstructWithPayload_Generic_PreservesHopsAsync() {
    // Arrange
    var hops = new List<MessageHop> {
      new() {
        ServiceInstance = _createServiceInstance(),
        Timestamp = DateTimeOffset.UtcNow,
        Topic = "orders"
      }
    };
    var jsonEnvelope = _createJsonEnvelope(new { OrderId = "ORD-001" }, hops);
    var order = new TestOrder("ORD-001", 50m);

    // Act
    var result = jsonEnvelope.ReconstructWithPayload(order);

    // Assert
    await Assert.That(result.Hops).Count().IsEqualTo(1);
    await Assert.That(result.Hops[0].Topic).IsEqualTo("orders");
  }

  [Test]
  public async Task ReconstructWithPayload_Generic_ReturnsStronglyTypedEnvelopeAsync() {
    // Arrange
    var jsonEnvelope = _createJsonEnvelope(new { OrderId = "ORD-001", Amount = 99.99 });
    var order = new TestOrder("ORD-001", 99.99m);

    // Act
    MessageEnvelope<TestOrder> result = jsonEnvelope.ReconstructWithPayload(order);

    // Assert
    await Assert.That(result.Payload.OrderId).IsEqualTo("ORD-001");
    await Assert.That(result.Payload.Amount).IsEqualTo(99.99m);
  }

  [Test]
  public async Task ReconstructWithPayload_Generic_ThrowsOnNullEnvelopeAsync() {
    // Arrange
    IMessageEnvelope<JsonElement>? nullEnvelope = null;

    // Act & Assert
    await Assert.That(() =>
      nullEnvelope!.ReconstructWithPayload(new TestOrder("ORD-001", 10m))
    ).ThrowsExactly<ArgumentNullException>();
  }

  [Test]
  public async Task ReconstructWithPayload_Generic_ThrowsOnNullPayloadAsync() {
    // Arrange
    var jsonEnvelope = _createJsonEnvelope(new { Name = "test" });

    // Act & Assert
    await Assert.That(() =>
      jsonEnvelope.ReconstructWithPayload<TestOrder>(null!)
    ).ThrowsExactly<ArgumentNullException>();
  }

  [Test]
  public async Task ReconstructWithPayload_Generic_SharesHopsReferenceAsync() {
    // Arrange - the reconstructed envelope should share the same Hops list reference
    var jsonEnvelope = _createJsonEnvelope(new { Name = "test" });
    var order = new TestOrder("ORD-001", 10m);

    // Act
    var result = jsonEnvelope.ReconstructWithPayload(order);

    // Assert - Hops should be same reference (not a copy)
    await Assert.That(ReferenceEquals(result.Hops, jsonEnvelope.Hops)).IsTrue();
  }

  #endregion
}
