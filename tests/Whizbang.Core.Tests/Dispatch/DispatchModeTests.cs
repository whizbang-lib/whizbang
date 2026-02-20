using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Dispatch;

#pragma warning disable CA1707 // Identifiers should not contain underscores (test method names use underscores by convention)

namespace Whizbang.Core.Tests.Dispatch;

/// <summary>
/// Tests for DispatchMode enum which defines routing destinations for cascaded messages.
/// </summary>
/// <code-under-test>src/Whizbang.Core/Dispatch/DispatchMode.cs</code-under-test>
public class DispatchModeTests {
  #region Base Flag Values

  [Test]
  public async Task None_HasValue_ZeroAsync() {
    // Arrange
    var mode = DispatchMode.None;

    // Assert
    await Assert.That((int)mode).IsEqualTo(0);
  }

  [Test]
  public async Task LocalDispatch_HasValue_OneAsync() {
    // Arrange
    var mode = DispatchMode.LocalDispatch;

    // Assert - LocalDispatch is the base flag for invoking local receptors
    await Assert.That((int)mode).IsEqualTo(1);
  }

  [Test]
  public async Task Outbox_HasValue_TwoAsync() {
    // Arrange
    var mode = DispatchMode.Outbox;

    // Assert
    await Assert.That((int)mode).IsEqualTo(2);
  }

  [Test]
  public async Task EventStore_HasValue_FourAsync() {
    // Arrange
    var mode = DispatchMode.EventStore;

    // Assert - EventStore is the flag for direct event storage
    await Assert.That((int)mode).IsEqualTo(4);
  }

  #endregion

  #region Composite Mode Values

  [Test]
  public async Task Local_HasValue_FiveAsync() {
    // Arrange
    var mode = DispatchMode.Local;

    // Assert - Local = LocalDispatch | EventStore = 1 | 4 = 5
    await Assert.That((int)mode).IsEqualTo(5);
  }

  [Test]
  public async Task LocalNoPersist_HasValue_OneAsync() {
    // Arrange
    var mode = DispatchMode.LocalNoPersist;

    // Assert - LocalNoPersist = LocalDispatch = 1
    await Assert.That((int)mode).IsEqualTo(1);
  }

  [Test]
  public async Task Both_HasValue_ThreeAsync() {
    // Arrange
    var mode = DispatchMode.Both;

    // Assert - Both = LocalDispatch | Outbox = 1 | 2 = 3
    await Assert.That((int)mode).IsEqualTo(3);
  }

  [Test]
  public async Task EventStoreOnly_HasValue_FourAsync() {
    // Arrange
    var mode = DispatchMode.EventStoreOnly;

    // Assert - EventStoreOnly = EventStore = 4
    await Assert.That((int)mode).IsEqualTo(4);
  }

  #endregion

  #region Composite Mode Flag Combinations

  [Test]
  public async Task Local_IsCombination_OfLocalDispatchAndEventStoreAsync() {
    // Arrange
    var mode = DispatchMode.Local;

    // Assert - Local = LocalDispatch | EventStore
    await Assert.That(mode).IsEqualTo(DispatchMode.LocalDispatch | DispatchMode.EventStore);
  }

  [Test]
  public async Task LocalNoPersist_Equals_LocalDispatchAsync() {
    // Arrange
    var mode = DispatchMode.LocalNoPersist;

    // Assert - LocalNoPersist is just LocalDispatch (old Route.Local behavior)
    await Assert.That(mode).IsEqualTo(DispatchMode.LocalDispatch);
  }

  [Test]
  public async Task Both_IsCombination_OfLocalDispatchAndOutboxAsync() {
    // Arrange
    var mode = DispatchMode.Both;

    // Assert - Both = LocalDispatch | Outbox
    await Assert.That(mode).IsEqualTo(DispatchMode.LocalDispatch | DispatchMode.Outbox);
  }

  [Test]
  public async Task EventStoreOnly_Equals_EventStoreAsync() {
    // Arrange
    var mode = DispatchMode.EventStoreOnly;

    // Assert - EventStoreOnly = EventStore (storage without local dispatch)
    await Assert.That(mode).IsEqualTo(DispatchMode.EventStore);
  }

  [Test]
  public async Task LocalDispatchOrEventStore_Equals_LocalAsync() {
    // Arrange
    var combined = DispatchMode.LocalDispatch | DispatchMode.EventStore;

    // Assert
    await Assert.That(combined).IsEqualTo(DispatchMode.Local);
  }

  [Test]
  public async Task LocalDispatchOrOutbox_Equals_BothAsync() {
    // Arrange
    var combined = DispatchMode.LocalDispatch | DispatchMode.Outbox;

    // Assert
    await Assert.That(combined).IsEqualTo(DispatchMode.Both);
  }

  #endregion

  #region HasFlag Tests - LocalDispatch Flag

  [Test]
  public async Task Local_HasFlag_LocalDispatchAsync() {
    // Arrange
    var mode = DispatchMode.Local;

    // Assert - Local includes LocalDispatch
    await Assert.That(mode.HasFlag(DispatchMode.LocalDispatch)).IsTrue();
  }

