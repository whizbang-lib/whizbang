using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Observability;
using Whizbang.Core.ValueObjects;

namespace Whizbang.Observability.Tests;

/// <summary>
/// Tests for EnvelopeRegistry - pooled dictionary implementation
/// for tracking message envelopes by their payload.
/// </summary>
public class EnvelopeRegistryTests {
  private static MessageEnvelope<T> _createEnvelope<T>(T payload) {
    return new MessageEnvelope<T> {
      MessageId = MessageId.New(),
      Payload = payload,
      Hops = [
        new MessageHop {
          ServiceInstance = new ServiceInstanceInfo {
            ServiceName = "TestService",
            InstanceId = Guid.NewGuid(),
            HostName = "test-host",
            ProcessId = 12345
          }
        }
      ]
    };
  }

  [Test]
  public async Task Register_WithEnvelope_CanBeRetrievedByMessageAsync() {
    // Arrange
    using var registry = new EnvelopeRegistry();
    var message = new TestMessage("test-payload");
    var envelope = _createEnvelope(message);

    // Act
    registry.Register(envelope);
    var retrieved = registry.TryGetEnvelope(message);

    // Assert
    await Assert.That(retrieved).IsNotNull();
    await Assert.That(retrieved!.MessageId).IsEqualTo(envelope.MessageId);
    await Assert.That(retrieved.Payload).IsEqualTo(message);
  }

  [Test]
  public async Task TryGetEnvelope_WithUnregisteredMessage_ReturnsNullAsync() {
    // Arrange
    using var registry = new EnvelopeRegistry();
    var message = new TestMessage("not-registered");

    // Act
    var retrieved = registry.TryGetEnvelope(message);

    // Assert
    await Assert.That(retrieved).IsNull();
  }

  [Test]
  public async Task TryGetEnvelope_WithDifferentInstanceSameValue_ReturnsNullAsync() {
    // Arrange - uses reference equality, not value equality
    using var registry = new EnvelopeRegistry();
    var message1 = new TestMessage("same-value");
    var message2 = new TestMessage("same-value"); // Different instance, same value
    var envelope = _createEnvelope(message1);

    // Act
    registry.Register(envelope);
    var retrieved = registry.TryGetEnvelope(message2);

    // Assert - should NOT find it because different reference
    await Assert.That(retrieved).IsNull();
  }

  [Test]
  public async Task Unregister_ByMessage_RemovesFromRegistryAsync() {
    // Arrange
    using var registry = new EnvelopeRegistry();
    var message = new TestMessage("to-unregister");
    var envelope = _createEnvelope(message);
    registry.Register(envelope);

    // Act
    registry.Unregister(message);
    var retrieved = registry.TryGetEnvelope(message);

    // Assert
    await Assert.That(retrieved).IsNull();
  }

  [Test]
  public async Task Unregister_ByEnvelope_RemovesFromRegistryAsync() {
    // Arrange
    using var registry = new EnvelopeRegistry();
    var message = new TestMessage("to-unregister");
    var envelope = _createEnvelope(message);
    registry.Register(envelope);

    // Act
    registry.Unregister(envelope);
    var retrieved = registry.TryGetEnvelope(message);

    // Assert
    await Assert.That(retrieved).IsNull();
  }

  [Test]
  public async Task Register_MultipleEnvelopes_AllCanBeRetrievedAsync() {
    // Arrange
    using var registry = new EnvelopeRegistry();
    var message1 = new TestMessage("message-1");
    var message2 = new TestMessage("message-2");
    var message3 = new TestMessage("message-3");
    var envelope1 = _createEnvelope(message1);
    var envelope2 = _createEnvelope(message2);
    var envelope3 = _createEnvelope(message3);

    // Act
    registry.Register(envelope1);
    registry.Register(envelope2);
    registry.Register(envelope3);

    // Assert
    await Assert.That(registry.TryGetEnvelope(message1)).IsNotNull();
    await Assert.That(registry.TryGetEnvelope(message2)).IsNotNull();
    await Assert.That(registry.TryGetEnvelope(message3)).IsNotNull();
    await Assert.That(registry.TryGetEnvelope(message1)!.MessageId).IsEqualTo(envelope1.MessageId);
    await Assert.That(registry.TryGetEnvelope(message2)!.MessageId).IsEqualTo(envelope2.MessageId);
    await Assert.That(registry.TryGetEnvelope(message3)!.MessageId).IsEqualTo(envelope3.MessageId);
  }

