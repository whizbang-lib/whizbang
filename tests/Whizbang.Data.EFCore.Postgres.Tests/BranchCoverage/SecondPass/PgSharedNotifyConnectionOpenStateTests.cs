// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Options;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Notifications;
using Whizbang.Core.Observability;
using Whizbang.Data.Postgres.Notifications;

namespace Whizbang.Data.EFCore.Postgres.Tests.BranchCoverage.SecondPass;

/// <summary>
/// The shared LISTEN connection reports no open connection before it starts, and an open one once it
/// has come up and declared itself available.
/// </summary>
/// <code-under-test>src/Whizbang.Data.Postgres/Notifications/PgSharedNotifyConnection.cs</code-under-test>
[Category("Integration")]
[Category("Shard5")]
public class PgSharedNotifyConnectionOpenStateTests : EFCoreTestBase {

  [Test]
  [Timeout(60000)]
  public async Task ConnectionOpenState_NoConnectionBeforeStart_OpenOnceAvailableAsync(CancellationToken cancellationToken) {
    var logger = new CapturingLogger();
    using var gate = new PgSharedNotifyConnection(
      Options.Create(new WhizbangNotificationOptions {
        DirectConnectionString = ConnectionString,
        SignalingMode = WorkSignalingMode.ListenNotify,
        SelfTestTimeout = TimeSpan.FromSeconds(10),
      }),
      new ConfigurationBuilder().AddInMemoryCollection([]).Build(),
      new ServiceInstanceProvider(Guid.NewGuid(), "open-state-svc", "open-state-host", processId: 1),
      logger: logger);
    var available = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    gate.OnAvailabilityChanged += isAvailable => {
      if (isAvailable) {
        available.TrySetResult();
      }
    };

    var openBeforeStart = gate.IsConnectionOpenForTesting;
    await gate.StartAsync(cancellationToken);
    try {
      // Bounded inside the test timeout so a connection that never comes up fails with what it reported
      // (each failed attempt logs its reason) rather than a bare timeout (#2138).
      try {
        await available.Task.WaitAsync(TimeSpan.FromSeconds(45), cancellationToken);
      } catch (TimeoutException) {
        Assert.Fail("the shared LISTEN connection never became available; it logged: "
          + string.Join(" | ", logger.AllMessages()));
      }

      await Assert.That(openBeforeStart).IsFalse()
        .Because("nothing has opened the LISTEN connection before the loop starts");
      await Assert.That(gate.IsConnectionOpenForTesting).IsTrue()
        .Because("availability is declared from an open LISTEN connection");
    } finally {
      await gate.StopAsync(CancellationToken.None);
    }
  }
}