  [Test]
  public async Task LocalNoPersist_HasFlag_LocalDispatchAsync() {
    // Arrange
    var mode = DispatchMode.LocalNoPersist;

    // Assert
    await Assert.That(mode.HasFlag(DispatchMode.LocalDispatch)).IsTrue();
  }

  [Test]
  public async Task Both_HasFlag_LocalDispatchAsync() {
    // Arrange
    var mode = DispatchMode.Both;

    // Assert
    await Assert.That(mode.HasFlag(DispatchMode.LocalDispatch)).IsTrue();
  }

  [Test]
  public async Task EventStoreOnly_DoesNotHaveFlag_LocalDispatchAsync() {
    // Arrange
    var mode = DispatchMode.EventStoreOnly;

    // Assert - EventStoreOnly doesn't invoke local receptors
    await Assert.That(mode.HasFlag(DispatchMode.LocalDispatch)).IsFalse();
  }

  [Test]
  public async Task Outbox_DoesNotHaveFlag_LocalDispatchAsync() {
    // Arrange
    var mode = DispatchMode.Outbox;

    // Assert
    await Assert.That(mode.HasFlag(DispatchMode.LocalDispatch)).IsFalse();
  }

  #endregion

  #region HasFlag Tests - EventStore Flag

  [Test]
  public async Task Local_HasFlag_EventStoreAsync() {
    // Arrange
    var mode = DispatchMode.Local;

    // Assert - Local includes EventStore
    await Assert.That(mode.HasFlag(DispatchMode.EventStore)).IsTrue();
  }

  [Test]
  public async Task EventStoreOnly_HasFlag_EventStoreAsync() {
    // Arrange
    var mode = DispatchMode.EventStoreOnly;

    // Assert
    await Assert.That(mode.HasFlag(DispatchMode.EventStore)).IsTrue();
  }

  [Test]
  public async Task LocalNoPersist_DoesNotHaveFlag_EventStoreAsync() {
    // Arrange
    var mode = DispatchMode.LocalNoPersist;

    // Assert - LocalNoPersist doesn't persist to event store
    await Assert.That(mode.HasFlag(DispatchMode.EventStore)).IsFalse();
  }

  [Test]
  public async Task Both_DoesNotHaveFlag_EventStoreAsync() {
    // Arrange
    var mode = DispatchMode.Both;

    // Assert - Both goes through outbox (which handles event storage)
    await Assert.That(mode.HasFlag(DispatchMode.EventStore)).IsFalse();
  }

  [Test]
  public async Task Outbox_DoesNotHaveFlag_EventStoreAsync() {
    // Arrange
    var mode = DispatchMode.Outbox;

    // Assert - Outbox handles event storage via process_work_batch
    await Assert.That(mode.HasFlag(DispatchMode.EventStore)).IsFalse();
  }

  #endregion

  #region HasFlag Tests - Outbox Flag

  [Test]
  public async Task Both_HasFlag_OutboxAsync() {
    // Arrange
    var mode = DispatchMode.Both;

    // Assert
    await Assert.That(mode.HasFlag(DispatchMode.Outbox)).IsTrue();
  }

  [Test]
  public async Task Outbox_HasFlag_OutboxAsync() {
    // Arrange
    var mode = DispatchMode.Outbox;

    // Assert
    await Assert.That(mode.HasFlag(DispatchMode.Outbox)).IsTrue();
  }

  [Test]
  public async Task Local_DoesNotHaveFlag_OutboxAsync() {
    // Arrange
    var mode = DispatchMode.Local;

    // Assert - Local doesn't use outbox transport
    await Assert.That(mode.HasFlag(DispatchMode.Outbox)).IsFalse();
  }

  [Test]
  public async Task LocalNoPersist_DoesNotHaveFlag_OutboxAsync() {
    // Arrange
    var mode = DispatchMode.LocalNoPersist;

    // Assert
    await Assert.That(mode.HasFlag(DispatchMode.Outbox)).IsFalse();
  }

  [Test]
  public async Task EventStoreOnly_DoesNotHaveFlag_OutboxAsync() {
    // Arrange
    var mode = DispatchMode.EventStoreOnly;

    // Assert
    await Assert.That(mode.HasFlag(DispatchMode.Outbox)).IsFalse();
  }

  #endregion

  #region HasFlag Tests - None Mode

  [Test]
  public async Task None_DoesNotHaveFlag_LocalDispatchAsync() {
    // Arrange
    var mode = DispatchMode.None;

    // Assert
    await Assert.That(mode.HasFlag(DispatchMode.LocalDispatch)).IsFalse();
  }

  [Test]
  public async Task None_DoesNotHaveFlag_OutboxAsync() {
    // Arrange
    var mode = DispatchMode.None;

    // Assert
    await Assert.That(mode.HasFlag(DispatchMode.Outbox)).IsFalse();
  }

  [Test]
  public async Task None_DoesNotHaveFlag_EventStoreAsync() {
    // Arrange
    var mode = DispatchMode.None;

    // Assert
    await Assert.That(mode.HasFlag(DispatchMode.EventStore)).IsFalse();
  }

  #endregion
}
