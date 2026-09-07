using Microsoft.Extensions.Logging.Abstractions;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Notifications;
using Whizbang.Core.Observability;
using Whizbang.Data.Postgres.Notifications;

namespace Whizbang.Data.EFCore.Postgres.Tests;

/// <summary>
/// Coverage for <see cref="PgWorkNotificationListener"/>'s <c>"deadletter"</c> payload branch
/// (v0.681 slice 7c). The sibling suite in <c>Whizbang.Core.Tests</c> already locks the
/// <c>"outbox"</c>, <c>"inbox"</c>, <c>"perspective"</c>, <c>"orphan"</c> and unknown-payload cases;
/// this is the one case it never added when the dead-letter-recovery wake-up payload was
/// introduced. No database is used in this file — the listener only parses a string payload and
/// raises a local event.
/// </summary>
/// <code-under-test>src/Whizbang.Data.Postgres/Notifications/PgWorkNotificationListener.cs</code-under-test>
[Category("Shard1")]
public class PgWorkNotificationListenerCoverageTests {

  // DeadLetterRecoveryWorker wakes on this category to scan for dead letters whose recovery
  // policy elapsed. If "deadletter" fell into the unknown-payload branch instead, the AFTER
  // INSERT trigger on wh_dead_letters would fire a notification nobody ever wakes up for — the
  // recovery worker would sit idle until its own slow poll backstop, silently delaying recovery
  // of every newly dead-lettered message.
  [Test]
  public async Task OnNotification_DeadLetterPayload_FiresOnSignalWithDeadLetterReadyAsync() {
    var listener = new PgWorkNotificationListener(
      new _fakeSharedConnection(), new _fakeGate(), new ServiceInstanceProvider(
        Guid.NewGuid(), "coverage-svc", "coverage-host", processId: 1),
      NullLogger<PgWorkNotificationListener>.Instance);
    WorkSignalCategory? received = null;
    listener.OnSignal += category => received = category;

    ((INotifySubscription)listener).OnNotification("deadletter");

    await Assert.That(received).IsEqualTo(WorkSignalCategory.DeadLetterReady)
      .Because("the \"deadletter\" wire payload must route to WorkSignalCategory.DeadLetterReady "
             + "so DeadLetterRecoveryWorker actually wakes on it");
  }

  private sealed class _fakeSharedConnection : ISharedNotifyConnection {
    public IDisposable Subscribe(INotifySubscription subscription) => new _noopDisposable();
    private sealed class _noopDisposable : IDisposable {
      public void Dispose() { }
    }
  }

  private sealed class _fakeGate : INotifySignalingGate {
    public bool IsAvailable => true;
    public DateTimeOffset? LastVerifiedAt => null;
    public DateTimeOffset? LastFailureAt => null;
    public string? LastFailureReason => null;
    public event Action<bool>? OnAvailabilityChanged;
    public Task<bool> ProbeNowAsync(CancellationToken cancellationToken = default) {
      OnAvailabilityChanged?.Invoke(true);
      return Task.FromResult(true);
    }
  }
}
