// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using Npgsql;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Notifications;
using Whizbang.Core.Observability;
using Whizbang.Data.Postgres.Notifications;

namespace Whizbang.Data.EFCore.Postgres.Tests;

/// <summary>
/// <para>The self-test probe's wait for its own notification is bounded by a timeout Npgsql arms on the read, never by
/// a token cancel (#2138). The probe waited with <c>WaitAsync(token)</c> and a <c>CancelAfter(SelfTestTimeout)</c>
/// token, and Npgsql can discard a cancel that lands as the wait enters its read (the race #2150 and #2155 fixed in
/// the dispatch loop). A probe whose notification had not arrived then sat on the idle socket for good: the
/// connection loop never reached its retry, the instance never declared LISTEN/NOTIFY available, and a test waiting
/// for availability timed out with nothing logged.</para>
/// <para>Each case hands the probe a wait that ignores its token, the missed cancellation made certain, so only the
/// timeout the probe passes can end it.</para>
/// </summary>
/// <code-under-test>src/Whizbang.Data.Postgres/Notifications/PgSharedNotifyConnection.cs</code-under-test>
[Category("Shard5")]
public class PgSharedNotifyProbeWaitTests : EFCoreTestBase {
  private static readonly TimeSpan _selfTestTimeout = TimeSpan.FromSeconds(5);

  [Test]
  [Timeout(60000)]
  public async Task Probe_WhoseWaitIgnoresItsToken_EndsAtTheSelfTestTimeoutItPassesAsync(CancellationToken cancellationToken) {
    var clock = new FakeTimeProvider();
    using var gate = _gate(clock);
    var timeouts = new List<TimeSpan>();
    // A wait that times out with nothing read, the way Npgsql reports one, and never looks at its token.
    gate.ProbeWaitAsync = (_, timeout, _) => {
      timeouts.Add(timeout);
      return Task.FromResult(false);
    };
    await using var conn = new NpgsqlConnection(ConnectionString);
    await conn.OpenAsync(cancellationToken);

    var arrived = await gate.RunProbeAsync(conn, ConnectionString, cancellationToken);

    await Assert.That(arrived).IsFalse();
    await Assert.That(timeouts).IsEquivalentTo([_selfTestTimeout])
      .Because("the probe's one wait carries the whole self-test window as its timeout, which a lost cancel cannot undo");
  }

  [Test]
  [Timeout(60000)]
  public async Task Probe_WhoseWaitsReadOnlyOtherMessages_StopsAtTheDeadline_PassingWhatRemainsAsync(
      CancellationToken cancellationToken) {
    var clock = new FakeTimeProvider();
    using var gate = _gate(clock);
    var timeouts = new List<TimeSpan>();
    // Each wait reads a message that is not the probe's (backend chatter) after two seconds pass.
    gate.ProbeWaitAsync = (_, timeout, _) => {
      timeouts.Add(timeout);
      clock.Advance(TimeSpan.FromSeconds(2));
      return Task.FromResult(true);
    };
    await using var conn = new NpgsqlConnection(ConnectionString);
    await conn.OpenAsync(cancellationToken);

    var arrived = await gate.RunProbeAsync(conn, ConnectionString, cancellationToken);

    await Assert.That(arrived).IsFalse();
    await Assert.That(timeouts).IsEquivalentTo(
        [TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(3), TimeSpan.FromSeconds(1)],
        TUnit.Assertions.Enums.CollectionOrdering.Matching)
      .Because("each wait is bounded by what is left of the window, and none starts once it has passed");
  }

  [Test]
  [Timeout(60000)]
  public async Task Probe_WithTheRealWait_SeesItsOwnNotificationAsync(CancellationToken cancellationToken) {
    using var gate = _gate(TimeProvider.System);
    await using var conn = new NpgsqlConnection(ConnectionString);
    await conn.OpenAsync(cancellationToken);

    var arrived = await gate.RunProbeAsync(conn, ConnectionString, cancellationToken);

    await Assert.That(arrived).IsTrue()
      .Because("the default wait is Npgsql's own, bounded by the window, and the probe's notification ends it");
  }

  private PgSharedNotifyConnection _gate(TimeProvider clock) {
    var cfg = new ConfigurationBuilder().AddInMemoryCollection([]).Build();
    return new PgSharedNotifyConnection(
      Options.Create(new WhizbangNotificationOptions {
        DirectConnectionString = ConnectionString,
        SignalingMode = WorkSignalingMode.ListenNotify,
        SelfTestTimeout = _selfTestTimeout,
      }),
      cfg,
      new ServiceInstanceProvider(cfg),
      NullLogger<PgSharedNotifyConnection>.Instance,
      connectionStringFallback: null,
      timeProvider: clock);
  }
}
