// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using System.Transactions;
using Dapper;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Npgsql;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Notifications;
using Whizbang.Core.Notifications.AppSignals;
using Whizbang.Core.Observability;
using Whizbang.Core.Signals;
using Whizbang.Data.Postgres.Notifications;

namespace Whizbang.Data.Dapper.Postgres.Tests;

/// <summary>
/// #2076 on the Dapper path. Inside a <see cref="TransactionScope"/> an application signal is delivered only when the
/// scope commits, and never when it rolls back, exactly as on EF Core. A transaction a caller begins on a connection of
/// its own (<see cref="NpgsqlConnection.BeginTransaction()"/>) is not tracked by the framework, so a publish made inside
/// one is sent at once, as before: the documented boundary, locked here.
/// </summary>
/// <docs>fundamentals/work-coordinator/app-signals#transactional-publish</docs>
public class DapperAppSignalTransactionTests : PostgresTestBase {
  private const string TOPIC = "stock_changed";
  private const string SENTINEL = "sentinel";

  private (IAppSignalChannel Channel, PostgresSignalTransport Transport) _channel() {
    var options = Options.Create(new WhizbangNotificationOptions { DirectConnectionString = ConnectionString });
    var configuration = new ConfigurationBuilder().AddInMemoryCollection([]).Build();
    var instance = new ServiceInstanceProvider(configuration);
    var shared = new PgSharedNotifyConnection(options, configuration, instance, NullLogger<PgSharedNotifyConnection>.Instance);
    var transport = new PostgresSignalTransport(options, configuration, shared, instance, NullLogger<PostgresSignalTransport>.Instance);
    return (new SignalBus([transport], []), transport);
  }

  [Test]
  public async Task InsideATransactionScope_IsHeardOnlyAfterCommitAsync(CancellationToken cancellationToken) {
    await using var probe = await AppSignalNotifyProbe.ListenAsync(ConnectionString, TOPIC, cancellationToken);
    var (channel, transport) = _channel();
    using var ownedTransport = transport;

    using (var ambient = new TransactionScope(TransactionScopeOption.Required, TransactionScopeAsyncFlowOption.Enabled)) {
      await using (var connection = new NpgsqlConnection(ConnectionString)) {
        await connection.OpenAsync(cancellationToken);
        _ = await connection.ExecuteScalarAsync<int>("SELECT 1");
        await channel.PublishAsync(TOPIC, "scoped", cancellationToken);
      }
      await probe.SendSentinelAsync(SENTINEL, cancellationToken);
      await Assert.That(await probe.NextAsync(cancellationToken)).IsEqualTo(SENTINEL)
        .Because("inside a TransactionScope the notification waits for the scope to commit");
      ambient.Complete();
    }

    await Assert.That(await probe.NextAsync(cancellationToken)).IsEqualTo("scoped");
  }

  [Test]
  public async Task InsideARolledBackTransactionScope_IsNeverHeardAsync(CancellationToken cancellationToken) {
    await using var probe = await AppSignalNotifyProbe.ListenAsync(ConnectionString, TOPIC, cancellationToken);
    var (channel, transport) = _channel();
    using var ownedTransport = transport;
    var settled = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
    transport.DeferredTopicSettled = (_, delivered) => settled.TrySetResult(delivered);

    using (new TransactionScope(TransactionScopeOption.Required, TransactionScopeAsyncFlowOption.Enabled)) {
      await using var connection = new NpgsqlConnection(ConnectionString);
      await connection.OpenAsync(cancellationToken);
      _ = await connection.ExecuteScalarAsync<int>("SELECT 1");
      await channel.PublishAsync(TOPIC, "rolled-back", cancellationToken);
    }

    await Assert.That(await settled.Task.WaitAsync(TimeSpan.FromSeconds(15), cancellationToken)).IsFalse();
    await probe.SendSentinelAsync(SENTINEL, cancellationToken);
    await Assert.That(await probe.NextAsync(cancellationToken)).IsEqualTo(SENTINEL)
      .Because("a change that rolled back must not announce itself");
  }

  [Test]
  public async Task InsideAConnectionsOwnTransaction_IsSentAtOnce_TheDocumentedBoundaryAsync(CancellationToken cancellationToken) {
    await using var probe = await AppSignalNotifyProbe.ListenAsync(ConnectionString, TOPIC, cancellationToken);
    var (channel, transport) = _channel();
    using var ownedTransport = transport;
    await using var connection = new NpgsqlConnection(ConnectionString);
    await connection.OpenAsync(cancellationToken);
    await using var tx = await connection.BeginTransactionAsync(cancellationToken);

    await channel.PublishAsync(TOPIC, "untracked", cancellationToken);

    await Assert.That(await probe.NextAsync(cancellationToken)).IsEqualTo("untracked")
      .Because("the framework does not see a transaction begun on a caller's own connection, so it cannot wait for it");
    await tx.RollbackAsync(cancellationToken);
  }

  [Test]
  public async Task WithNoTransaction_IsHeardAtOnceAsync(CancellationToken cancellationToken) {
    await using var probe = await AppSignalNotifyProbe.ListenAsync(ConnectionString, TOPIC, cancellationToken);
    var (channel, transport) = _channel();
    using var ownedTransport = transport;

    await channel.PublishAsync(TOPIC, "now", cancellationToken);

    await Assert.That(await probe.NextAsync(cancellationToken)).IsEqualTo("now");
  }
}
