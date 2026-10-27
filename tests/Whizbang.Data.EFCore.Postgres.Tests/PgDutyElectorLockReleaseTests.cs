// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Npgsql;
using Whizbang.Core.Messaging;
using Whizbang.Core.Notifications;
using Whizbang.Core.Observability;
using Whizbang.Core.Serialization;
using Whizbang.Core.Startup;
using Whizbang.Data.Postgres.Notifications;

namespace Whizbang.Data.EFCore.Postgres.Tests;

/// <summary>
/// A duty is a session-level advisory lock on a direct connection, held for the duty's tenure. Releasing it checks
/// that the session still held it: a release that finds nothing means the lock went somewhere this session cannot
/// see (a pooler ran the release on another backend), and that is logged with the duty and the key.
/// </summary>
/// <remarks>
/// The lock disappears from under the elector here through a function of the same name earlier on the search path,
/// which releases every advisory lock of the session it runs in before doing the real work. That is the observable
/// shape of a release landing on a backend that never took the lock.
/// </remarks>
/// <code-under-test>src/Whizbang.Data.Postgres/Notifications/PgDutyElector.cs</code-under-test>
[Category("Shard1")]
public class PgDutyElectorLockReleaseTests : EFCoreTestBase {
  private const string PROBE = "wb_unlock_probe";

  [Test]
  [Timeout(60000)]
  public async Task AGrantWhoseLockWasGoneBeforeItsRelease_SaysSoAsync(CancellationToken cancellationToken) {
    await _probeAsync("""
      CREATE OR REPLACE FUNCTION wb_unlock_probe.release_capability(p_instance_id uuid, p_capability text) RETURNS void
      LANGUAGE plpgsql AS $$ BEGIN PERFORM pg_advisory_unlock_all(); PERFORM public.release_capability(p_instance_id, p_capability); END $$
      """, cancellationToken);
    var instance = await _joinFleetAsync(cancellationToken);
    var logger = new CapturingLogger();
    var attempt = await _elector(instance, logger, $"{PROBE}, public").TryAcquireAsync("unlock-probe-duty", cancellationToken);
    await Assert.That(attempt.Grant).IsNotNull();

    await attempt.Grant!.DisposeAsync();

    await Assert.That(logger.Warnings).Contains(m =>
      m.Contains("duty 'unlock-probe-duty'", StringComparison.Ordinal) && m.Contains("not held", StringComparison.Ordinal));
  }

  [Test]
  [Timeout(60000)]
  public async Task ARefusedWinWhoseLockWasAlreadyGone_SaysSoAsync(CancellationToken cancellationToken) {
    await _probeAsync("""
      CREATE OR REPLACE FUNCTION wb_unlock_probe.record_capability(p_instance_id uuid, p_capability text) RETURNS boolean
      LANGUAGE plpgsql AS $$ BEGIN PERFORM pg_advisory_unlock_all(); RETURN false; END $$
      """, cancellationToken);
    var instance = await _joinFleetAsync(cancellationToken);
    var logger = new CapturingLogger();

    var attempt = await _elector(instance, logger, $"{PROBE}, public").TryAcquireAsync("unlock-probe-refused", cancellationToken);

    await Assert.That(attempt.Refusal).IsEqualTo(DutyRefusal.Refused);
    await Assert.That(logger.Warnings).Contains(m =>
      m.Contains("duty 'unlock-probe-refused'", StringComparison.Ordinal) && m.Contains("not held", StringComparison.Ordinal));
  }

  [Test]
  [Timeout(60000)]
  public async Task AGrantReleasedCleanly_FreesTheDuty_AndReportsNoMissingLockAsync(CancellationToken cancellationToken) {
    var first = await _joinFleetAsync(cancellationToken);
    var second = await _joinFleetAsync(cancellationToken);
    var logger = new CapturingLogger();
    var attempt = await _elector(first, logger, null).TryAcquireAsync("unlock-clean-duty", cancellationToken);
    await Assert.That(attempt.Grant).IsNotNull();

    await attempt.Grant!.DisposeAsync();

    await Assert.That(logger.Warnings).IsEmpty()
      .Because("the release ran on the session that took the lock, with the key it was taken with");
    var next = await _elector(second, new CapturingLogger(), null).TryAcquireAsync("unlock-clean-duty", cancellationToken);
    await Assert.That(next.Grant).IsNotNull()
      .Because("a released duty is free for the next instance at once");
    await next.Grant!.DisposeAsync();
  }

  private async Task _probeAsync(string function, CancellationToken cancellationToken) {
    await using var connection = new NpgsqlConnection(ConnectionString);
    await connection.OpenAsync(cancellationToken);
    await using var command = new NpgsqlCommand($"CREATE SCHEMA IF NOT EXISTS {PROBE}; {function}", connection);
    await command.ExecuteNonQueryAsync(cancellationToken);
  }

  private async Task<IServiceInstanceProvider> _joinFleetAsync(CancellationToken cancellationToken) {
    var instance = new ServiceInstanceProvider(Guid.NewGuid(), "unlock-service", "unlock-host", processId: 1);
    await using var ctx = CreateDbContext();
    var coordinator = new EFCoreWorkCoordinator<WorkCoordinationDbContext>(ctx, JsonContextRegistry.CreateCombinedOptions());
    await coordinator.RecordHeartbeatAsync(
      new HeartbeatRequest(instance.InstanceId, instance.ServiceName, instance.HostName, 1), cancellationToken);
    return instance;
  }

  private PgDutyElector _elector(IServiceInstanceProvider instance, ILogger<PgDutyElector> logger, string? searchPath) => new(
    Options.Create(new WhizbangNotificationOptions { DirectConnectionString = ConnectionString, SearchPath = searchPath }),
    new ConfigurationBuilder().AddInMemoryCollection([]).Build(),
    instance,
    logger);

  private sealed class CapturingLogger : ILogger<PgDutyElector> {
    private readonly Lock _lock = new();
    private readonly List<string> _warnings = [];
    public List<string> Warnings { get { lock (_lock) { return [.. _warnings]; } } }
    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
    public bool IsEnabled(LogLevel logLevel) => true;
    public void Log<TState>(LogLevel logLevel, Microsoft.Extensions.Logging.EventId eventId, TState state, Exception? exception,
        Func<TState, Exception?, string> formatter) {
      if (logLevel == LogLevel.Warning) {
        lock (_lock) { _warnings.Add(formatter(state, exception)); }
      }
    }
  }
}
