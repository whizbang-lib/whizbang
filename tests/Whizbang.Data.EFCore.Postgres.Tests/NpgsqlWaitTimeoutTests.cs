// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using Npgsql;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Data.Postgres.Notifications;

namespace Whizbang.Data.EFCore.Postgres.Tests;

/// <summary>
/// The Npgsql behavior the shared LISTEN connection's keepalive rests on (#2155): a wait given a
/// finite timeout returns false when it elapses, and leaves the connection open and usable, so the
/// same session goes on to run commands and to receive later notifications.
/// </summary>
/// <remarks>
/// <see cref="PgSharedNotifyConnection"/> bounds every wait with the keepalive interval because a
/// token cancel can be lost inside Npgsql's wait, while its timeout is armed afresh on every read
/// and cannot be. Should an Npgsql upgrade turn a timed-out wait into a broken connector, every
/// keepalive would recycle the LISTEN connection, and these tests fail first.
/// </remarks>
/// <docs>fundamentals/workers/instance-liveness</docs>
/// <code-under-test>src/Whizbang.Data.Postgres/Notifications/PgSharedNotifyConnection.cs</code-under-test>
[Category("Shard1")]
public class NpgsqlWaitTimeoutTests : EFCoreTestBase {
  private static readonly TimeSpan _shortTimeout = TimeSpan.FromMilliseconds(200);
  private static readonly TimeSpan _signalDeadline = TimeSpan.FromSeconds(15);

  [Test]
  [Timeout(60000)]
  public async Task WaitAsync_TimeoutElapses_ReturnsFalseAndLeavesTheSameSessionOpenAsync(
      CancellationToken cancellationToken) {
    await using var listener = new NpgsqlConnection(ConnectionString);
    await listener.OpenAsync(cancellationToken);
    var backendBefore = listener.ProcessID;

    var first = await listener.WaitAsync(_shortTimeout, cancellationToken);
    var second = await listener.WaitAsync(_shortTimeout, cancellationToken);
    await using var ping = new NpgsqlCommand("SELECT pg_backend_pid()", listener);
    var backendAfter = (int)(await ping.ExecuteScalarAsync(cancellationToken))!;

    await Assert.That(first).IsFalse().Because("a wait that times out reports it, it does not throw");
    await Assert.That(second).IsFalse().Because("a timed-out wait can be followed by another wait");
    await Assert.That(listener.State).IsEqualTo(System.Data.ConnectionState.Open)
      .Because("a timeout must not break the connector, or every keepalive would recycle the connection");
    await Assert.That(backendAfter).IsEqualTo(backendBefore)
      .Because("the command after the timeout ran on the same server session, not a reopened one");
  }

  [Test]
  [Timeout(60000)]
  public async Task WaitAsync_NotificationAfterATimeout_ArrivesOnTheSameConnectionAsync(
      CancellationToken cancellationToken) {
    var channel = $"wh_wait_timeout_{Guid.NewGuid():N}";
    await using var listener = new NpgsqlConnection(ConnectionString);
    await listener.OpenAsync(cancellationToken);
    var received = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
    listener.Notification += (_, e) => received.TrySetResult(e.Payload);
    await using (var listen = new NpgsqlCommand($"LISTEN \"{channel}\"", listener)) {
      await listen.ExecuteNonQueryAsync(cancellationToken);
    }

    var timedOut = await listener.WaitAsync(_shortTimeout, cancellationToken);
    await using (var notifier = new NpgsqlConnection(ConnectionString)) {
      await notifier.OpenAsync(cancellationToken);
      await using var notify = new NpgsqlCommand("SELECT pg_notify(@channel, 'after-timeout')", notifier);
      notify.Parameters.AddWithValue("channel", channel);
      await notify.ExecuteNonQueryAsync(cancellationToken);
    }
    var arrived = await listener.WaitAsync(_signalDeadline, cancellationToken);
    var payload = await received.Task.WaitAsync(_signalDeadline, cancellationToken);

    await Assert.That(timedOut).IsFalse();
    await Assert.That(arrived).IsTrue()
      .Because("the LISTEN survives a timed-out wait, so a later notification still reaches this session");
    await Assert.That(payload).IsEqualTo("after-timeout");
  }
}
