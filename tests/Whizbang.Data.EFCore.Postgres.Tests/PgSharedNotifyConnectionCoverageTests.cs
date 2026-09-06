using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Npgsql;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Notifications;
using Whizbang.Core.Observability;
using Whizbang.Data.Postgres.Notifications;

namespace Whizbang.Data.EFCore.Postgres.Tests;

/// <summary>
/// Coverage-round tests for <see cref="PgSharedNotifyConnection"/> paths that need a live
/// Postgres session: one bad LISTEN not taking down the others, the alive-lock's two failure
/// shapes (already held elsewhere vs. the claim function missing), and the keepalive actually
/// running during an idle period.
/// </summary>
/// <code-under-test>src/Whizbang.Data.Postgres/Notifications/PgSharedNotifyConnection.cs</code-under-test>
[Category("Shard1")]
public class PgSharedNotifyConnectionCoverageTests : EFCoreTestBase {

  private PgSharedNotifyConnection _sharedConnection(
      ILogger<PgSharedNotifyConnection> logger, IServiceInstanceProvider? instanceProvider = null) {
    var cfg = new ConfigurationBuilder().AddInMemoryCollection([]).Build();
    return new PgSharedNotifyConnection(
      Options.Create(new WhizbangNotificationOptions {
        DirectConnectionString = ConnectionString,
        SignalingMode = WorkSignalingMode.ListenNotify,
        SelfTestTimeout = TimeSpan.FromSeconds(5),
      }),
      cfg,
      instanceProvider ?? new ServiceInstanceProvider(cfg),
      logger,
      connectionStringFallback: null,
      timeProvider: null);
  }

  /// <summary>
  /// Blocks until <paramref name="gate"/> reports available, via the availability event rather
  /// than a poll — the transition is a real event this class fires exactly once per change.
  /// </summary>
  private static async Task _awaitAvailableAsync(
      PgSharedNotifyConnection gate, CancellationToken cancellationToken) {
    if (gate.IsAvailable) {
      return;
    }
    var tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    void OnChanged(bool available) {
      if (available) {
        tcs.TrySetResult();
      }
    }
    gate.OnAvailabilityChanged += OnChanged;
    try {
      if (gate.IsAvailable) {
        return;
      }
      await tcs.Task.WaitAsync(TimeSpan.FromSeconds(15), cancellationToken);
    } finally {
      gate.OnAvailabilityChanged -= OnChanged;
    }
  }

  // A subscriber's channel name is producer-controlled, and PgSharedNotifyConnection never
  // sanitizes it before splicing it into `LISTEN "{channel}"`. If a bad name (or a bug in a
  // caller building one) threw the resync pass off its feet instead of being caught per
  // channel, one unrelated bad subscription would silently stop this pod from LISTENing on
  // EVERY channel it owns -- every other consumer on the pod would stop receiving
  // notifications, with no indication why.
  [Test]
  [Timeout(60000)]
  public async Task SyncListens_OneChannelHasMalformedName_OtherChannelStillListensAsync(
      CancellationToken cancellationToken) {
    var logger = new _CapturingLogger();
    using var shared = _sharedConnection(logger);

    var goodChannel = $"wh_cov_good_{Guid.NewGuid():N}";
    // The embedded double-quote is never escaped by PgSharedNotifyConnection before it builds
    // LISTEN "{channel}", so this turns into invalid SQL and the LISTEN attempt must fail.
    var badChannel = $"wh_cov_bad_{Guid.NewGuid():N}\"x";
    using var goodHandle = shared.Subscribe(new _NoopSubscription(goodChannel));
    using var badHandle = shared.Subscribe(new _NoopSubscription(badChannel));

    await shared.StartAsync(cancellationToken);
    try {
      var goodListened = shared.WaitForChannelListenedAsync(goodChannel, cancellationToken);
      var badLogged = logger.WaitForCountAsync(9, 1, TimeSpan.FromSeconds(15));
      await goodListened.WaitAsync(TimeSpan.FromSeconds(15), cancellationToken);
      var badMessages = await badLogged;

      await Assert.That(shared.ListenedChannelsForTesting).Contains(goodChannel)
        .Because("a malformed channel elsewhere must not stop this pod from listening on "
               + "every other, well-formed channel");
      await Assert.That(shared.ListenedChannelsForTesting).DoesNotContain(badChannel)
        .Because("a channel whose LISTEN never succeeded must not be reported as listened");
      await Assert.That(badMessages[0].Message).Contains(badChannel)
        .Because("the failure log must name the exact channel that failed so an operator can "
               + "trace it back to the subscriber that produced the bad name");
    } finally {
      await shared.StopAsync(CancellationToken.None);
    }
  }

