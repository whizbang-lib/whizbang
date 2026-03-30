using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Messaging;
using Whizbang.Core.Observability;
using Whizbang.Core.SystemEvents;
using Whizbang.Core.ValueObjects;
using Whizbang.Core.Dispatch;

namespace Whizbang.Core.Tests.SystemEvents;

/// <summary>
/// Tests for NullSystemEventEmitter - verifies all no-op methods complete successfully.
/// </summary>
[Category("SystemEvents")]
public class NullSystemEventEmitterTests {
  [Test]
  public async Task EmitEventAuditedAsync_CompletesSuccessfully_ReturnsCompletedTaskAsync() {
    // Arrange
    var emitter = new NullSystemEventEmitter();
    var envelope = new MessageEnvelope<string> {
      MessageId = MessageId.New(),
      Payload = "test",
      Hops = [],
      DispatchContext = new MessageDispatchContext { Mode = DispatchModes.Local, Source = MessageSource.Local }
    };

    // Act
    await emitter.EmitEventAuditedAsync(Guid.NewGuid(), 1, envelope);

    // Assert - Method completed without error (no-op)
    var completed = true;
    await Assert.That(completed).IsTrue();
  }

  [Test]
  public async Task EmitCommandAuditedAsync_CompletesSuccessfully_ReturnsCompletedTaskAsync() {
    // Arrange
    var emitter = new NullSystemEventEmitter();

    // Act
    await emitter.EmitCommandAuditedAsync("command", "response", "TestReceptor", null);

    // Assert - Method completed without error (no-op)
    var completed = true;
    await Assert.That(completed).IsTrue();
  }

  [Test]
  public async Task EmitAsync_CompletesSuccessfully_ReturnsCompletedTaskAsync() {
    // Arrange
    var emitter = new NullSystemEventEmitter();
    var systemEvent = new EventAudited {
      Id = Guid.NewGuid(),
      OriginalEventType = "Test",
      OriginalStreamId = "stream-1",
      OriginalStreamPosition = 1,
      OriginalBody = default,
      Timestamp = DateTimeOffset.UtcNow
    };

    // Act
    await emitter.EmitAsync(systemEvent);

    // Assert - Method completed without error (no-op)
    var completed = true;
    await Assert.That(completed).IsTrue();
  }

  [Test]
  public async Task ShouldExcludeFromAudit_AlwaysReturnsFalseAsync() {
    // Arrange
    var emitter = new NullSystemEventEmitter();

    // Act
    var result = emitter.ShouldExcludeFromAudit(typeof(string));

    // Assert - NullSystemEventEmitter never excludes anything
    await Assert.That(result).IsFalse();
  }

  [Test]
  public async Task ShouldExcludeFromAudit_WithEventAuditedType_StillReturnsFalseAsync() {
    // Arrange
    var emitter = new NullSystemEventEmitter();

    // Act
    var result = emitter.ShouldExcludeFromAudit(typeof(EventAudited));

    // Assert - Even for excluded types, NullSystemEventEmitter always returns false
    await Assert.That(result).IsFalse();
  }
}