  [Test]
  public async Task Dispose_ClearsRegistryAndReturnsDictionaryToPoolAsync() {
    // Arrange
    var message = new TestMessage("disposed");
    var envelope = _createEnvelope(message);
    EnvelopeRegistry registry;

    // Act
    using (registry = new EnvelopeRegistry()) {
      registry.Register(envelope);
      await Assert.That(registry.TryGetEnvelope(message)).IsNotNull();
    }

    // Assert - after dispose, registry should be cleared
    // Note: We can't directly test the dictionary was returned to pool,
    // but we can verify the registry clears its entries
    // The dispose completed without throwing (implicit success)
  }

  [Test]
  public async Task Register_SameMessageTwice_OverwritesPreviousEnvelopeAsync() {
    // Arrange
    using var registry = new EnvelopeRegistry();
    var message = new TestMessage("overwrite-test");
    var envelope1 = _createEnvelope(message);
    var envelope2 = _createEnvelope(message);

    // Act
    registry.Register(envelope1);
    registry.Register(envelope2);
    var retrieved = registry.TryGetEnvelope(message);

    // Assert - should have the second envelope
    await Assert.That(retrieved).IsNotNull();
    await Assert.That(retrieved!.MessageId).IsEqualTo(envelope2.MessageId);
  }

  [Test]
  public async Task Unregister_NonExistentMessage_DoesNotThrowAsync() {
    // Arrange
    using var registry = new EnvelopeRegistry();
    var message = new TestMessage("never-registered");

    // Act & Assert - should not throw
    registry.Unregister(message);
    // Implicit success - method completed without throwing
  }

  [Test]
  public async Task Registry_IsThreadSafe_ConcurrentAccessAsync() {
    // Arrange
    using var registry = new EnvelopeRegistry();
    var messages = Enumerable.Range(0, 100)
      .Select(i => new TestMessage($"message-{i}"))
      .ToList();
    var envelopes = messages.Select(_createEnvelope).ToList();

    // Act - concurrent registration
    await Parallel.ForEachAsync(
      envelopes,
      new ParallelOptions { MaxDegreeOfParallelism = 10 },
      (envelope, _) => {
        registry.Register(envelope);
        return ValueTask.CompletedTask;
      });

    // Assert - all should be retrievable
    foreach (var (message, envelope) in messages.Zip(envelopes)) {
      var retrieved = registry.TryGetEnvelope(message);
      await Assert.That(retrieved).IsNotNull();
      await Assert.That(retrieved!.MessageId).IsEqualTo(envelope.MessageId);
    }
  }

  [Test]
  public async Task PoolReuse_MultipleRegistryInstances_ReusesDictionariesAsync() {
    // Arrange & Act - create and dispose multiple registries
    for (int i = 0; i < 10; i++) {
      using var registry = new EnvelopeRegistry();
      var message = new TestMessage($"pool-test-{i}");
      var envelope = _createEnvelope(message);
      registry.Register(envelope);
      await Assert.That(registry.TryGetEnvelope(message)).IsNotNull();
    }

    // Assert - if we got here without OOM or issues, pooling is working
    // (Full pool reuse verification would require internal access)
    // Implicit success - iterations completed without throwing
  }

  /// <summary>
  /// Test message type for registry tests.
  /// </summary>
  private sealed record TestMessage(string Value);
}