  // HeartbeatWorker reads IsAliveLockHeld to choose the fast (5 s) vs. slow (60 s) heartbeat
  // cadence. If a session that lost the race for the advisory lock quietly believed it held it
  // anyway, a duplicate-startup pod would take the fast cadence on false pretenses -- and worse,
  // peers reading is_instance_alive() would see the lock held by a DIFFERENT session than the
  // one actually reporting itself alive, defeating the sub-second death detection the lock
  // exists for.
  [Test]
  [Timeout(60000)]
  public async Task AliveLock_AlreadyHeldByAnotherSession_ReportsNotAcquiredAsync(
      CancellationToken cancellationToken) {
    var sharedInstanceProvider = new ServiceInstanceProvider(
      Guid.NewGuid(), "coverage-svc", "coverage-host", Environment.ProcessId);

    var loggerA = new _CapturingLogger();
    using var gateA = _sharedConnection(loggerA, sharedInstanceProvider);
    await gateA.StartAsync(cancellationToken);
    try {
      await _awaitAvailableAsync(gateA, cancellationToken);
      await Assert.That(gateA.IsAliveLockHeld).IsTrue()
        .Because("nothing else holds the lock yet, so the first session must claim it");

      var loggerB = new _CapturingLogger();
      using var gateB = _sharedConnection(loggerB, sharedInstanceProvider);
      await gateB.StartAsync(cancellationToken);
      try {
        await _awaitAvailableAsync(gateB, cancellationToken);
        var messages = await loggerB.WaitForCountAsync(100, 1, TimeSpan.FromSeconds(15));

        await Assert.That(gateB.IsAliveLockHeld).IsFalse()
          .Because("a second session claiming the same instance id's lock must be told it "
                 + "does not hold it, not silently believe it does");
        await Assert.That(messages[0].Message).Contains("duplicate-startup")
          .Because("losing the lock race must be reported, not swallowed -- an operator "
                 + "watching logs needs to see the duplicate-startup race happened at all");
      } finally {
        await gateB.StopAsync(CancellationToken.None);
      }
    } finally {
      await gateA.StopAsync(CancellationToken.None);
    }
  }

  // The claim can also fail outright (e.g. an older schema that predates migration 055). If
  // that exception were swallowed without a trace, IsAliveLockHeld would sit at false and look
  // IDENTICAL to "another session holds it" -- two failure modes that need completely different
  // remediation (nothing to do, vs. run the missing migration) collapse into one silent state.
  [Test]
  [Timeout(60000)]
  public async Task AliveLock_ClaimFunctionMissing_ReportsClaimFailedAsync(
      CancellationToken cancellationToken) {
    await using (var dbContext = CreateDbContext()) {
      await dbContext.Database.ExecuteSqlRawAsync(
        "DROP FUNCTION claim_instance_alive_lock(uuid)", cancellationToken);
    }

    var logger = new _CapturingLogger();
    using var gate = _sharedConnection(logger);
    await gate.StartAsync(cancellationToken);
    try {
      await _awaitAvailableAsync(gate, cancellationToken);
      var messages = await logger.WaitForCountAsync(101, 1, TimeSpan.FromSeconds(15));

      await Assert.That(gate.IsAliveLockHeld).IsFalse()
        .Because("a claim that could not even run must not be reported as a held lock");
      // LogAliveLockClaimFailed's fixed template names no specifics; the function name and
      // "does not exist" only show up in the exception it logs alongside the message.
      await Assert.That(messages[0].Exception).IsNotNull();
      await Assert.That(messages[0].Exception!.Message).Contains("claim_instance_alive_lock")
        .Because("the operator needs to see which function failed, not just that something "
               + "did -- 'no lock, no reason' sends them looking in the wrong place");
    } finally {
      await gate.StopAsync(CancellationToken.None);
    }
  }

