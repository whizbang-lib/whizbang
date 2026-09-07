using System.Net;
using System.Net.Sockets;
using System.Reflection;
using Microsoft.Extensions.Logging;
using Npgsql;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Data.Postgres;

namespace Whizbang.Data.EFCore.Postgres.Tests;

/// <summary>
/// Coverage for the logging paths of <see cref="PostgresConnectionRetry"/> that the existing
/// <c>PostgresConnectionRetryTests</c> never exercise: every log call there sits behind
/// <c>if (_logger is not null)</c>, and none of those tests pass a logger. A retry that eventually
/// succeeds is invisible to its caller (it just returns), so these log lines are the only record an
/// operator has that the database briefly struggled; indefinite retry also needs its own heartbeat
/// so "still trying" reads differently from "stuck." Also covers the two transient-exception
/// classifications that need a raw socket/IO failure rather than an Npgsql-wrapped one --
/// <c>_isTransientNpgsqlException</c>'s message checks (<c>"refused"</c>, <c>"timeout"</c>,
/// <c>"connection"</c>) already short-circuit the closed-port scenario the rest of the suite uses
/// everywhere else, so the raw-type fallback never gets exercised by it.
/// </summary>
[Category("Shard1")]
public class PostgresConnectionRetryCoverageTests : EFCoreTestBase {

  /// <summary>Reserves a currently-free loopback port without listening on it -- a "closed port"
  /// (connecting fails immediately with no listener, unlike a filtered port that would hang).</summary>
  private static int _reserveClosedPort() {
    var probe = new TcpListener(IPAddress.Loopback, 0);
    probe.Start();
    var port = ((IPEndPoint)probe.LocalEndpoint).Port;
    probe.Stop();
    return port;
  }

  /// <summary>Builds a connection string that targets <paramref name="relay"/>'s loopback port
  /// instead of the real fixture host/port, with pooling off so each test's physical connection is
  /// unambiguously torn down when the relay is disposed.</summary>
  private string _throughRelay(_flakyRelay relay) => new NpgsqlConnectionStringBuilder(ConnectionString) {
    Host = "127.0.0.1",
    Port = relay.Port,
    Pooling = false,
  }.ConnectionString;

  /// <summary>Captures every log entry and, optionally, reacts to one as it arrives -- synchronously,
  /// from inside the SUT's own catch block. That synchronous callback is the deterministic signal
  /// this file uses instead of sleeping to know exactly when a failed attempt has been logged.</summary>
  private sealed class _capturingLogger : ILogger {
    private readonly List<(LogLevel Level, string? Message, Exception? Exception)> _entries = [];

    public Action<(LogLevel Level, string? Message, Exception? Exception)>? OnEntry { get; set; }

    public IReadOnlyList<(LogLevel Level, string? Message, Exception? Exception)> Entries {
      get { lock (_entries) { return [.. _entries]; } }
    }

    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
    public bool IsEnabled(LogLevel logLevel) => true;

    public void Log<TState>(LogLevel logLevel, Microsoft.Extensions.Logging.EventId eventId, TState state, Exception? exception,
        Func<TState, Exception?, string> formatter) {
      var entry = (logLevel, formatter(state, exception), exception);
      lock (_entries) { _entries.Add(entry); }
      OnEntry?.Invoke(entry);
    }
  }

  /// <summary>
  /// A byte-for-byte TCP relay standing in for "the database was unreachable, then came back."
  /// <see cref="PostgresConnectionRetry"/> reuses one connection string for an entire retry call, so
  /// there is no way to point attempt 1 and attempt 2 at different addresses. Instead this reserves
  /// one loopback port that starts closed (nothing listening) and, once <see cref="Start"/> runs,
  /// forwards every subsequent inbound connection byte-for-byte to the real fixture database --
  /// Postgres's wire protocol is never parsed, only relayed, so this works regardless of what the
  /// protocol exchange looks like (auth, SSL negotiation, etc.).
  /// </summary>
  private sealed class _flakyRelay : IDisposable {
    private readonly TcpListener _listener;
    private readonly string _targetHost;
    private readonly int _targetPort;
    private readonly List<TcpClient> _clients = [];
    private readonly Lock _gate = new();
    private bool _started;
    private bool _disposed;

    private _flakyRelay(int port, string targetHost, int targetPort) {
      Port = port;
      _listener = new TcpListener(IPAddress.Loopback, port);
      _targetHost = targetHost;
      _targetPort = targetPort;
    }

    public int Port { get; }

