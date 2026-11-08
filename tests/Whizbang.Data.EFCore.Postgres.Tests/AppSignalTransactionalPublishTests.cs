// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using System.Data.Common;
using System.Transactions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
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
using Whizbang.Data.EFCore.Postgres.Tests.Generated;
using Whizbang.Data.Postgres.Notifications;

namespace Whizbang.Data.EFCore.Postgres.Tests;

/// <summary>
/// #2076: an application signal published while the caller is inside a transaction is delivered only when that
/// transaction commits, and never when it rolls back. A publish through a scope's channel inside that scope's DbContext
/// transaction is sent on the transaction's own connection; a publish inside a <see cref="TransactionScope"/> is sent
/// once the scope commits. Everything else (no transaction, a channel resolved from the root or held by a singleton) is
/// sent at once, as before.
/// </summary>
/// <remarks>
/// Every negative is deterministic: a sentinel sent on the same channel from another connection is read first only if the
/// notification under test was not delivered before it, because notifications arrive in commit order.
/// </remarks>
/// <docs>fundamentals/work-coordinator/app-signals#transactional-publish</docs>
[Category("Shard4")]
public class AppSignalTransactionalPublishTests : EFCoreTestBase {
  private const string TOPIC = "orders_changed";
  private const string SENTINEL = "sentinel";

  private sealed class SingletonPublisher(IAppSignalChannel channel) {
    public IAppSignalChannel Channel { get; } = channel;
  }

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

  /// <summary>A transaction of another provider: the Postgres transport cannot send on it.</summary>
  private sealed class ForeignTransaction : DbTransaction {
    public override System.Data.IsolationLevel IsolationLevel => System.Data.IsolationLevel.ReadCommitted;
    protected override DbConnection? DbConnection => null;
    public override void Commit() { }
    public override void Rollback() { }
  }

  private PostgresSignalTransport _transport(string? connectionString = null, ILogger<PostgresSignalTransport>? logger = null) {
    var options = Options.Create(new WhizbangNotificationOptions { DirectConnectionString = connectionString ?? ConnectionString });
    var configuration = new ConfigurationBuilder().AddInMemoryCollection([]).Build();
    var instance = new ServiceInstanceProvider(configuration);
    // Publishing opens connections of its own (or uses the caller's), so a never-started shared connection is enough.
    var shared = new PgSharedNotifyConnection(options, configuration, instance, NullLogger<PgSharedNotifyConnection>.Instance);
    return new PostgresSignalTransport(options, configuration, shared, instance, logger ?? NullLogger<PostgresSignalTransport>.Instance);
  }

