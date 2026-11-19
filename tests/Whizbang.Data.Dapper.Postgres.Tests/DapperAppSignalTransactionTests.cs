// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using System.Transactions;
using Dapper;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
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

  /// <summary>Records each log entry's level, message and exception.</summary>
  private sealed class CapturingLogger<T> : ILogger<T> {
    public List<(LogLevel Level, string Message, Exception? Exception)> Entries { get; } = [];
    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
    public bool IsEnabled(LogLevel logLevel) => true;
    public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) {
      lock (Entries) {
        Entries.Add((logLevel, formatter(state, exception), exception));
      }
    }
  }

  private (IAppSignalChannel Channel, PostgresSignalTransport Transport) _channel(ILogger<PostgresSignalTransport>? logger = null) {
    var options = Options.Create(new WhizbangNotificationOptions { DirectConnectionString = ConnectionString });
    var configuration = new ConfigurationBuilder().AddInMemoryCollection([]).Build();
    var instance = new ServiceInstanceProvider(configuration);
    var shared = new PgSharedNotifyConnection(options, configuration, instance, NullLogger<PgSharedNotifyConnection>.Instance);
    var transport = new PostgresSignalTransport(options, configuration, shared, instance, logger ?? NullLogger<PostgresSignalTransport>.Instance);
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

  [Test]
  public async Task AnOversizePayload_InsideATransactionScope_DoesNotFailTheScope_TheSendAfterCommitFailsAndIsLoggedAsync(CancellationToken cancellationToken) {
    // Unlike a DbContext transaction, a TransactionScope's signal is sent after the scope commits, on a connection of its
    // own: an oversize payload cannot fail the caller's transaction, which commits; the send is then rejected, logged
    // with its channel, and nothing is delivered.
    await using var probe = await AppSignalNotifyProbe.ListenAsync(ConnectionString, TOPIC, cancellationToken);
    var logger = new CapturingLogger<PostgresSignalTransport>();
    var (channel, transport) = _channel(logger);
    using var ownedTransport = transport;
    var settled = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
    transport.DeferredTopicSettled = (_, delivered) => settled.TrySetResult(delivered);
    await using (var setup = new NpgsqlConnection(ConnectionString)) {
      await setup.OpenAsync(cancellationToken);
      _ = await setup.ExecuteAsync("CREATE TABLE app_signal_oversize_work (id int PRIMARY KEY)");
    }

    using (var ambient = new TransactionScope(TransactionScopeOption.Required, TransactionScopeAsyncFlowOption.Enabled)) {
      await using (var connection = new NpgsqlConnection(ConnectionString)) {
        await connection.OpenAsync(cancellationToken);
        _ = await connection.ExecuteAsync("INSERT INTO app_signal_oversize_work (id) VALUES (1)");
        await channel.PublishAsync(TOPIC, new string('x', 8000), cancellationToken);
      }
      ambient.Complete();
    }

    await Assert.That(await settled.Task.WaitAsync(TimeSpan.FromSeconds(15), cancellationToken)).IsFalse()
      .Because("the send after the commit is rejected by PostgreSQL");
    await using (var check = new NpgsqlConnection(ConnectionString)) {
      await check.OpenAsync(cancellationToken);
      await Assert.That(await check.ExecuteScalarAsync<long>("SELECT count(*) FROM app_signal_oversize_work")).IsEqualTo(1L)
        .Because("the caller's work committed: the signal is sent only after the commit, so it cannot fail it");
    }
    List<(LogLevel Level, string Message, Exception? Exception)> entries;
    lock (logger.Entries) {
      entries = [.. logger.Entries];
    }
    await Assert.That(entries.Any(e => e.Level == LogLevel.Warning && e.Message.Contains("wh_app_" + TOPIC, StringComparison.Ordinal)
        && e.Exception is PostgresException { SqlState: PostgresErrorCodes.InvalidParameterValue })).IsTrue()
      .Because("a send that fails where nobody awaits it must say what failed");
    await probe.SendSentinelAsync(SENTINEL, cancellationToken);
    await Assert.That(await probe.NextAsync(cancellationToken)).IsEqualTo(SENTINEL);
  }
}
