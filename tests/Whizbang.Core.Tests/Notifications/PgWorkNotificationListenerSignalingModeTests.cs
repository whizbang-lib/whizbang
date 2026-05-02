using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Notifications;
using Whizbang.Data.Postgres.Notifications;

namespace Whizbang.Core.Tests.Notifications;

/// <summary>
/// Tests <see cref="PgWorkNotificationListener"/>'s behavior across the three
/// <see cref="WorkSignalingMode"/> values + the <c>DisableNotifications</c> kill switch.
/// These run without a real Postgres connection — the listener's startup branching is
/// the only thing tested. Real LISTEN/NOTIFY round-trip lives in the integration suite.
/// </summary>
/// <docs>fundamentals/work-coordinator/notifications-and-pgbouncer</docs>
public class PgWorkNotificationListenerSignalingModeTests {

  private static IConfiguration _emptyConfig() =>
    new ConfigurationBuilder().AddInMemoryCollection([]).Build();

  private static IConfiguration _configWith(string key, string value) =>
    new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { [key] = value }).Build();

  [Test]
  public async Task SignalingMode_Polling_ReturnsImmediatelyWithoutOpeningConnectionAsync() {
    var opts = Options.Create(new WhizbangNotificationOptions {
      SignalingMode = WorkSignalingMode.Polling,
      ConnectionStringKey = "should-be-ignored"
    });
    var config = _configWith("ConnectionStrings:should-be-ignored-direct", "Host=should-be-ignored");
    var listener = new PgWorkNotificationListener(opts, config, NullLogger<PgWorkNotificationListener>.Instance);

    using var cts = new CancellationTokenSource();
    await listener.StartAsync(cts.Token);
    var executeTask = listener.ExecuteTask!;
    await executeTask;  // synchronous return — task already completed

    await Assert.That(executeTask.IsCompletedSuccessfully).IsTrue();
    await Assert.That(listener.IsHealthy).IsFalse();
    await listener.StopAsync(CancellationToken.None);
  }

  [Test]
  public async Task DisableNotifications_True_ReturnsImmediatelyAsync() {
    // DisableNotifications is the legacy synonym of SignalingMode.Polling — same behavior.
    var opts = Options.Create(new WhizbangNotificationOptions {
      DisableNotifications = true,
      ConnectionStringKey = "ignored"
    });
    var config = _configWith("ConnectionStrings:ignored-direct", "Host=ignored");
    var listener = new PgWorkNotificationListener(opts, config, NullLogger<PgWorkNotificationListener>.Instance);

    using var cts = new CancellationTokenSource();
    await listener.StartAsync(cts.Token);
    await listener.ExecuteTask!;

    await Assert.That(listener.ExecuteTask!.IsCompletedSuccessfully).IsTrue();
    await Assert.That(listener.IsHealthy).IsFalse();
    await listener.StopAsync(CancellationToken.None);
  }

  [Test]
  public async Task SignalingMode_Auto_NoConnectionResolved_FallsBackToDisabledAsync() {
    // Auto mode without any conn string config or direct override → polling-only fallback,
    // no exception. This is the safe path for environments without LISTEN/NOTIFY support.
    var opts = Options.Create(new WhizbangNotificationOptions {
      SignalingMode = WorkSignalingMode.Auto
    });
    var listener = new PgWorkNotificationListener(opts, _emptyConfig(), NullLogger<PgWorkNotificationListener>.Instance);

    using var cts = new CancellationTokenSource();
    await listener.StartAsync(cts.Token);
    await listener.ExecuteTask!;

    await Assert.That(listener.ExecuteTask!.IsCompletedSuccessfully).IsTrue();
    await Assert.That(listener.IsHealthy).IsFalse();
    await listener.StopAsync(CancellationToken.None);
  }

  [Test]
  public async Task SignalingMode_ListenNotify_NoConnectionResolved_ThrowsAtStartupAsync() {
    // Fail-fast in production where the operator expects burst-latency to be ≤50ms; a
    // misconfig that silently degrades to ≤250ms polling would mask a real problem.
    var opts = Options.Create(new WhizbangNotificationOptions {
      SignalingMode = WorkSignalingMode.ListenNotify
    });
    var listener = new PgWorkNotificationListener(opts, _emptyConfig(), NullLogger<PgWorkNotificationListener>.Instance);

    using var cts = new CancellationTokenSource();
    await listener.StartAsync(cts.Token);

    // ExecuteTask should be faulted with InvalidOperationException
    await Assert.That(async () => await listener.ExecuteTask!)
      .Throws<InvalidOperationException>()
      .WithMessageContaining("WorkSignalingMode.ListenNotify");

    await listener.StopAsync(CancellationToken.None);
  }
}
