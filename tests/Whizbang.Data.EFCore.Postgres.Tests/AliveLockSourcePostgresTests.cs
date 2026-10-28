// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Notifications;
using Whizbang.Core.Observability;
using Whizbang.Core.Workers;
using Whizbang.Data.Postgres.Notifications;
using Whizbang.Testing.Containers;

namespace Whizbang.Data.EFCore.Postgres.Tests;

/// <summary>
/// The shared notify connection as the instance's alive-lock source (#1286), against a real database: on a connection
/// of its own it holds the lock and says so, which puts the heartbeat on its slow cadence; behind the pooled fallback
/// it says it holds none, so a pooled deployment keeps the fast cadence exactly as before, whatever lock its session
/// managed to take.
/// </summary>
/// <remarks>
/// Every wait is on the connection's own availability signal, raised once the lock has been claimed and the
/// self-test probe has round-tripped. The deadline only bounds the wait.
/// </remarks>
/// <docs>fundamentals/workers/instance-liveness</docs>
/// <code-under-test>src/Whizbang.Data.Postgres/Notifications/PgSharedNotifyConnection.cs</code-under-test>
[Category("Shard3")]
public class AliveLockSourcePostgresTests : EFCoreTestBase {
  private static readonly TimeSpan _signalDeadline = TimeSpan.FromSeconds(60);

  [Test]
  [Timeout(120000)]
  public async Task OnADirectConnection_TheLockIsHeld_AndReportedAsync(CancellationToken cancellationToken) {
    var cfg = new ConfigurationBuilder().AddInMemoryCollection([]).Build();
    using var shared = _shared(new WhizbangNotificationOptions {
      DirectConnectionString = ConnectionString,
      SignalingMode = WorkSignalingMode.ListenNotify,
      SelfTestTimeout = TimeSpan.FromSeconds(10),
    }, cfg);
    IInstanceAliveLockSource source = shared;

    await shared.StartAsync(cancellationToken);
    try {
      await _availableAsync(shared, cancellationToken);

      await Assert.That(shared.ConnectionMode).IsEqualTo(InstanceConnectionMode.Direct);
      await Assert.That(source.IsAliveLockHeld).IsTrue()
        .Because("a direct instance's lock is visible to its peers, so its heartbeat may run on the slow cadence");
    } finally {
      await shared.StopAsync(CancellationToken.None);
    }

    await Assert.That(source.IsAliveLockHeld).IsFalse()
      .Because("a stopped connection holds nothing, and the heartbeat must return to the fast cadence");
  }

  [Test]
  [Timeout(120000)]
  public async Task BehindThePooledFallback_NoLockIsReported_WhateverTheSessionTookAsync(CancellationToken cancellationToken) {
    var cfg = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> {
      ["ConnectionStrings:alive-lock-pooled"] = ConnectionString,
    }).Build();
    using var shared = _shared(new WhizbangNotificationOptions {
      ConnectionStringKey = "alive-lock-pooled",
      SignalingMode = WorkSignalingMode.ListenNotify,
      SelfTestTimeout = TimeSpan.FromSeconds(10),
    }, cfg);
    IInstanceAliveLockSource source = shared;

