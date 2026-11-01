// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.ValueObjects;
using Whizbang.Data.EFCore.Postgres.Tests.Migrations;

namespace Whizbang.Data.EFCore.Postgres.Tests;

/// <summary>
/// The barrier every schema initialization passes (#1636): a second initialization of the same database and schema in
/// the same host waits for the first to leave; one in another host, database or schema does not wait; a context with
/// no host does not wait; and a canceled wait leaves the barrier as it found it. No server is involved: the connections
/// are never opened.
/// </summary>
/// <code-under-test>src/Whizbang.Data.EFCore.Postgres/SchemaInitializationBarrier.cs</code-under-test>
/// <docs>data/turnkey-initialization#a-second-call-waits</docs>
[Category("Unit")]
[Category("Shard3")]
public class SchemaInitializationBarrierTests {
  // Each test names databases of its own, so an initialization another test runs at the same time never shares a key.
  private readonly string _suffix = ((Guid)TrackedGuid.New()).ToString("N");

  private string _connectionString(string database, string host = "db-a") =>
    $"Host={host};Port=5432;Database={database}_{_suffix};Username=u;Password=p";

  private NpgsqlConnection _connection(string database, string host = "db-a") => new(_connectionString(database, host));

  private static ServiceProvider _host() => new ServiceCollection().BuildServiceProvider();

  /// <summary>
  /// The rule: a second initialization of the same database and schema in the same host waits until the first leaves,
  /// says so, and then has its own turn. A scope of the host is the same host.
  /// </summary>
  [Test]
  public async Task ASecondEntry_ForTheSameHostDatabaseAndSchema_WaitsForTheFirstToLeaveAsync() {
    await using var host = _host();
    await using var scope = host.CreateAsyncScope();
    await using var connection = _connection("orders");
    var logger = new SignalingListLogger();

    var first = await SchemaInitializationBarrier.EnterAsync(host, connection, "public", null, CancellationToken.None);
    var second = SchemaInitializationBarrier.EnterAsync(scope.ServiceProvider, connection, "public", logger, CancellationToken.None);

    await Assert.That(second.IsCompleted).IsFalse().Because("the first initialization has not left");
    await Assert.That(logger.Entries.Any(e => e.Message.Contains("already running in this host", StringComparison.Ordinal)))
      .IsTrue().Because("a wait is said, so a start that stalls here can be read");

    await first.DisposeAsync();
    var turn = await second;
    await turn.DisposeAsync();
    await turn.DisposeAsync();

    await Assert.That(SchemaInitializationBarrier.Tracks(host, connection, "public")).IsFalse()
      .Because("a key with nobody holding or waiting for it is forgotten, and a second dispose releases nothing");
  }

  /// <summary>
  /// An initialization in another host (another instance, even in this process), of another schema, another database
  /// or another server does not wait.
  /// </summary>
  [Test]
  public async Task EntriesForAnotherHostSchemaOrDatabase_DoNotWaitAsync() {
    await using var host = _host();
    await using var otherInstance = _host();
    await using var orders = _connection("orders");
    await using var billing = _connection("billing");
    await using var otherServer = _connection("orders", host: "db-b");

    var held = await SchemaInitializationBarrier.EnterAsync(host, orders, "public", null, CancellationToken.None);
    Task<IAsyncDisposable>[] others = [
      SchemaInitializationBarrier.EnterAsync(otherInstance, orders, "public", null, CancellationToken.None),
      SchemaInitializationBarrier.EnterAsync(host, orders, "tenant_a", null, CancellationToken.None),
      SchemaInitializationBarrier.EnterAsync(host, billing, "public", null, CancellationToken.None),
      SchemaInitializationBarrier.EnterAsync(host, otherServer, "public", null, CancellationToken.None),
    ];

    foreach (var other in others) {
      await Assert.That(other.IsCompleted).IsTrue();
      await (await other).DisposeAsync();
    }
    await held.DisposeAsync();
    await Assert.That(SchemaInitializationBarrier.Tracks(host, orders, "public")).IsFalse();
  }

