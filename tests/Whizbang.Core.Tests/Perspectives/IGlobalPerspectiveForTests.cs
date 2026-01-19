using TUnit.Core;
using Whizbang.Core;
using Whizbang.Core.Perspectives;

namespace Whizbang.Core.Tests.Perspectives;

/// <summary>
/// Tests for IGlobalPerspectiveFor interface - multi-stream perspective pattern with partition keys.
/// Inspired by Marten's MultiStreamProjection Identity() pattern.
/// </summary>
public class IGlobalPerspectiveForTests {
  [Test]
  public async Task GlobalPerspective_HasGetPartitionKeyMethod_ExtractsPartitionFromEventAsync() {
    // Arrange
    var perspective = new TestGlobalPerspective();
    var @event = new TestEventWithPartitionKey { PartitionId = Guid.NewGuid(), Delta = 5 };

    // Act
    var partitionKey = perspective.GetPartitionKey(@event);

    // Assert
    await Assert.That(partitionKey).IsEqualTo(@event.PartitionId);
  }

  [Test]
  public async Task GlobalPerspective_ApplyMethod_IsPureFunctionAsync() {
    // Arrange
    var perspective = new TestGlobalPerspective();
    var model = new TestModel { Value = 10 };
    var @event = new TestEventWithPartitionKey { PartitionId = Guid.NewGuid(), Delta = 3 };

    // Act - Apply should be pure (no async, no I/O)
    var result = perspective.Apply(model, @event);

    // Assert
    await Assert.That(result).IsNotNull();
    await Assert.That(result.Value).IsEqualTo(13);
    await Assert.That(model.Value).IsEqualTo(10); // Original unchanged (pure!)
  }

  [Test]
  public async Task GlobalPerspective_MultipleEventTypes_HasGetPartitionKeyForEachAsync() {
    // Arrange
    var perspective = new MultiEventGlobalPerspective();
    var event1 = new TestEventWithPartitionKey { PartitionId = Guid.NewGuid(), Delta = 5 };
    var event2 = new AnotherTestEventWithPartitionKey { PartitionId = Guid.NewGuid(), Multiplier = 2 };

    // Act
    var key1 = perspective.GetPartitionKey(event1);
    var key2 = perspective.GetPartitionKey(event2);

    // Assert
    await Assert.That(key1).IsEqualTo(event1.PartitionId);
    await Assert.That(key2).IsEqualTo(event2.PartitionId);
  }

  [Test]
  public async Task GlobalPerspective_DifferentPartitionKeys_CanUseStringTypeAsync() {
    // Arrange
    var perspective = new StringPartitionPerspective();
    var @event = new TestEventWithStringPartition { TenantId = "tenant-123", Value = 42 };

    // Act
    var partitionKey = perspective.GetPartitionKey(@event);

    // Assert
    await Assert.That(partitionKey).IsEqualTo("tenant-123");
  }

  [Test]
  public async Task GlobalPerspective_EventsFromDifferentStreams_UpdateSamePartitionAsync() {
    // Arrange
    var perspective = new TestGlobalPerspective();
    var partitionId = Guid.NewGuid();
    var model = new TestModel { Value = 0 };

    // Events from different streams but same partition
    var event1 = new TestEventWithPartitionKey { PartitionId = partitionId, Delta = 5 };
    var event2 = new TestEventWithPartitionKey { PartitionId = partitionId, Delta = 3 };

    // Act - Apply events to same partition
    var afterEvent1 = perspective.Apply(model, event1);
    var afterEvent2 = perspective.Apply(afterEvent1, event2);

    // Assert - Both events updated the same partitioned model
    await Assert.That(afterEvent2.Value).IsEqualTo(8);
  }
}

// Test implementations

internal sealed record TestEventWithPartitionKey : IEvent {
  public required Guid PartitionId { get; init; }
  public int Delta { get; init; }
}

internal sealed record AnotherTestEventWithPartitionKey : IEvent {
  public required Guid PartitionId { get; init; }
  public int Multiplier { get; init; }
}

internal sealed record TestEventWithStringPartition : IEvent {
  public required string TenantId { get; init; }
  public int Value { get; init; }
}

// Test global perspective with Guid partition key
internal sealed class TestGlobalPerspective : IGlobalPerspectiveFor<TestModel, Guid, TestEventWithPartitionKey> {
  public Guid GetPartitionKey(TestEventWithPartitionKey @event) {
    return @event.PartitionId;
  }

  public TestModel Apply(TestModel currentData, TestEventWithPartitionKey @event) {
    return currentData with { Value = currentData.Value + @event.Delta };
  }
}

// Test global perspective with multiple event types
internal sealed class MultiEventGlobalPerspective :
    IGlobalPerspectiveFor<TestModel, Guid, TestEventWithPartitionKey>,
    IGlobalPerspectiveFor<TestModel, Guid, AnotherTestEventWithPartitionKey> {

  public Guid GetPartitionKey(TestEventWithPartitionKey @event) => @event.PartitionId;
  public Guid GetPartitionKey(AnotherTestEventWithPartitionKey @event) => @event.PartitionId;

  public TestModel Apply(TestModel currentData, TestEventWithPartitionKey @event) {
    return currentData with { Value = currentData.Value + @event.Delta };
  }

  public TestModel Apply(TestModel currentData, AnotherTestEventWithPartitionKey @event) {
    return currentData with { Value = currentData.Value * @event.Multiplier };
  }
}

// Test global perspective with string partition key
internal sealed class StringPartitionPerspective : IGlobalPerspectiveFor<TestModel, string, TestEventWithStringPartition> {
  public string GetPartitionKey(TestEventWithStringPartition @event) {
    return @event.TenantId;
  }

  public TestModel Apply(TestModel currentData, TestEventWithStringPartition @event) {
    return currentData with { Value = @event.Value };
  }
}