    await shared.StartAsync(cancellationToken);
    try {
      await _availableAsync(shared, cancellationToken);

      await Assert.That(shared.ConnectionMode).IsEqualTo(InstanceConnectionMode.Pooled);
      await Assert.That(source.IsAliveLockHeld).IsFalse()
        .Because("a pooled instance is judged by its heartbeat alone, so it must keep the fast cadence");
    } finally {
      await shared.StopAsync(CancellationToken.None);
    }
  }

  /// <summary>
  /// On a database the migrations have not reached yet, the connection does not call a function that is not there
  /// (#1636): it says nothing above Debug and holds no lock, and the first keepalive after the function appears claims
  /// it. It used to fail with 42883 and a Warning on every fresh database, and never claim the lock on that connection.
  /// </summary>
  [Test]
  [Timeout(120000)]
  public async Task OnADatabaseWithoutTheFunctionYet_TheClaimWaitsQuietly_AndAKeepaliveMakesItOnceItExistsAsync(
      CancellationToken cancellationToken) {
    var database = await PerTestDatabaseFactory.CreateAsync("alivelock", cancellationToken: cancellationToken);
    try {
      var warnings = new WarningLog();
      using var shared = new PgSharedNotifyConnection(Options.Create(new WhizbangNotificationOptions {
        DirectConnectionString = database.ConnectionString,
        SignalingMode = WorkSignalingMode.ListenNotify,
        SelfTestTimeout = TimeSpan.FromSeconds(10),
        ListenKeepaliveInterval = TimeSpan.FromMilliseconds(200),
      }), new ConfigurationBuilder().AddInMemoryCollection([]).Build(),
        new ServiceInstanceProvider(Guid.CreateVersion7(), "alive-lock", "host", 1), warnings);
      var claimed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
      shared.OnAliveLockClaimed += () => claimed.TrySetResult();
      IInstanceAliveLockSource source = shared;

      await shared.StartAsync(cancellationToken);
      try {
        await _availableAsync(shared, cancellationToken);
        await Assert.That(source.IsAliveLockHeld).IsFalse();
        await Assert.That(warnings.Lines).IsEmpty().Because("a function the migrations have not created yet is not a failure");

        await using (var connection = new Npgsql.NpgsqlConnection(database.ConnectionString)) {
          await connection.OpenAsync(cancellationToken);
          await using var create = new Npgsql.NpgsqlCommand("""
            CREATE FUNCTION claim_instance_alive_lock(p_instance_id UUID) RETURNS BOOLEAN AS $$
            BEGIN
              RETURN pg_try_advisory_lock(hashtext('wh_instance_alive:' || p_instance_id::text));
            END;
            $$ LANGUAGE plpgsql;
            """, connection);
          await create.ExecuteNonQueryAsync(cancellationToken);
        }
        await claimed.Task.WaitAsync(_signalDeadline, cancellationToken);

        await Assert.That(source.IsAliveLockHeld).IsTrue().Because("a keepalive claimed the lock once the function existed");
        await Assert.That(warnings.Lines).IsEmpty();
      } finally {
        await shared.StopAsync(CancellationToken.None);
      }
    } finally {
      Npgsql.NpgsqlConnection.ClearAllPools();
      await PerTestDatabaseFactory.DropAsync(database.Name, cancellationToken: CancellationToken.None);
    }
  }

  /// <summary>The Warning and Error lines the connection logs.</summary>
  private sealed class WarningLog : ILogger<PgSharedNotifyConnection> {
    private readonly List<string> _lines = [];

    public List<string> Lines {
      get {
        lock (_lines) {
          return [.. _lines];
        }
      }
    }

    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
    public bool IsEnabled(LogLevel logLevel) => true;
    public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
        Func<TState, Exception?, string> formatter) {
      if (logLevel >= LogLevel.Warning) {
        lock (_lines) {
          _lines.Add($"{logLevel} {formatter(state, exception)}");
        }
      }
    }
  }

  private static PgSharedNotifyConnection _shared(WhizbangNotificationOptions options, IConfiguration cfg) =>
    new(Options.Create(options), cfg, new ServiceInstanceProvider(Guid.CreateVersion7(), "alive-lock", "host", 1),
      NullLogger<PgSharedNotifyConnection>.Instance);

  /// <summary>Waits for the connection's availability signal: raised after the lock claim and the probe.</summary>
  private static async Task _availableAsync(PgSharedNotifyConnection shared, CancellationToken cancellationToken) {
    var available = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    void OnChanged(bool isAvailable) {
      if (isAvailable) {
        available.TrySetResult();
      }
    }
    shared.OnAvailabilityChanged += OnChanged;
    try {
      if (!shared.IsAvailable) {
        await available.Task.WaitAsync(_signalDeadline, cancellationToken);
      }
    } finally {
      shared.OnAvailabilityChanged -= OnChanged;
    }
  }
}