  /// <summary>With no host there is nothing to wait for: a context built by hand and initialized with no provider.</summary>
  [Test]
  public async Task NoHost_DoesNotWaitAsync() {
    await using var connection = _connection("orders");

    var first = await SchemaInitializationBarrier.EnterAsync((IServiceProvider?)null, connection, "public", null, CancellationToken.None);
    var second = SchemaInitializationBarrier.EnterAsync((IServiceProvider?)null, connection, "public", null, CancellationToken.None);

    await Assert.That(second.IsCompleted).IsTrue();
    await first.DisposeAsync();
    await (await second).DisposeAsync();
  }

  /// <summary>A wait that is canceled ends with the cancellation, and the next initialization enters as usual.</summary>
  [Test]
  public async Task ACanceledWait_LeavesTheBarrierAsItFoundItAsync() {
    await using var host = _host();
    await using var connection = _connection("orders");
    using var cancel = new CancellationTokenSource();

    var held = await SchemaInitializationBarrier.EnterAsync(host, connection, "public", null, CancellationToken.None);
    var waiting = SchemaInitializationBarrier.EnterAsync(host, connection, "public", null, cancel.Token);
    await cancel.CancelAsync();

    await Assert.That(async () => await waiting).Throws<OperationCanceledException>();
    await held.DisposeAsync();
    await Assert.That(SchemaInitializationBarrier.Tracks(host, connection, "public")).IsFalse();
    var next = SchemaInitializationBarrier.EnterAsync(host, connection, "public", null, CancellationToken.None);
    await Assert.That(next.IsCompleted).IsTrue();
    await (await next).DisposeAsync();
  }

  /// <summary>
  /// A context is barriered by the host it was built from (its application service provider), else by the provider
  /// passed to the initialization, and not at all when it has neither.
  /// </summary>
  [Test]
  public async Task AContext_IsBarriered_ByTheHostItWasBuiltFrom_ElseByThePassedProviderAsync() {
    await using var host = _host();
    await using var passed = _host();
    await using var built = new MinimalDbContext(new DbContextOptionsBuilder<MinimalDbContext>()
      .UseNpgsql(_connectionString("orders")).UseApplicationServiceProvider(host).Options);
    await using var byHand = new MinimalDbContext(new DbContextOptionsBuilder<MinimalDbContext>()
      .UseNpgsql(_connectionString("orders")).Options);
    var connection = byHand.Database.GetDbConnection();

    await using (await SchemaInitializationBarrier.EnterAsync(built, passed, "public", null, CancellationToken.None)) {
      await Assert.That(SchemaInitializationBarrier.Tracks(host, connection, "public")).IsTrue()
        .Because("the context's own application service provider is its host");
      await Assert.That(SchemaInitializationBarrier.Tracks(passed, connection, "public")).IsFalse();
    }
    await using (await SchemaInitializationBarrier.EnterAsync(byHand, passed, "public", null, CancellationToken.None)) {
      await Assert.That(SchemaInitializationBarrier.Tracks(passed, connection, "public")).IsTrue()
        .Because("a context with no application service provider is barriered by the provider passed in");
    }
    var unhosted = SchemaInitializationBarrier.EnterAsync(byHand, null, "public", null, CancellationToken.None);
    await Assert.That(unhosted.IsCompleted).IsTrue();
    await (await unhosted).DisposeAsync();
  }

  /// <summary>Missing arguments are caller errors.</summary>
  [Test]
  public async Task MissingArguments_AreRefusedAsync() {
    await using var host = _host();
    await using var connection = _connection("orders");

    await Assert.That(async () => await SchemaInitializationBarrier.EnterAsync(host, null!, "public", null, CancellationToken.None))
      .Throws<ArgumentNullException>();
    await Assert.That(async () => await SchemaInitializationBarrier.EnterAsync(host, connection, null!, null, CancellationToken.None))
      .Throws<ArgumentNullException>();
    await Assert.That(async () => await SchemaInitializationBarrier.EnterAsync((DbContext)null!, host, "public", null, CancellationToken.None))
      .Throws<ArgumentNullException>();
  }
}
