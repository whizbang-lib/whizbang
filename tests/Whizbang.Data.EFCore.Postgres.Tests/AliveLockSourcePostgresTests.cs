// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Notifications;
using Whizbang.Core.Observability;
using Whizbang.Core.Workers;
using Whizbang.Data.Postgres.Notifications;

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