  /// <summary>A host's composition: the bus over the Postgres transport, the per-scope channel, and the framework context.</summary>
  private ServiceProvider _host(PostgresSignalTransport transport, Action<IServiceCollection>? extra = null) {
    var services = new ServiceCollection();
    services.AddSingleton(new SignalBus([transport], []));
    services.AddWhizbangAppSignalChannel();
    services.AddScoped(_ => CreateDbContext());
    EFCoreAppSignalTransactionSource.Register(services, typeof(WorkCoordinationDbContext));
    extra?.Invoke(services);
    return services.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true, ValidateOnBuild = true });
  }

  [Test]
  public async Task InsideADbContextTransaction_IsHeardOnlyAfterCommitAsync(CancellationToken cancellationToken) {
    await using var probe = await AppSignalNotifyProbe.ListenAsync(ConnectionString, TOPIC, cancellationToken);
    using var transport = _transport();
    await using var provider = _host(transport);
    await using var scope = provider.CreateAsyncScope();
    var db = scope.ServiceProvider.GetRequiredService<WorkCoordinationDbContext>();
    await using var tx = await db.Database.BeginTransactionAsync(cancellationToken);

    await scope.ServiceProvider.GetRequiredService<IAppSignalChannel>().PublishAsync(TOPIC, "inside", cancellationToken);
    await probe.SendSentinelAsync(SENTINEL, cancellationToken);

    await Assert.That(await probe.NextAsync(cancellationToken)).IsEqualTo(SENTINEL)
      .Because("a subscriber must hear nothing before the caller commits, or it re-reads the old state and misses the change");
    await tx.CommitAsync(cancellationToken);
    await Assert.That(await probe.NextAsync(cancellationToken)).IsEqualTo("inside");
  }

  [Test]
  public async Task InsideARolledBackDbContextTransaction_IsNeverHeardAsync(CancellationToken cancellationToken) {
    await using var probe = await AppSignalNotifyProbe.ListenAsync(ConnectionString, TOPIC, cancellationToken);
    using var transport = _transport();
    await using var provider = _host(transport);
    await using var scope = provider.CreateAsyncScope();
    var db = scope.ServiceProvider.GetRequiredService<WorkCoordinationDbContext>();
    await using (var tx = await db.Database.BeginTransactionAsync(cancellationToken)) {
      await scope.ServiceProvider.GetRequiredService<IAppSignalChannel>().PublishAsync(TOPIC, "rolled-back", cancellationToken);
      await tx.RollbackAsync(cancellationToken);
    }

    await probe.SendSentinelAsync(SENTINEL, cancellationToken);

    await Assert.That(await probe.NextAsync(cancellationToken)).IsEqualTo(SENTINEL)
      .Because("a change that rolled back must not announce itself");
  }

  [Test]
  public async Task ASavepointRolledBack_DropsItsNotification_AndTheRestCommitAsync(CancellationToken cancellationToken) {
    await using var probe = await AppSignalNotifyProbe.ListenAsync(ConnectionString, TOPIC, cancellationToken);
    using var transport = _transport();
    await using var provider = _host(transport);
    await using var scope = provider.CreateAsyncScope();
    var db = scope.ServiceProvider.GetRequiredService<WorkCoordinationDbContext>();
    var channel = scope.ServiceProvider.GetRequiredService<IAppSignalChannel>();
    await using var tx = await db.Database.BeginTransactionAsync(cancellationToken);

    await channel.PublishAsync(TOPIC, "kept", cancellationToken);
    await tx.CreateSavepointAsync("partial", cancellationToken);
    await channel.PublishAsync(TOPIC, "dropped", cancellationToken);
    await tx.RollbackToSavepointAsync("partial", cancellationToken);
    await tx.CommitAsync(cancellationToken);
    await probe.SendSentinelAsync(SENTINEL, cancellationToken);

    await Assert.That(await probe.NextAsync(cancellationToken)).IsEqualTo("kept");
    await Assert.That(await probe.NextAsync(cancellationToken)).IsEqualTo(SENTINEL)
      .Because("a publish inside a savepoint that rolled back is undone with the work it announced");
  }

  [Test]
  public async Task SeveralPublishesInOneTransaction_OnePerDistinctPayload_IdenticalOnesCollapseAsync(CancellationToken cancellationToken) {
    // PostgreSQL delivers a notification once per distinct channel and payload within one transaction: two identical
    // publishes inside it reach a subscriber once, at commit; distinct payloads each arrive, in publish order.
    await using var probe = await AppSignalNotifyProbe.ListenAsync(ConnectionString, TOPIC, cancellationToken);
    using var transport = _transport();
    await using var provider = _host(transport);
    await using var scope = provider.CreateAsyncScope();
    var db = scope.ServiceProvider.GetRequiredService<WorkCoordinationDbContext>();
    var channel = scope.ServiceProvider.GetRequiredService<IAppSignalChannel>();
    await using var tx = await db.Database.BeginTransactionAsync(cancellationToken);

    await channel.PublishAsync(TOPIC, "same", cancellationToken);
    await channel.PublishAsync(TOPIC, "same", cancellationToken);
    await channel.PublishAsync(TOPIC, "other", cancellationToken);
    await tx.CommitAsync(cancellationToken);
    await probe.SendSentinelAsync(SENTINEL, cancellationToken);

    await Assert.That(await probe.NextAsync(cancellationToken)).IsEqualTo("same");
    await Assert.That(await probe.NextAsync(cancellationToken)).IsEqualTo("other");
    await Assert.That(await probe.NextAsync(cancellationToken)).IsEqualTo(SENTINEL)
      .Because("the identical second publish collapses into the first");
  }

  [Test]
  public async Task InAScopeWithNoTransaction_IsHeardAtOnceAsync(CancellationToken cancellationToken) {
    await using var probe = await AppSignalNotifyProbe.ListenAsync(ConnectionString, TOPIC, cancellationToken);
    using var transport = _transport();
    await using var provider = _host(transport);
    await using var scope = provider.CreateAsyncScope();

    await scope.ServiceProvider.GetRequiredService<IAppSignalChannel>().PublishAsync(TOPIC, "now", cancellationToken);

    await Assert.That(await probe.NextAsync(cancellationToken)).IsEqualTo("now");
  }

  [Test]
  public async Task FromTheRootOrASingleton_IsHeardAtOnce_WhileAScopeHoldsATransactionAsync(CancellationToken cancellationToken) {
    await using var probe = await AppSignalNotifyProbe.ListenAsync(ConnectionString, TOPIC, cancellationToken);
    using var transport = _transport();
    await using var provider = _host(transport, static s => s.AddSingleton<SingletonPublisher>());
    await using var scope = provider.CreateAsyncScope();
    var db = scope.ServiceProvider.GetRequiredService<WorkCoordinationDbContext>();
    await using var tx = await db.Database.BeginTransactionAsync(cancellationToken);

    await provider.GetRequiredService<IAppSignalChannel>().PublishAsync(TOPIC, "root", cancellationToken);
    await Assert.That(await probe.NextAsync(cancellationToken)).IsEqualTo("root")
      .Because("the root has no scope, so its publish is not tied to any scope's transaction");

    await scope.ServiceProvider.GetRequiredService<SingletonPublisher>().Channel.PublishAsync(TOPIC, "singleton", cancellationToken);
    await Assert.That(await probe.NextAsync(cancellationToken)).IsEqualTo("singleton")
      .Because("a singleton's channel is the root's, even when the singleton is first resolved inside the scope");
    await tx.RollbackAsync(cancellationToken);
  }

  [Test]
  public async Task InsideATransactionScope_OnEFCore_IsHeardOnlyAfterCommitAsync(CancellationToken cancellationToken) {
    await using var probe = await AppSignalNotifyProbe.ListenAsync(ConnectionString, TOPIC, cancellationToken);
    using var transport = _transport();
    await using var provider = _host(transport);
    await using var scope = provider.CreateAsyncScope();
    var db = scope.ServiceProvider.GetRequiredService<WorkCoordinationDbContext>();

    using (var ambient = new TransactionScope(TransactionScopeOption.Required, TransactionScopeAsyncFlowOption.Enabled)) {
      _ = await db.Database.ExecuteSqlRawAsync("SELECT 1", cancellationToken);
      await scope.ServiceProvider.GetRequiredService<IAppSignalChannel>().PublishAsync(TOPIC, "scoped", cancellationToken);
      await probe.SendSentinelAsync(SENTINEL, cancellationToken);
      await Assert.That(await probe.NextAsync(cancellationToken)).IsEqualTo(SENTINEL)
        .Because("inside a TransactionScope the notification waits for the scope to commit");
      ambient.Complete();
    }

    await Assert.That(await probe.NextAsync(cancellationToken)).IsEqualTo("scoped");
  }

  [Test]
  public async Task InsideARolledBackTransactionScope_OnEFCore_IsNeverHeardAsync(CancellationToken cancellationToken) {
    await using var probe = await AppSignalNotifyProbe.ListenAsync(ConnectionString, TOPIC, cancellationToken);
    using var transport = _transport();
    var settled = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
    transport.DeferredTopicSettled = (_, delivered) => settled.TrySetResult(delivered);
    await using var provider = _host(transport);
    await using var scope = provider.CreateAsyncScope();
    var db = scope.ServiceProvider.GetRequiredService<WorkCoordinationDbContext>();

    using (new TransactionScope(TransactionScopeOption.Required, TransactionScopeAsyncFlowOption.Enabled)) {
      _ = await db.Database.ExecuteSqlRawAsync("SELECT 1", cancellationToken);
      await scope.ServiceProvider.GetRequiredService<IAppSignalChannel>().PublishAsync(TOPIC, "rolled-back", cancellationToken);
    }

    await Assert.That(await settled.Task.WaitAsync(TimeSpan.FromSeconds(15), cancellationToken)).IsFalse()
      .Because("a scope disposed without Complete rolls back, and its notification is dropped");
    await probe.SendSentinelAsync(SENTINEL, cancellationToken);
    await Assert.That(await probe.NextAsync(cancellationToken)).IsEqualTo(SENTINEL);
  }

  [Test]
  public async Task AfterATransactionScopeCommits_AFailedSend_IsLoggedAndSettlesUndeliveredAsync(CancellationToken cancellationToken) {
    // Nothing listens on port 1: the send after commit cannot open a connection. The caller's work has committed and
    // nobody awaits the send, so the failure is logged with the channel it was for.
    var logger = new CapturingLogger<PostgresSignalTransport>();
    using var transport = _transport("Host=127.0.0.1;Port=1;Database=none;Username=none;Password=none;Timeout=5", logger);
    var settled = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
    transport.DeferredTopicSettled = (_, delivered) => settled.TrySetResult(delivered);

    using (var ambient = new TransactionScope(TransactionScopeOption.Required, TransactionScopeAsyncFlowOption.Enabled)) {
      await transport.PublishTopicAsync(TOPIC, "lost", transaction: null, cancellationToken);
      ambient.Complete();
    }

    await Assert.That(await settled.Task.WaitAsync(TimeSpan.FromSeconds(30), cancellationToken)).IsFalse();
    List<(LogLevel Level, string Message, Exception? Exception)> entries;
    lock (logger.Entries) {
      entries = [.. logger.Entries];
    }
    await Assert.That(entries.Any(e => e.Level == LogLevel.Warning && e.Message.Contains("wh_app_" + TOPIC, StringComparison.Ordinal) && e.Exception is not null)).IsTrue()
      .Because("a send that fails where nobody awaits it must say what failed");
  }

  [Test]
  public async Task AfterATransactionScopeCommits_WithNoConnectionResolved_SettlesUndeliveredAsync(CancellationToken cancellationToken) {
    var options = Options.Create(new WhizbangNotificationOptions());
    var configuration = new ConfigurationBuilder().AddInMemoryCollection([]).Build();
    var instance = new ServiceInstanceProvider(configuration);
    var shared = new PgSharedNotifyConnection(options, configuration, instance, NullLogger<PgSharedNotifyConnection>.Instance);
    using var transport = new PostgresSignalTransport(options, configuration, shared, instance, NullLogger<PostgresSignalTransport>.Instance);
    var settled = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
    transport.DeferredTopicSettled = (_, delivered) => settled.TrySetResult(delivered);

    using (var ambient = new TransactionScope(TransactionScopeOption.Required, TransactionScopeAsyncFlowOption.Enabled)) {
      await transport.PublishTopicAsync(TOPIC, "nowhere", transaction: null, cancellationToken);
      ambient.Complete();
    }

    await Assert.That(await settled.Task.WaitAsync(TimeSpan.FromSeconds(15), cancellationToken)).IsFalse()
      .Because("with no notification connection the send is dropped after commit, as it is without a transaction");
  }

  [Test]
  public async Task AnotherProvidersTransaction_IsSentAtOnceAsync(CancellationToken cancellationToken) {
    // The Postgres transport cannot send inside another provider's transaction, so it treats it as none.
    await using var probe = await AppSignalNotifyProbe.ListenAsync(ConnectionString, TOPIC, cancellationToken);
    using var transport = _transport();

    await transport.PublishTopicAsync(TOPIC, "foreign", new ForeignTransaction(), cancellationToken);

    await Assert.That(await probe.NextAsync(cancellationToken)).IsEqualTo("foreign");
  }

  [Test]
  public async Task AFinishedNpgsqlTransaction_FailsThePublish_RatherThanSendingOutsideItAsync(CancellationToken cancellationToken) {
    // A publish handed a transaction is meant to be tied to it. Once that transaction has finished there is nothing to tie
    // it to, and sending at once would announce a change the caller meant to commit with it; Npgsql refuses instead.
    await using var probe = await AppSignalNotifyProbe.ListenAsync(ConnectionString, TOPIC, cancellationToken);
    using var transport = _transport();
    await using var connection = new NpgsqlConnection(ConnectionString);
    await connection.OpenAsync(cancellationToken);
    await using var finished = await connection.BeginTransactionAsync(cancellationToken);
    await finished.CommitAsync(cancellationToken);

    await Assert.That(async () => await transport.PublishTopicAsync(TOPIC, "finished", finished, cancellationToken))
      .Throws<InvalidOperationException>();
    await probe.SendSentinelAsync(SENTINEL, cancellationToken);
    await Assert.That(await probe.NextAsync(cancellationToken)).IsEqualTo(SENTINEL)
      .Because("the refused publish sent nothing");
  }

  [Test]
  public async Task AnOversizePayload_InsideADbContextTransaction_FailsThePublishAndTheTransactionAsync(CancellationToken cancellationToken) {
    // Sent inside the caller's transaction, a payload PostgreSQL rejects (8000 bytes or more) is rejected by the statement
    // that runs in that transaction, so it aborts it: the publish throws, and the caller's own work in that transaction is
    // lost even if it goes on to commit. Nothing is delivered.
    await using var probe = await AppSignalNotifyProbe.ListenAsync(ConnectionString, TOPIC, cancellationToken);
    using var transport = _transport();
    await using var provider = _host(transport);
    await using (var setup = new NpgsqlConnection(ConnectionString)) {
      await setup.OpenAsync(cancellationToken);
      await using var create = new NpgsqlCommand("CREATE TABLE app_signal_oversize_work (id int PRIMARY KEY)", setup);
      _ = await create.ExecuteNonQueryAsync(cancellationToken);
    }
    await using var scope = provider.CreateAsyncScope();
    var db = scope.ServiceProvider.GetRequiredService<WorkCoordinationDbContext>();
    await using var tx = await db.Database.BeginTransactionAsync(cancellationToken);
    _ = await db.Database.ExecuteSqlRawAsync("INSERT INTO app_signal_oversize_work (id) VALUES (1)", cancellationToken);

    var oversize = new string('x', 8000);
    var thrown = await Assert.That(async () =>
        await scope.ServiceProvider.GetRequiredService<IAppSignalChannel>().PublishAsync(TOPIC, oversize, cancellationToken))
      .Throws<PostgresException>();
    await Assert.That(thrown!.SqlState).IsEqualTo(PostgresErrorCodes.InvalidParameterValue)
      .Because("PostgreSQL rejects a NOTIFY payload of 8000 bytes or more");
    var next = await Assert.That(async () => await db.Database.ExecuteSqlRawAsync("SELECT 1", cancellationToken))
      .Throws<PostgresException>();
    await Assert.That(next!.SqlState).IsEqualTo(PostgresErrorCodes.InFailedSqlTransaction)
      .Because("the rejected statement aborted the caller's transaction");
    await tx.CommitAsync(cancellationToken);

    await using (var check = new NpgsqlConnection(ConnectionString)) {
      await check.OpenAsync(cancellationToken);
      await using var count = new NpgsqlCommand("SELECT count(*) FROM app_signal_oversize_work", check);
      await Assert.That((long)(await count.ExecuteScalarAsync(cancellationToken))!).IsEqualTo(0L)
        .Because("a commit of an aborted transaction rolls it back, so the caller's work in it is lost");
    }
    await probe.SendSentinelAsync(SENTINEL, cancellationToken);
    await Assert.That(await probe.NextAsync(cancellationToken)).IsEqualTo(SENTINEL)
      .Because("the rejected notification was never sent");
  }
}
