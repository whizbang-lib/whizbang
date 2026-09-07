using Dapper;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Npgsql;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Perspectives;
using Whizbang.Data.Dapper.Postgres;

namespace Whizbang.Data.Dapper.Postgres.Tests;

/// <summary>
/// Targeted coverage for the debug-logging branches of <see cref="DapperPerspectiveStreamLocker"/> that
/// the sibling suite (Perspectives/DapperPerspectiveStreamLockerTests.cs) never reaches: every test
/// there constructs the locker with no logger, so <c>logger?.IsEnabled(LogLevel.Debug) == true</c> is
/// always false and the acquired/not-acquired/released log calls never run. These tests supply a real
/// <see cref="ILogger{TCategoryName}"/> whose <c>IsEnabled</c> returns true and capture the formatted
/// message produced by each <c>[LoggerMessage]</c> call, so the assertions verify the log body actually
/// executed rather than only that the null-conditional receiver was evaluated.
/// Uses <see cref="PostgresTestBase"/> (same fixture pattern as the sibling suite) against a real
/// PostgreSQL instance, since every locker method executes a live UPDATE against
/// wh_perspective_cursors.
/// </summary>
public class DapperPerspectiveStreamLockerCoverageTests : IDisposable {
  private TestFixture _testBase = null!;
  private PerspectiveStreamLockOptions _lockOptions = null!;

  [Before(Test)]
  public async Task SetupAsync() {
    _testBase = new TestFixture();
    await _testBase.SetupAsync();
    _lockOptions = new PerspectiveStreamLockOptions {
      LockTimeout = TimeSpan.FromSeconds(30),
      KeepAliveInterval = TimeSpan.FromSeconds(10)
    };
  }

  public void Dispose() {
    _testBase?.DisposeAsync().AsTask().GetAwaiter().GetResult();
    GC.SuppressFinalize(this);
  }

  [After(Test)]
  public async Task CleanupAsync() {
    await _testBase.DisposeAsync();
  }

  /// <summary>Captures the fully formatted message of every log call, and reports itself enabled for
  /// every level so the locker's `logger?.IsEnabled(LogLevel.Debug) == true` guard passes.</summary>
  private sealed class _capturingLogger : ILogger<DapperPerspectiveStreamLocker> {
    private readonly List<string> _messages = [];
    public List<string> Messages => _messages;
    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
    public bool IsEnabled(LogLevel logLevel) => true;
    public void Log<TState>(
        LogLevel logLevel, Microsoft.Extensions.Logging.EventId eventId, TState state, Exception? exception,
        Func<TState, Exception?, string> formatter) {
      lock (_messages) { _messages.Add(formatter(state, exception)); }
    }
  }

  // --- TryAcquireLockAsync debug logging (lines 48-53) ---

  /// <summary>
  /// If the acquired-branch log call regresses (or the guard around it stops evaluating true), an
  /// operator debugging a stuck rewind/bootstrap/purge loses the one line that proves which instance
  /// actually holds a given stream's lock and why it asked for it.
  /// </summary>
  [Test]
  public async Task TryAcquireLockAsync_DebugEnabledAndAcquired_LogsAcquisitionWithDetailAsync() {
    var streamId = Guid.CreateVersion7();
    const string perspectiveName = "CoverageAcquiredPerspective";
    var instanceId = Guid.CreateVersion7();
    await _insertCursorRowAsync(streamId, perspectiveName);
    var logger = new _capturingLogger();
    var locker = new DapperPerspectiveStreamLocker(_testBase.TestConnectionString, Options.Create(_lockOptions), logger);

    var acquired = await locker.TryAcquireLockAsync(streamId, perspectiveName, instanceId, "coverage-rewind");

    await Assert.That(acquired).IsTrue();
    await Assert.That(logger.Messages).Count().IsEqualTo(1)
      .Because("exactly one debug log call must fire when the lock is successfully acquired");
    var message = logger.Messages[0];
    await Assert.That(message).Contains("Stream lock acquired for " + perspectiveName)
      .Because("the acquired-branch message template must actually run, not just the null-conditional receiver");
    await Assert.That(message).Contains(streamId.ToString());
    await Assert.That(message).Contains(instanceId.ToString());
    await Assert.That(message).Contains("coverage-rewind");
    await Assert.That(message).DoesNotContain("NOT acquired")
      .Because("the acquired and not-acquired branches must produce distinct, non-overlapping messages");
  }

