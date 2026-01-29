using TUnit.Core;
using Whizbang.Core;
using Whizbang.Core.SystemEvents;

namespace Whizbang.Core.Tests.SystemEvents;

/// <summary>
/// Tests for ISystemEvent marker interface and base system event types.
/// System events are internal Whizbang events for audit, monitoring, and operations.
/// </summary>
public class ISystemEventTests {
  [Test]
  public async Task ISystemEvent_ExtendsIEvent_ForEventStoreCompatibilityAsync() {
    // Arrange & Act - Verify interface hierarchy
    var isEvent = typeof(ISystemEvent).IsAssignableTo(typeof(IEvent));

    // Assert
    await Assert.That(isEvent).IsTrue();
  }

  [Test]
  public async Task SystemEventStreamName_IsConstant_ForDedicatedStreamAsync() {
    // Arrange & Act
    var streamName = SystemEventStreams.Name;

    // Assert - Dedicated system stream with $ prefix (convention for system streams)
    await Assert.That(streamName).IsEqualTo("$wb-system");
  }
}
