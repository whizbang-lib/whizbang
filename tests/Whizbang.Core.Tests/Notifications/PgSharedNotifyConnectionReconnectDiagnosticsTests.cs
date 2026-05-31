using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Notifications;
using Whizbang.Core.Observability;
using Whizbang.Data.Postgres.Notifications;

namespace Whizbang.Core.Tests.Notifications;

#pragma warning disable CA1707
#pragma warning disable IDE1006

/// <summary>
/// Regression lock for the a consumer-deployment diagnostic: when
/// <see cref="PgSharedNotifyConnection"/>'s probe fails (or any other connect-time
/// error), the disconnect warning MUST name the resolved connection-string source
/// and key so operators can immediately see whether the bad connection came from
/// <c>-direct</c>, the pooled key fallback, the DbContext fallback, or an explicit
/// option. Without this, "Self-test probe failed" in Azure logs is unactionable —
/// the operator has to read source code to know which env var to investigate.
///
/// We drive a deliberately-bad connection string through the worker; it fails at
/// the network layer (the host is unresolvable) which routes us into the
/// LogReconnect path. The capturing logger snapshots the formatted message; we
/// assert both placeholders survived into the output.
/// </summary>
/// <docs>fundamentals/work-coordinator/notifications-and-pgbouncer</docs>
public class PgSharedNotifyConnectionReconnectDiagnosticsTests {

  [Test]
  public async Task LogReconnect_FormattedMessage_NamesResolutionSourceAndKeyAsync() {
    // A deliberately-bad connection string with valid credentials (so the
    // resolver picks it as DirectKey) but a host that won't resolve — guarantees
    // the connect call inside ExecuteAsync throws, dropping us into the catch
    // → LogReconnect path within milliseconds.
    var badDirect = "Host=__whizbang-nonexistent-host__;Database=x;Username=u;Password=p;Timeout=2;Command Timeout=2";
    var cfg = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> {
      ["ConnectionStrings:test-db-direct"] = badDirect,
    }).Build();
    var options = new WhizbangNotificationOptions {
      ConnectionStringKey = "test-db",
      SignalingMode = WorkSignalingMode.Auto,
      // Aggressive reconnect backoff so the test doesn't have to wait.
      ListenReconnectInitialDelay = TimeSpan.FromMilliseconds(50),
      ListenReconnectMaxDelay = TimeSpan.FromMilliseconds(50),
      ListenReconnectBackoffMultiplier = 1.0,
      SelfTestTimeout = TimeSpan.FromMilliseconds(200),
    };

    var logger = new _CapturingLogger();
    var worker = new PgSharedNotifyConnection(
      Options.Create(options),
      cfg,
      new ServiceInstanceProvider(cfg),
      logger,
      connectionStringFallback: null);

    using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
    await worker.StartAsync(cts.Token);

    // Wait until the disconnect warning lands, polling the capture (no Task.Delay
    // polling for state we're waiting on — the log capture's TCS fires on event id 3).
    await logger.DisconnectLoggedTcs.Task.WaitAsync(TimeSpan.FromSeconds(10));

    try { await worker.StopAsync(CancellationToken.None); } catch { /* shutdown best-effort */ }

    var msg = logger.LastDisconnectMessage;
    await Assert.That(msg).IsNotNull();
    // The new format must name the source (DirectKey here) and the key
    // ("test-db") so a consumer operators can match the symptom to the env var.
    await Assert.That(msg!).Contains("DirectKey");
    await Assert.That(msg!).Contains("test-db");
    // The operator-actionable hint must also be present so the log line stands
    // on its own without needing source-code lookup.
    await Assert.That(msg!).Contains("-direct");
  }

  [Test]
  public async Task LogReconnect_PooledKeyFallback_NamesSourceAndHintsAddDirectAsync() {
    // Mirrors the a consumer failure mode: ONLY the pooled "appservice-db" key is
    // configured, no "-direct". Resolver falls back to PooledKeyFallback → the
    // connection routes through pgbouncer → LISTEN/NOTIFY round-trip will fail.
    // Operator must see in the log that the source was PooledKeyFallback so the
    // fix ("add ConnectionStrings:test-db-direct") is obvious.
    var badPooled = "Host=__whizbang-nonexistent-host__;Database=x;Username=u;Password=p;Timeout=2;Command Timeout=2";
    var cfg = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> {
      ["ConnectionStrings:test-db"] = badPooled,
    }).Build();
    var options = new WhizbangNotificationOptions {
      ConnectionStringKey = "test-db",
      SignalingMode = WorkSignalingMode.Auto,
      ListenReconnectInitialDelay = TimeSpan.FromMilliseconds(50),
      ListenReconnectMaxDelay = TimeSpan.FromMilliseconds(50),
      ListenReconnectBackoffMultiplier = 1.0,
      SelfTestTimeout = TimeSpan.FromMilliseconds(200),
    };

    var logger = new _CapturingLogger();
    var worker = new PgSharedNotifyConnection(
      Options.Create(options),
      cfg,
      new ServiceInstanceProvider(cfg),
      logger,
      connectionStringFallback: null);

    using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
    await worker.StartAsync(cts.Token);
    await logger.DisconnectLoggedTcs.Task.WaitAsync(TimeSpan.FromSeconds(10));
    try { await worker.StopAsync(CancellationToken.None); } catch { /* shutdown */ }

    var msg = logger.LastDisconnectMessage;
    await Assert.That(msg).IsNotNull();
    await Assert.That(msg!).Contains("PooledKeyFallback");
    await Assert.That(msg!).Contains("test-db");
    // Operator-actionable hint must appear so the fix is obvious without
    // having to read the source.
    await Assert.That(msg!).Contains("pgbouncer");
    await Assert.That(msg!).Contains("test-db-direct");
  }

  private sealed class _CapturingLogger : ILogger<PgSharedNotifyConnection> {
    public TaskCompletionSource DisconnectLoggedTcs { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public string? LastDisconnectMessage { get; private set; }

    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
    public bool IsEnabled(LogLevel logLevel) => true;

    public void Log<TState>(
      LogLevel logLevel,
      EventId eventId,
      TState state,
      Exception? exception,
      Func<TState, Exception?, string> formatter) {
      if (eventId.Id == 3) {
        // EventId 3 is the disconnect warning. Capture and signal.
        LastDisconnectMessage = formatter(state, exception);
        _ = DisconnectLoggedTcs.TrySetResult();
      }
    }
  }
}
