using TUnit.Assertions;
using TUnit.Core;
using Whizbang.Core.Messaging;

namespace Whizbang.Core.Tests.Messaging;

/// <summary>
/// Tests for IMessageQueue interface contract.
/// Verifies atomic enqueue-and-lease operations for distributed inbox pattern.
/// </summary>
[Category("Messaging")]
[Category("Contract")]
public class IMessageQueueTests {

  [Test]
  public async Task EnqueueAndLeaseAsync_NewMessage_ReturnsTrue_AndLeasesMessageAsync() {
    // Arrange
    var queue = new FakeMessageQueue();
    var message = new QueuedMessage {
      MessageId = Guid.NewGuid(),
      EventType = "TestEvent",
      EventData = "{}",
      Metadata = null
    };

    // Act
    var result = await queue.EnqueueAndLeaseAsync(message, "instance1", TimeSpan.FromMinutes(5));

    // Assert
    await Assert.That(result).IsTrue();
  }

  [Test]
  public async Task EnqueueAndLeaseAsync_DuplicateMessage_ReturnsFalse_IdempotencyCheckAsync() {
    // Arrange
    var queue = new FakeMessageQueue();
    var message = new QueuedMessage {
      MessageId = Guid.NewGuid(),
      EventType = "TestEvent",
      EventData = "{}",
      Metadata = null
    };
    await queue.EnqueueAndLeaseAsync(message, "instance1", TimeSpan.FromMinutes(5));

    // Act
    var result = await queue.EnqueueAndLeaseAsync(message, "instance1", TimeSpan.FromMinutes(5));

    // Assert
    await Assert.That(result).IsFalse();
  }

  [Test]
  public async Task CompleteAsync_WithLeasedMessage_RemovesFromQueueAsync() {
    // Arrange
    var queue = new FakeMessageQueue();
    var message = new QueuedMessage {
      MessageId = Guid.NewGuid(),
      EventType = "TestEvent",
      EventData = "{}",
      Metadata = null
    };
    await queue.EnqueueAndLeaseAsync(message, "instance1", TimeSpan.FromMinutes(5));

    // Act
    await queue.CompleteAsync(message.MessageId, "instance1", "TestHandler");

    // Assert - Try to enqueue same message again, should return false (already processed)
    var result = await queue.EnqueueAndLeaseAsync(message, "instance2", TimeSpan.FromMinutes(5));
    await Assert.That(result).IsFalse();
  }

  [Test]
  public async Task LeaseOrphanedMessagesAsync_WithExpiredLeases_ClaimsMessagesAsync() {
    // Arrange
    var queue = new FakeMessageQueue();
    var message1 = new QueuedMessage {
      MessageId = Guid.NewGuid(),
      EventType = "TestEvent1",
      EventData = "{}",
      Metadata = null
    };
    var message2 = new QueuedMessage {
      MessageId = Guid.NewGuid(),
      EventType = "TestEvent2",
      EventData = "{}",
      Metadata = null
    };

    // Enqueue messages with short lease duration and simulate expiration
    queue.SetTestTime(DateTime.UtcNow);
    await queue.EnqueueAndLeaseAsync(message1, "instance1", TimeSpan.FromSeconds(1));
    await queue.EnqueueAndLeaseAsync(message2, "instance1", TimeSpan.FromSeconds(1));

    // Simulate time passing to expire leases
    queue.SetTestTime(DateTime.UtcNow.AddSeconds(10));

    // Act
    var orphanedMessages = await queue.LeaseOrphanedMessagesAsync("instance2", 10, TimeSpan.FromMinutes(5));

    // Assert
    await Assert.That(orphanedMessages).Count().IsEqualTo(2);
    await Assert.That(orphanedMessages[0].MessageId).IsEqualTo(message1.MessageId);
    await Assert.That(orphanedMessages[1].MessageId).IsEqualTo(message2.MessageId);
  }

  /// <summary>
  /// Simple in-memory fake implementation of IMessageQueue for contract testing.
  /// Uses dictionaries to track messages, leases, and processed state.
  /// </summary>
  private sealed class FakeMessageQueue : IMessageQueue {
    private readonly Dictionary<Guid, QueuedMessage> _messages = new();
    private readonly HashSet<Guid> _processed = new();
    private readonly Dictionary<Guid, (string instanceId, DateTime leaseExpiry)> _leases = new();
    private DateTime _testTime = DateTime.UtcNow;

    public void SetTestTime(DateTime time) {
      _testTime = time;
    }

    public Task<bool> EnqueueAndLeaseAsync(QueuedMessage message, string instanceId, TimeSpan leaseDuration, CancellationToken cancellationToken = default) {
      // Check if already processed
      if (_processed.Contains(message.MessageId)) {
        return Task.FromResult(false);
      }

      // Check if already exists in queue
      if (_messages.ContainsKey(message.MessageId)) {
        return Task.FromResult(false);
      }

      // Enqueue and lease
      _messages[message.MessageId] = message;
      _leases[message.MessageId] = (instanceId, _testTime.Add(leaseDuration));
      return Task.FromResult(true);
    }

    public Task CompleteAsync(Guid messageId, string instanceId, string handlerName, CancellationToken cancellationToken = default) {
      _processed.Add(messageId);
      _messages.Remove(messageId);
      _leases.Remove(messageId);
      return Task.CompletedTask;
    }

    public Task<IReadOnlyList<QueuedMessage>> LeaseOrphanedMessagesAsync(string instanceId, int maxCount, TimeSpan leaseDuration, CancellationToken cancellationToken = default) {
      var orphaned = _messages.Values
        .Where(m => !_leases.ContainsKey(m.MessageId) || _leases[m.MessageId].leaseExpiry < _testTime)
        .Take(maxCount)
        .ToList();

      foreach (var msg in orphaned) {
        _leases[msg.MessageId] = (instanceId, _testTime.Add(leaseDuration));
      }

      return Task.FromResult<IReadOnlyList<QueuedMessage>>(orphaned);
    }
  }
}