    public static _flakyRelay Create(string targetConnectionString) {
      var target = new NpgsqlConnectionStringBuilder(targetConnectionString);
      return new _flakyRelay(_reserveClosedPort(), target.Host!, target.Port);
    }

    public void Start() {
      if (_started) {
        return;
      }
      _started = true;
      _listener.Start();
      _ = _acceptLoopAsync();
    }

    private async Task _acceptLoopAsync() {
      try {
        while (true) {
          var inbound = await _listener.AcceptTcpClientAsync().ConfigureAwait(false);
          lock (_gate) { _clients.Add(inbound); }
          _ = _relayAsync(inbound);
        }
      } catch {
        // Expected once Dispose() stops the listener.
      }
    }

    private async Task _relayAsync(TcpClient inbound) {
      try {
        using var outbound = new TcpClient();
        await outbound.ConnectAsync(_targetHost, _targetPort).ConfigureAwait(false);
        lock (_gate) { _clients.Add(outbound); }
        var toTarget = inbound.GetStream().CopyToAsync(outbound.GetStream());
        var toCaller = outbound.GetStream().CopyToAsync(inbound.GetStream());
        await Task.WhenAny(toTarget, toCaller).ConfigureAwait(false);
      } catch {
        // Either leg closing ends the relay -- expected once the client or the real database
        // finishes the connection.
      }
    }

    public void Dispose() {
      if (_disposed) {
        return;
      }
      _disposed = true;
      if (_started) {
        _listener.Stop();
      }
      lock (_gate) {
        foreach (var client in _clients) {
          try {
            client.Close();
          } catch {
            // Best-effort teardown.
          }
        }
        _clients.Clear();
      }
    }
  }

  // ---- WaitForConnectionAsync / WaitForSchemaReadyAsync: logging the recovery, not just the
  // ---- failure -----------------------------------------------------------------------------

  [Test]
  [Timeout(30000)]
  public async Task WaitForConnectionAsync_LogsConnectionEstablished_AfterATransientFailureThenSuccessAsync(
      CancellationToken testToken) {
    // A retry that ultimately succeeds returns exactly like a connection that always worked -- the
    // caller sees nothing. If this Information log regresses, an operator loses the only record
    // that the database was flapping: "we reconnected 40 times overnight" silently becomes "the
    // database was rock solid," and nobody investigates before it becomes an outage.
    using var relay = _flakyRelay.Create(ConnectionString);
    var logger = new _capturingLogger {
      OnEntry = entry => {
        if (entry.Level == LogLevel.Warning) {
          relay.Start();
        }
      },
    };

    var options = new PostgresOptions {
      InitialRetryAttempts = 5,
      InitialRetryDelay = TimeSpan.FromMilliseconds(20),
      MaxRetryDelay = TimeSpan.FromMilliseconds(50),
      RetryIndefinitely = false,
    };
    var retry = new PostgresConnectionRetry(options, logger);

    await retry.WaitForConnectionAsync(_throughRelay(relay), testToken);

    var established = logger.Entries.SingleOrDefault(e => e.Level == LogLevel.Information);
    await Assert.That(established.Message).IsNotNull()
      .Because("the Information log is the only trace that the connection needed more than one attempt");
    await Assert.That(established.Message!).Contains("after 2 attempts")
      .Because("the attempt count is what tells an operator this was a one-off blip, not chronic instability");
  }

  [Test]
  [Timeout(30000)]
  public async Task WaitForSchemaReadyAsync_LogsSchemaReady_AfterATransientFailureThenSuccessAsync(
      CancellationToken testToken) {
    // Same invariant as connection retry, one layer up: a schema check that recovers is invisible to
    // the caller. Losing this log means an operator can't tell "the check needed one extra pass"
    // from "nothing ever polled." This also exercises the schema wait's own transient-exception
    // branch (LogRetrying) with a real logger attached for the first time -- the database
    // disappearing between the connection wait and the schema wait is exactly the case that branch
    // exists for.
    using var relay = _flakyRelay.Create(ConnectionString);
    var logger = new _capturingLogger {
      OnEntry = entry => {
        if (entry.Level == LogLevel.Warning) {
          relay.Start();
        }
      },
    };

    var options = new PostgresOptions {
      InitialRetryAttempts = 5,
      InitialRetryDelay = TimeSpan.FromMilliseconds(20),
      MaxRetryDelay = TimeSpan.FromMilliseconds(50),
      RetryIndefinitely = false,
    };
    var retry = new PostgresConnectionRetry(options, logger);

    await retry.WaitForSchemaReadyAsync(_throughRelay(relay), testToken);

    var retryWarning = logger.Entries.SingleOrDefault(e => e.Level == LogLevel.Warning);
    await Assert.That(retryWarning.Message).IsNotNull()
      .Because("the transient failure during the schema wait must be reported, not just the eventual success");

    var established = logger.Entries.SingleOrDefault(e => e.Level == LogLevel.Information);
    await Assert.That(established.Message).IsNotNull()
      .Because("the schema-ready recovery is as invisible to the caller as the connection recovery");
    await Assert.That(established.Message!).Contains("after 2 attempts")
      .Because("the attempt count is what tells an operator this was a one-off blip, not chronic instability");
  }