  // If the keepalive stopped firing during an idle LISTEN period, a session sitting behind a
  // NAT, load balancer, or pgbouncer idle-eviction policy that silently drops quiet connections
  // would look alive to this process long after the network actually cut it -- the pod would
  // keep believing notifications are flowing when nothing is listening on the other end.
  [Test]
  [Timeout(60000)]
  public async Task Keepalive_DuringIdlePeriod_IssuesSelectOneAndStaysAvailableAsync(
      CancellationToken cancellationToken) {
    var instanceProvider = new ServiceInstanceProvider(
      Guid.NewGuid(), "coverage-svc", "coverage-host", Environment.ProcessId);
    var cfg = new ConfigurationBuilder().AddInMemoryCollection([]).Build();
    var logger = new _CapturingLogger();
    using var gate = new PgSharedNotifyConnection(
      Options.Create(new WhizbangNotificationOptions {
        DirectConnectionString = ConnectionString,
        SignalingMode = WorkSignalingMode.ListenNotify,
        SelfTestTimeout = TimeSpan.FromSeconds(5),
        ListenKeepaliveInterval = TimeSpan.FromMilliseconds(200),
      }),
      cfg,
      instanceProvider,
      logger,
      connectionStringFallback: null,
      timeProvider: null);

    await gate.StartAsync(cancellationToken);
    try {
      await _awaitAvailableAsync(gate, cancellationToken);

      // Idle well past several keepalive intervals. Nothing else touches this connection
      // during the window (no subscribers, no notifications), so any SELECT 1 that ran can
      // only be the keepalive branch.
      await Task.Delay(TimeSpan.FromMilliseconds(1200), cancellationToken);

      var appName = PgSharedNotifyConnection.ComputeApplicationName(instanceProvider.InstanceId);
      await using var admin = new NpgsqlConnection(ConnectionString);
      await admin.OpenAsync(cancellationToken);
      await using var cmd = admin.CreateCommand();
      cmd.CommandText =
        "SELECT query, state FROM pg_stat_activity WHERE application_name = @appName";
      cmd.Parameters.AddWithValue("@appName", appName);
      await using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
      var found = await reader.ReadAsync(cancellationToken);

      await Assert.That(found).IsTrue()
        .Because("the shared connection's own backend row must still exist -- a plain idle "
               + "period must not have dropped and reopened it");
      var lastQuery = reader.GetString(0);
      var state = reader.GetString(1);
      await Assert.That(state).IsEqualTo("idle")
        .Because("the keepalive round-trip must complete and return the session to idle, not "
               + "leave it stuck mid-query");
      await Assert.That(lastQuery).Contains("SELECT 1")
        .Because("pg_stat_activity retains the last statement even once idle -- this is the "
               + "only positive evidence the keepalive ping actually ran during the idle "
               + "window, rather than the session merely not having been closed yet");
      await Assert.That(gate.IsAvailable).IsTrue()
        .Because("a healthy keepalive round-trip must not flip the gate into believing the "
               + "connection died");
    } finally {
      await gate.StopAsync(CancellationToken.None);
    }
  }

  private sealed class _NoopSubscription(string channel) : INotifySubscription {
    public string ChannelName => channel;
    public void OnNotification(string payload) { }
  }
}

/// <summary>
/// Reconnect-backoff coverage for <see cref="PgSharedNotifyConnection"/> that needs no
/// database: an unresolvable host drives fast, repeated reconnect failures, and the capturing
/// logger reads the delay <c>LogReconnect</c> reports on each attempt.
/// </summary>
/// <code-under-test>src/Whizbang.Data.Postgres/Notifications/PgSharedNotifyConnection.cs</code-under-test>
[Category("Shard1")]
public class PgSharedNotifyConnectionBackoffCoverageTests {

  // If backoff never stretched past ListenReconnectMaxDelay once FailuresBeforeFallback is
  // reached, a genuinely broken NOTIFY path (misconfigured pgbouncer, dead network) would keep
  // this pod hammering Postgres with a reconnect attempt every few seconds indefinitely,
  // instead of backing off to the slow PeriodicReprobeInterval cadence the fallback design
  // exists to provide.
  [Test]
  public async Task ComputeBackoff_AfterFailuresBeforeFallback_StretchesToPeriodicReprobeIntervalAsync() {
    // Deliberately-unresolvable host, same technique as the reconnect-diagnostics regression
    // lock: guarantees the connect attempt fails at the network layer within milliseconds, on
    // every attempt, without needing a real database.
    var badDirect = "Host=__whizbang-nonexistent-host__;Database=x;Username=u;Password=p;Timeout=2;Command Timeout=2";
    var cfg = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> {
      ["ConnectionStrings:test-db-direct"] = badDirect,
    }).Build();
    var options = new WhizbangNotificationOptions {
      ConnectionStringKey = "test-db",
      SignalingMode = WorkSignalingMode.Auto,
      ListenReconnectInitialDelay = TimeSpan.FromMilliseconds(10),
      ListenReconnectMaxDelay = TimeSpan.FromMilliseconds(10),
      ListenReconnectBackoffMultiplier = 1.0,
      SelfTestTimeout = TimeSpan.FromMilliseconds(200),
      FailuresBeforeFallback = 2,
      PeriodicReprobeInterval = TimeSpan.FromSeconds(2),
    };

