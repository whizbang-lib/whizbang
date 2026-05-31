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
/// Sibling regression lock to
/// <c>PgSharedNotifyConnectionReconnectDiagnosticsTests</c> — same diagnostic
/// requirement applied to <see cref="PgCommitOrderStamperWorker"/>'s iteration-error
/// log. The Azure logs the user shared confirmed
/// <c>PgCommitOrderStamperWorker iteration failed</c> warnings stack up alongside
/// PgSharedNotifyConnection's; both need to surface the resolution source +
/// connection-string key so operators can act without source-code spelunking.
/// </summary>
/// <docs>fundamentals/work-coordinator/commit-sequence</docs>
public class PgCommitOrderStamperIterationDiagnosticsTests {

  [Test]
  public async Task LogIterationError_NamesResolutionSourceAndKeyAsync() {
    // Deliberately-bad connection string with credentials so the resolver
    // picks DirectKey, but the host won't resolve — the open call inside
    // ExecuteAsync's iteration throws, hitting the catch → LogIterationError.
    var badDirect = "Host=__whizbang-nonexistent-host__;Database=x;Username=u;Password=p;Timeout=2;Command Timeout=2";
    var cfg = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> {
      ["ConnectionStrings:test-db-direct"] = badDirect,
    }).Build();

    var notifyOptions = new WhizbangNotificationOptions {
      ConnectionStringKey = "test-db",
      SignalingMode = WorkSignalingMode.Auto,
    };
    var stamperOptions = new CommitOrderStamperOptions {
      LeaderElectionRetry = TimeSpan.FromMilliseconds(50),
    };

    var sharedConn = new PgSharedNotifyConnection(
      Options.Create(notifyOptions),
      cfg,
      new ServiceInstanceProvider(cfg),
      Microsoft.Extensions.Logging.Abstractions.NullLogger<PgSharedNotifyConnection>.Instance,
      connectionStringFallback: null);

    var logger = new _StamperCapturingLogger();
    var worker = new PgCommitOrderStamperWorker(
      Options.Create(notifyOptions),
      Options.Create(stamperOptions),
      cfg,
      sharedConn,
      logger,
      connectionStringFallback: null);

    using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
    await worker.StartAsync(cts.Token);

    await logger.IterationFailedTcs.Task.WaitAsync(TimeSpan.FromSeconds(10));

    try { await worker.StopAsync(CancellationToken.None); } catch { /* shutdown */ }

    var msg = logger.LastIterationFailedMessage;
    await Assert.That(msg).IsNotNull();
    await Assert.That(msg!).Contains("DirectKey");
    await Assert.That(msg!).Contains("test-db");
  }

  [Test]
  public async Task Startup_LogStarted_NamesSourceAndKeyAsync() {
    var goodDirect = "Host=__whizbang-nonexistent-host__;Database=x;Username=u;Password=p;Timeout=2;Command Timeout=2";
    var cfg = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> {
      ["ConnectionStrings:test-db-direct"] = goodDirect,
    }).Build();

    var notifyOptions = new WhizbangNotificationOptions {
      ConnectionStringKey = "test-db",
      SignalingMode = WorkSignalingMode.Auto,
    };
    var stamperOptions = new CommitOrderStamperOptions {
      LeaderElectionRetry = TimeSpan.FromMilliseconds(50),
    };

    var sharedConn = new PgSharedNotifyConnection(
      Options.Create(notifyOptions),
      cfg,
      new ServiceInstanceProvider(cfg),
      Microsoft.Extensions.Logging.Abstractions.NullLogger<PgSharedNotifyConnection>.Instance,
      connectionStringFallback: null);

    var logger = new _StamperCapturingLogger();
    var worker = new PgCommitOrderStamperWorker(
      Options.Create(notifyOptions),
      Options.Create(stamperOptions),
      cfg,
      sharedConn,
      logger,
      connectionStringFallback: null);

    using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
    await worker.StartAsync(cts.Token);
    await logger.StartedLoggedTcs.Task.WaitAsync(TimeSpan.FromSeconds(10));
    try { await worker.StopAsync(CancellationToken.None); } catch { /* shutdown */ }

    await Assert.That(logger.LastStartedMessage).IsNotNull();
    await Assert.That(logger.LastStartedMessage!).Contains("DirectKey");
    await Assert.That(logger.LastStartedMessage!).Contains("test-db");
  }

  [Test]
  public async Task Startup_PooledFallback_LogsLoudPooledWarningAsync() {
    var badPooled = "Host=__whizbang-nonexistent-host__;Database=x;Username=u;Password=p;Timeout=2;Command Timeout=2";
    var cfg = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> {
      ["ConnectionStrings:test-db"] = badPooled,
    }).Build();
    var notifyOptions = new WhizbangNotificationOptions {
      ConnectionStringKey = "test-db",
      SignalingMode = WorkSignalingMode.Auto,
    };
    var stamperOptions = new CommitOrderStamperOptions {
      LeaderElectionRetry = TimeSpan.FromMilliseconds(50),
    };

    var sharedConn = new PgSharedNotifyConnection(
      Options.Create(notifyOptions),
      cfg,
      new ServiceInstanceProvider(cfg),
      Microsoft.Extensions.Logging.Abstractions.NullLogger<PgSharedNotifyConnection>.Instance,
      connectionStringFallback: null);

    var logger = new _StamperCapturingLogger();
    var worker = new PgCommitOrderStamperWorker(
      Options.Create(notifyOptions),
      Options.Create(stamperOptions),
      cfg,
      sharedConn,
      logger,
      connectionStringFallback: null);

    using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
    await worker.StartAsync(cts.Token);
    await logger.PooledFallbackWarningTcs.Task.WaitAsync(TimeSpan.FromSeconds(10));
    try { await worker.StopAsync(CancellationToken.None); } catch { /* shutdown */ }

    await Assert.That(logger.LastPooledFallbackMessage).IsNotNull();
    await Assert.That(logger.LastPooledFallbackMessage!).Contains("pgbouncer");
    await Assert.That(logger.LastPooledFallbackMessage!).Contains("test-db-direct");
    await Assert.That(logger.LastPooledFallbackWarningLevel).IsEqualTo(LogLevel.Warning);
  }

  private sealed class _StamperCapturingLogger : ILogger<PgCommitOrderStamperWorker> {
    public TaskCompletionSource IterationFailedTcs { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public TaskCompletionSource StartedLoggedTcs { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public TaskCompletionSource PooledFallbackWarningTcs { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public string? LastIterationFailedMessage { get; private set; }
    public string? LastStartedMessage { get; private set; }
    public string? LastPooledFallbackMessage { get; private set; }
    public LogLevel? LastPooledFallbackWarningLevel { get; private set; }

    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
    public bool IsEnabled(LogLevel logLevel) => true;

    public void Log<TState>(
      LogLevel logLevel,
      EventId eventId,
      TState state,
      Exception? exception,
      Func<TState, Exception?, string> formatter) {
      switch (eventId.Id) {
        case 6:
          LastIterationFailedMessage = formatter(state, exception);
          _ = IterationFailedTcs.TrySetResult();
          break;
        case 3:
          // EventId 3 is LogStarted (startup connection-resolved info).
          LastStartedMessage = formatter(state, exception);
          _ = StartedLoggedTcs.TrySetResult();
          break;
        case 13:
          // EventId 13 is the pooled-fallback warning (startup).
          LastPooledFallbackMessage = formatter(state, exception);
          LastPooledFallbackWarningLevel = logLevel;
          _ = PooledFallbackWarningTcs.TrySetResult();
          break;
      }
    }
  }
}
