// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using Npgsql;
using Whizbang.Core.Notifications;

namespace Whizbang.Data.Postgres.Notifications;

/// <summary>
/// One connection lifetime of <see cref="PgSharedNotifyConnection"/>'s LISTEN connection: delivers
/// its notifications to the subscribers, and disposes it only once no wait is still reading from it.
/// </summary>
/// <param name="connection">The connection this lifetime owns.</param>
/// <param name="dispatch">Routes one notification (channel, payload) to its subscribers.</param>
/// <remarks>
/// <para>
/// A stop cannot rely on the dispatch loop's wait noticing it, because Npgsql's wait can miss its
/// cancellation and then sit on an idle socket until the server sends something (see
/// <c>PgSharedNotifyConnection._listenOnceAsync</c>). When no delivery is running, the stop closes
/// this lifetime to deliveries and leaves with the wait still reading. Two things then hold. No
/// subscriber is called after the stop, because a notification the abandoned wait still reads is
/// dropped here. And the connection is not disposed under the read, which would hand a connection
/// with a read in flight back to Npgsql's pool: disposal waits for the wait to end, which is when
/// the connection closes under it (the process exits, or the server drops the session).
/// </para>
/// <para>
/// When a delivery is running, the stop does not close: the wait returns as soon as the callback
/// does, so the stop waits for that instead and never finishes in the middle of a delivery.
/// </para>
/// </remarks>
/// <docs>fundamentals/workers/instance-liveness</docs>
/// <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/ListenConnectionLifetimeTests.cs:Dispose_TheWaitIsStillReading_DisposesOnlyOnceItEndsAsync</tests>
/// <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/ListenConnectionLifetimeTests.cs:Close_DuringADelivery_IsRefusedAndTheDeliveryCompletesAsync</tests>
/// <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/PgSharedNotifyConnectionCoverageTests.cs:Stop_WhileTheWaitMissesItsCancellation_StillCompletesAsync</tests>
internal sealed class ListenConnectionLifetime(IAsyncDisposable connection, Action<string, string> dispatch)
    : IAsyncDisposable {
  private readonly Lock _deliveryGate = new();
  private int _deliveriesInFlight;
  private bool _closed;

  /// <summary>
  /// The wait currently reading from the connection, if any. Set by the dispatch loop each time it
  /// parks.
  /// </summary>
  public Task? PendingWait { get; set; }

  /// <summary>
  /// Completes once the connection has been disposed: at once when nothing was reading from it,
  /// otherwise after the wait that was.
  /// </summary>
  public Task Disposed { get; private set; } = Task.CompletedTask;

  /// <summary>
  /// The connection's <see cref="NpgsqlConnection.Notification"/> handler. Runs synchronously on
  /// Npgsql's wait, so subscribers must respect the <see cref="INotifySubscription.OnNotification"/>
  /// contract (fast and non-blocking); a slow subscriber holds up the notifications behind it.
  /// </summary>
  public void OnNotification(object? sender, NpgsqlNotificationEventArgs e) => Deliver(e.Channel, e.Payload);

  /// <summary>
  /// Delivers one notification, unless the lifetime has been closed by a stop.
  /// </summary>
  /// <param name="channel">The channel it arrived on.</param>
  /// <param name="payload">Its payload.</param>
  public void Deliver(string channel, string payload) {
    lock (_deliveryGate) {
      if (_closed) {
        return;
      }
      _deliveriesInFlight++;
    }
    try {
      dispatch(channel, payload);
    } finally {
      lock (_deliveryGate) {
        _deliveriesInFlight--;
      }
    }
  }

  /// <summary>
  /// Closes the lifetime to deliveries for a stop, unless one is running.
  /// </summary>
  /// <returns>
  /// True when closed: no subscriber is called from this connection again. False when a delivery is
  /// running; the wait it runs inside returns once it finishes.
  /// </returns>
  public bool TryClose() {
    lock (_deliveryGate) {
      if (_deliveriesInFlight > 0) {
        return false;
      }
      _closed = true;
      return true;
    }
  }

  /// <inheritdoc />
  public ValueTask DisposeAsync() {
    var pending = PendingWait;
    // No wait ever parked, or the one that did has ended: nothing reads from the connection.
    if (pending?.IsCompleted != false) {
      return connection.DisposeAsync();
    }
    Disposed = _disposeAfterAsync(pending, connection);
    return ValueTask.CompletedTask;
  }

  private static async Task _disposeAfterAsync(Task pending, IAsyncDisposable connection) {
    // How the wait ended does not matter here: it is the stop the loop already acted on, or the
    // connection failing under it. Either way nothing reads from the connection any more.
    await pending.ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
    await connection.DisposeAsync().ConfigureAwait(false);
  }
}
