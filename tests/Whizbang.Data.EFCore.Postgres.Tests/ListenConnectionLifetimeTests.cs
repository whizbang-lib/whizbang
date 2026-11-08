// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Data.Postgres.Notifications;

namespace Whizbang.Data.EFCore.Postgres.Tests;

/// <summary>
/// One lifetime of the shared LISTEN connection. A stop closes it to deliveries unless one is
/// running, and it disposes the connection at once when nothing reads from it, but only after the
/// wait ends when a stop left a wait still reading. Disposing under the read would put a connection
/// with a read in flight back into Npgsql's pool; never disposing it would leak the session, and with
/// it the instance's alive-lock.
/// </summary>
/// <docs>fundamentals/workers/instance-liveness</docs>
/// <code-under-test>src/Whizbang.Data.Postgres/Notifications/ListenConnectionLifetime.cs</code-under-test>
[Category("Shard1")]
public class ListenConnectionLifetimeTests {
  [Test]
  public async Task Deliver_BeforeAnyStop_ReachesTheDispatchAsync() {
    var delivered = new List<(string Channel, string Payload)>();
    var lifetime = new ListenConnectionLifetime(new RecordingDisposable(), (channel, payload) => delivered.Add((channel, payload)));

    lifetime.Deliver("wh_channel", "outbox");

    await Assert.That(delivered).IsEquivalentTo([("wh_channel", "outbox")]);
  }

  [Test]
  public async Task Close_WithNoDeliveryRunning_IsGrantedAndLaterDeliveriesAreDroppedAsync() {
    var deliveries = 0;
    var lifetime = new ListenConnectionLifetime(new RecordingDisposable(), (_, _) => deliveries++);

    var closed = lifetime.TryClose();
    lifetime.Deliver("wh_channel", "outbox");

    await Assert.That(closed).IsTrue();
    await Assert.That(deliveries).IsEqualTo(0)
      .Because("a notification an abandoned wait still reads must reach no subscriber after the stop");
  }

  [Test]
  public async Task Close_DuringADelivery_IsRefusedAndTheDeliveryCompletesAsync() {
    bool? closedDuringDelivery = null;
    ListenConnectionLifetime? lifetime = null;
    lifetime = new ListenConnectionLifetime(new RecordingDisposable(), (_, _) => closedDuringDelivery = lifetime!.TryClose());

    lifetime.Deliver("wh_channel", "outbox");
    var closedAfterwards = lifetime.TryClose();

    await Assert.That(closedDuringDelivery).IsEqualTo(false)
      .Because("a stop must not finish in the middle of a delivery; it waits for the wait instead");
    await Assert.That(closedAfterwards).IsTrue()
      .Because("a delivery that threw or returned no longer holds the lifetime open");
  }

  [Test]
  public async Task Deliver_TheDispatchThrows_StillReleasesTheLifetimeForCloseAsync() {
    var lifetime = new ListenConnectionLifetime(
      new RecordingDisposable(), (_, _) => throw new InvalidOperationException("subscriber failed"));

    await Assert.That(() => lifetime.Deliver("wh_channel", "outbox")).Throws<InvalidOperationException>();

    await Assert.That(lifetime.TryClose()).IsTrue()
      .Because("a failed delivery that still counted as running would keep every later stop waiting on it");
  }

  [Test]
  public async Task Dispose_NoWaitEverParked_DisposesTheConnectionAtOnceAsync() {
    var connection = new RecordingDisposable();
    var lifetime = new ListenConnectionLifetime(connection, (_, _) => { });

    await lifetime.DisposeAsync();

    await Assert.That(connection.DisposeCount).IsEqualTo(1)
      .Because("a lifetime that never waited has nothing to defer behind");
  }

  [Test]
  public async Task Dispose_TheWaitHasEnded_DisposesTheConnectionAtOnceAsync() {
    var connection = new RecordingDisposable();
    var lifetime = new ListenConnectionLifetime(connection, (_, _) => { }) { PendingWait = Task.CompletedTask };

    await lifetime.DisposeAsync();

    await Assert.That(connection.DisposeCount).IsEqualTo(1)
      .Because("the usual stop: the wait saw its cancellation, so nothing reads from the connection");
  }

  [Test]
  public async Task Dispose_TheWaitIsStillReading_DisposesOnlyOnceItEndsAsync(CancellationToken cancellationToken) {
    var connection = new RecordingDisposable();
    var wait = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    var lifetime = new ListenConnectionLifetime(connection, (_, _) => { }) { PendingWait = wait.Task };

    await lifetime.DisposeAsync();
    var disposedWhileReading = connection.DisposeCount;
    // The connection closing under the wait is how a missed stop's wait ends; it ends in failure.
    wait.SetException(new InvalidOperationException("the connection closed under the wait"));
    await lifetime.Disposed.WaitAsync(cancellationToken);

    await Assert.That(disposedWhileReading).IsEqualTo(0)
      .Because("disposing under a read in flight would hand the read's connection back to the pool");
    await Assert.That(connection.DisposeCount).IsEqualTo(1)
      .Because("once the wait ends, however it ends, the connection must still be released");
  }

  private sealed class RecordingDisposable : IAsyncDisposable {
    public int DisposeCount { get; private set; }

    public ValueTask DisposeAsync() {
      DisposeCount++;
      return ValueTask.CompletedTask;
    }
  }
}
