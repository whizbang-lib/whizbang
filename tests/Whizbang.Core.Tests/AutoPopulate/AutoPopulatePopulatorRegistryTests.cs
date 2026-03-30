using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.AutoPopulate;
using Whizbang.Core.Observability;
using Whizbang.Core.Registry;
using Whizbang.Core.ValueObjects;

namespace Whizbang.Core.Tests.AutoPopulate;

/// <summary>
/// Tests for AutoPopulatePopulatorRegistry - typed record population using 'with' expressions.
/// </summary>
public class AutoPopulatePopulatorRegistryTests {
  // Use a unique record type that no generated populator handles to avoid interference
  private sealed record TestPopulatorOrderCreated(
      Guid OrderId,
      DateTimeOffset? SentAt = null,
      DateTimeOffset? QueuedAt = null,
      DateTimeOffset? DeliveredAt = null);

  private sealed class TestPopulator : IAutoPopulatePopulator {
    public object? TryPopulateSent(object message, MessageHop hop, MessageId messageId) {
      if (message is TestPopulatorOrderCreated m) {
        return m with { SentAt = hop.Timestamp };
      }
      return null;
    }

    public object? TryPopulateQueued(object message, DateTimeOffset timestamp) {
      if (message is TestPopulatorOrderCreated m) {
        return m with { QueuedAt = timestamp };
      }
      return null;
    }

    public object? TryPopulateDelivered(object message, DateTimeOffset timestamp) {
      if (message is TestPopulatorOrderCreated m) {
        return m with { DeliveredAt = timestamp };
      }
      return null;
    }
  }

  // Thread-safe one-time registration using Lazy to block concurrent callers
  // until registration completes (unlike Interlocked.CompareExchange which is non-blocking)
  private static readonly Lazy<TestPopulator> _registeredPopulator = new(() => {
    var populator = new TestPopulator();
    AutoPopulatePopulatorRegistry.Register(populator, priority: 50);
    return populator;
  });

  private static void _ensureRegistered() => _ = _registeredPopulator.Value;

  [Test]
  public async Task PopulateSent_WithMatchingType_ReturnsPopulatedRecordAsync() {
    _ensureRegistered();

    var message = new TestPopulatorOrderCreated(Guid.NewGuid());
    var hop = new MessageHop {
      Type = HopType.Current,
      ServiceInstance = new ServiceInstanceInfo {
        ServiceName = "TestService",
        InstanceId = Guid.NewGuid(),
        HostName = "localhost",
        ProcessId = 1234
      },
      Timestamp = DateTimeOffset.UtcNow
    };
    var messageId = MessageId.New();

    var result = AutoPopulatePopulatorRegistry.PopulateSent(message, hop, messageId);

    await Assert.That(result).IsTypeOf<TestPopulatorOrderCreated>();
    var populated = (TestPopulatorOrderCreated)result;
    await Assert.That(populated.SentAt).IsEqualTo(hop.Timestamp);
    await Assert.That(populated.OrderId).IsEqualTo(message.OrderId);
  }

  [Test]
  public async Task PopulateQueued_WithMatchingType_SetsQueuedAtTimestampAsync() {
    _ensureRegistered();

    var message = new TestPopulatorOrderCreated(Guid.NewGuid());
    var timestamp = DateTimeOffset.UtcNow;

    var result = AutoPopulatePopulatorRegistry.PopulateQueued(message, timestamp);

    await Assert.That(result).IsTypeOf<TestPopulatorOrderCreated>();
    var populated = (TestPopulatorOrderCreated)result;
    await Assert.That(populated.QueuedAt).IsEqualTo(timestamp);
  }

  [Test]
  public async Task PopulateDelivered_WithMatchingType_SetsDeliveredAtTimestampAsync() {
    _ensureRegistered();

    var message = new TestPopulatorOrderCreated(Guid.NewGuid());
    var timestamp = DateTimeOffset.UtcNow;

    var result = AutoPopulatePopulatorRegistry.PopulateDelivered(message, timestamp);

    await Assert.That(result).IsTypeOf<TestPopulatorOrderCreated>();
    var populated = (TestPopulatorOrderCreated)result;
    await Assert.That(populated.DeliveredAt).IsEqualTo(timestamp);
  }

  [Test]
  public async Task PopulateSent_WithNonMatchingType_ReturnsOriginalMessageAsync() {
    _ensureRegistered();

    const string message = "not a record";
    var hop = new MessageHop {
      Type = HopType.Current,
      ServiceInstance = new ServiceInstanceInfo {
        ServiceName = "TestService",
        InstanceId = Guid.NewGuid(),
        HostName = "localhost",
        ProcessId = 1234
      },
      Timestamp = DateTimeOffset.UtcNow
    };
    var messageId = MessageId.New();

    var result = AutoPopulatePopulatorRegistry.PopulateSent(message, hop, messageId);

    await Assert.That(result).IsSameReferenceAs(message);
  }

  [Test]
  public async Task Count_AfterRegistration_IsGreaterThanZeroAsync() {
    _ensureRegistered();

    await Assert.That(AutoPopulatePopulatorRegistry.Count).IsGreaterThan(0);
  }

  [Test]
  public async Task PopulateSent_PreservesImmutability_ReturnsNewInstanceAsync() {
    _ensureRegistered();

    var message = new TestPopulatorOrderCreated(Guid.NewGuid());
    var hop = new MessageHop {
      Type = HopType.Current,
      ServiceInstance = new ServiceInstanceInfo {
        ServiceName = "TestService",
        InstanceId = Guid.NewGuid(),
        HostName = "localhost",
        ProcessId = 1234
      },
      Timestamp = DateTimeOffset.UtcNow
    };
    var messageId = MessageId.New();

    var result = AutoPopulatePopulatorRegistry.PopulateSent(message, hop, messageId);

    // Original message should still have null SentAt
    await Assert.That(message.SentAt).IsNull();
    // Result should be a different instance
    await Assert.That(result).IsNotSameReferenceAs(message);
  }
}