    var logger = new _CapturingLogger();
    var worker = new PgSharedNotifyConnection(
      Options.Create(options),
      cfg,
      new ServiceInstanceProvider(cfg),
      logger,
      connectionStringFallback: null);

    await worker.StartAsync(CancellationToken.None);
    try {
      // Two consecutive disconnects: the first below FailuresBeforeFallback (short cadence),
      // the second at the threshold (stretched cadence).
      var messages = await logger.WaitForCountAsync(3, 2, TimeSpan.FromSeconds(15));

      await Assert.That(messages[0].Message).Contains("reconnecting in 0.01s")
        .Because("below the fallback threshold, backoff must still use the short reconnect "
               + "cadence");
      await Assert.That(messages[1].Message).Contains("reconnecting in 2s")
        .Because("once FailuresBeforeFallback consecutive failures accumulate, backoff must "
               + "stretch to PeriodicReprobeInterval rather than keep hammering Postgres at "
               + "the short reconnect cadence");
    } finally {
      try {
        await worker.StopAsync(CancellationToken.None);
      } catch {
        // Shutdown best-effort; the assertions above are already done.
      }
    }
  }
}

/// <summary>
/// One captured log call: the fully formatted message (template placeholders substituted) and
/// the separately-passed exception, if any. <c>[LoggerMessage]</c> methods that take a trailing
/// <see cref="Exception"/> parameter do NOT fold its message into the formatted string unless
/// the template explicitly references it, so callers that need exception detail must read
/// <see cref="Exception"/> rather than search <see cref="Message"/>.
/// </summary>
internal readonly record struct LogEntry(string Message, Exception? Exception);

/// <summary>
/// Shared capturing <see cref="ILogger{TCategoryName}"/> for the coverage tests in this file.
/// Records every log call per event id and exposes a deterministic wait for the N-th
/// occurrence of a given event id, so tests never poll or sleep to observe a log.
/// </summary>
internal sealed class _CapturingLogger : ILogger<PgSharedNotifyConnection> {
  private readonly Lock _gate = new();
  private readonly Dictionary<int, List<LogEntry>> _byEventId = [];
  private Action<int>? _onLogged;

  public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

  public bool IsEnabled(LogLevel logLevel) => true;

  public void Log<TState>(
      LogLevel logLevel,
      Microsoft.Extensions.Logging.EventId eventId,
      TState state,
      Exception? exception,
      Func<TState, Exception?, string> formatter) {
    var entry = new LogEntry(formatter(state, exception), exception);
    lock (_gate) {
      if (!_byEventId.TryGetValue(eventId.Id, out var list)) {
        list = [];
        _byEventId[eventId.Id] = list;
      }
      list.Add(entry);
    }
    _onLogged?.Invoke(eventId.Id);
  }

  /// <summary>
  /// Waits until at least <paramref name="count"/> entries have been logged for
  /// <paramref name="eventId"/>, then returns the entries captured so far (at least
  /// <paramref name="count"/> of them, in log order). Throws on <paramref name="timeout"/>
  /// rather than hanging when the expected log never fires.
  /// </summary>
  public async Task<IReadOnlyList<LogEntry>> WaitForCountAsync(int eventId, int count, TimeSpan timeout) {
    var tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    void CheckThreshold(int loggedEventId) {
      if (loggedEventId != eventId) {
        return;
      }
      lock (_gate) {
        if (_byEventId.TryGetValue(eventId, out var list) && list.Count >= count) {
          tcs.TrySetResult();
        }
      }
    }

    lock (_gate) {
      if (_byEventId.TryGetValue(eventId, out var existing) && existing.Count >= count) {
        return [.. existing];
      }
      _onLogged += CheckThreshold;
    }
    try {
      await tcs.Task.WaitAsync(timeout);
    } finally {
      lock (_gate) {
        _onLogged -= CheckThreshold;
      }
    }
    lock (_gate) {
      return [.. _byEventId[eventId]];
    }
  }
}