  /// <summary>
  /// If the not-acquired-branch log call regresses, an operator sees a failed lock attempt with no trace
  /// of which instance or reason lost the race — the exact signal needed to tell contention apart from a
  /// genuine bug.
  /// </summary>
  [Test]
  public async Task TryAcquireLockAsync_DebugEnabledAndNotAcquired_LogsContentionWithDetailAsync() {
    var streamId = Guid.CreateVersion7();
    const string perspectiveName = "CoverageContendedPerspective";
    var instanceA = Guid.CreateVersion7();
    var instanceB = Guid.CreateVersion7();
    await _insertCursorRowAsync(streamId, perspectiveName);
    var holderLocker = new DapperPerspectiveStreamLocker(_testBase.TestConnectionString, Options.Create(_lockOptions));
    await holderLocker.TryAcquireLockAsync(streamId, perspectiveName, instanceA, "held-by-a");

    var logger = new _capturingLogger();
    var contendingLocker = new DapperPerspectiveStreamLocker(_testBase.TestConnectionString, Options.Create(_lockOptions), logger);
    var acquired = await contendingLocker.TryAcquireLockAsync(streamId, perspectiveName, instanceB, "coverage-bootstrap");

    await Assert.That(acquired).IsFalse();
    await Assert.That(logger.Messages).Count().IsEqualTo(1)
      .Because("exactly one debug log call must fire when the lock attempt loses the race");
    var message = logger.Messages[0];
    await Assert.That(message).Contains("Stream lock NOT acquired for " + perspectiveName)
      .Because("the not-acquired-branch message template must actually run, not just the null-conditional receiver");
    await Assert.That(message).Contains(instanceB.ToString());
    await Assert.That(message).Contains("coverage-bootstrap");
    await Assert.That(message).Contains("held by another instance");
  }

  // --- ReleaseLockAsync debug logging (lines 101-102) ---

  /// <summary>
  /// If the release log call regresses, there is no durable trace that a lock was ever released — an
  /// operator investigating why a stream's lock reappeared unexpectedly loses the one line that
  /// distinguishes "released cleanly" from "expired" or "never acquired".
  /// </summary>
  [Test]
  public async Task ReleaseLockAsync_DebugEnabled_LogsReleaseWithDetailAsync() {
    var streamId = Guid.CreateVersion7();
    const string perspectiveName = "CoverageReleasedPerspective";
    var instanceId = Guid.CreateVersion7();
    await _insertCursorRowAsync(streamId, perspectiveName);
    var logger = new _capturingLogger();
    var locker = new DapperPerspectiveStreamLocker(_testBase.TestConnectionString, Options.Create(_lockOptions), logger);
    await locker.TryAcquireLockAsync(streamId, perspectiveName, instanceId, "coverage-purge");

    await locker.ReleaseLockAsync(streamId, perspectiveName, instanceId);

    var releaseMessage = logger.Messages.Single(m => m.StartsWith("Stream lock released", StringComparison.Ordinal));
    await Assert.That(releaseMessage).Contains("Stream lock released for " + perspectiveName)
      .Because("the release log call must actually run, not just the null-conditional receiver");
    await Assert.That(releaseMessage).Contains(streamId.ToString());
    await Assert.That(releaseMessage).Contains(instanceId.ToString());
  }

  private async Task _insertCursorRowAsync(Guid streamId, string perspectiveName) {
    await using var connection = new NpgsqlConnection(_testBase.TestConnectionString);
    await connection.OpenAsync();
    await connection.ExecuteAsync(
      "INSERT INTO wh_perspective_cursors (stream_id, perspective_name, status) VALUES (@StreamId, @PerspectiveName, 0)",
      new { StreamId = streamId, PerspectiveName = perspectiveName });
  }

  private sealed class TestFixture : PostgresTestBase {
    public string TestConnectionString => ConnectionString;
  }
}