  // ---- indefinite retry: the heartbeat -------------------------------------------------------

  [Test]
  [Timeout(30000)]
  public async Task WaitForConnectionAsync_LogsStillFailing_OnTheTenthAttemptWhileRetryingIndefinitelyAsync(
      CancellationToken testToken) {
    // Indefinite retry (the default -- "critical infrastructure, always retry") logs a warning on
    // attempt 1 and then goes silent unless this periodic status fires. Without it, an operator
    // watching logs during an outage cannot tell "still retrying every few ms" from "the process
    // wedged" -- both look identical: one warning, then nothing.
    var reachedTenthAttempt = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    var logger = new _capturingLogger {
      OnEntry = entry => {
        if (entry.Level == LogLevel.Warning
            && entry.Message?.Contains("still failing after 10 attempts", StringComparison.Ordinal) == true) {
          reachedTenthAttempt.TrySetResult();
        }
      },
    };

    var closedPort = _reserveClosedPort();
    var badConnectionString =
      $"Host=127.0.0.1;Port={closedPort};Username=nobody;Password=nobody;Database=nothing;Timeout=1";

    var options = new PostgresOptions {
      InitialRetryAttempts = 1,
      InitialRetryDelay = TimeSpan.FromMilliseconds(1),
      MaxRetryDelay = TimeSpan.FromMilliseconds(2),
      RetryIndefinitely = true,
    };
    var retry = new PostgresConnectionRetry(options, logger);

    using var cts = CancellationTokenSource.CreateLinkedTokenSource(testToken);
    var retryTask = retry.WaitForConnectionAsync(badConnectionString, cts.Token);

    await reachedTenthAttempt.Task;
    await cts.CancelAsync();

    await Assert.That(async () => await retryTask).ThrowsException()
      .Because("cancellation is the only way indefinite retry ends -- the loop must actually stop");

    var stillFailing = logger.Entries.LastOrDefault(e => e.Level == LogLevel.Warning
        && e.Message!.Contains("still failing", StringComparison.Ordinal));
    await Assert.That(stillFailing.Message).IsNotNull();
    await Assert.That(stillFailing.Message!).Contains("still failing after 10 attempts")
      .Because("the attempt count is what tells an operator this has been retrying for a while, not just once");
  }

  // ---- transient-exception classification: raw socket/IO types, not Npgsql-wrapped ones -----

  [Test]
  public async Task IsTransientException_ClassifiesARawSocketExceptionAsTransientAsync() {
    // If a bare SocketException ever stopped being retried, a machine-level network blip (as
    // opposed to a Postgres-reported error) would propagate straight to the caller as a fatal
    // failure instead of triggering backoff -- turning a transient network hiccup into a full outage.
    var method = typeof(PostgresConnectionRetry).GetMethod("_isTransientException",
      BindingFlags.NonPublic | BindingFlags.Static);
    await Assert.That(method).IsNotNull()
      .Because("this test targets PostgresConnectionRetry's private classifier by exact name");

    var result = (bool)method!.Invoke(null, [new SocketException((int)SocketError.HostUnreachable)])!;

    await Assert.That(result).IsTrue()
      .Because("a raw SocketException must be retried even when it never went through Npgsql's "
             + "message-based classification");
  }

  [Test]
  public async Task IsTransientException_ClassifiesARawIOExceptionAsTransientAsync() {
    // Same risk as the socket case: a mid-stream read/write failure (the socket dropped while
    // Npgsql was talking to the server) must be retried, not treated as a fatal error.
    var method = typeof(PostgresConnectionRetry).GetMethod("_isTransientException",
      BindingFlags.NonPublic | BindingFlags.Static);
    await Assert.That(method).IsNotNull()
      .Because("this test targets PostgresConnectionRetry's private classifier by exact name");

    var result = (bool)method!.Invoke(null, [new IOException("connection dropped mid-stream")])!;

    await Assert.That(result).IsTrue()
      .Because("a raw IOException must be retried even when its message carries none of the "
             + "'connection'/'timeout'/'refused' keywords the Npgsql-specific check looks for");
  }
}
