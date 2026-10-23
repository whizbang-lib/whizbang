// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Whizbang.Core.Messaging;
using Whizbang.Core.Notifications;
using Whizbang.Core.Observability;
using Whizbang.Core.Serialization;
using Whizbang.Core.Startup;
using Whizbang.Data.Postgres.Notifications;

namespace Whizbang.Data.EFCore.Postgres.Tests;

/// <summary>
/// The role elector's bridge is the one place it holds a session-level advisory lock: the duty's legacy lock, held on a
/// connection of its own for the tenure, so instances on the session-lock elector see the role as held. A session lock
/// needs a direct connection, so a bridge that resolves through a pooled key is warned about before it is taken, and
/// a clean release frees the lock for the next holder and reports nothing.
/// </summary>
/// <code-under-test>src/Whizbang.Data.Postgres/Notifications/PgRoleElector.cs</code-under-test>
[Category("Integration")]
[NotInParallel("EFCorePostgresTests")]
[Category("Shard3")]
public class PgRoleElectorBridgeLockTests : EFCoreTestBase {
  private const string ROLE = StartupDuties.MAINTAINER;

  [Test]
  [Timeout(120000)]
  public async Task ABridgedTenureReleasedCleanly_FreesTheSessionLock_AndReportsNoMissingLockAsync(CancellationToken cancellationToken) {
    var pod = await _joinAsync(cancellationToken);
    var logger = new CapturingLogger();
    var notification = new WhizbangNotificationOptions { DirectConnectionString = ConnectionString };
    var attempt = await _bridgedElector(pod, notification, logger).TryAcquireAsync(ROLE, cancellationToken);
    await Assert.That(attempt.Grant).IsNotNull();

    await attempt.Grant!.DisposeAsync();

    await Assert.That(logger.Warnings).IsEmpty()
      .Because("the bridge's release ran on the session that took the lock, with the key it was taken with");
    var sessionLockHolder = await new PgDutyElector(
      Options.Create(notification), _config(), await _joinAsync(cancellationToken), NullLogger<PgDutyElector>.Instance)
      .TryAcquireAsync(ROLE, cancellationToken);
    await Assert.That(sessionLockHolder.Grant).IsNotNull()
      .Because("the released bridge leaves the legacy lock free for an instance on the session-lock elector");
    await sessionLockHolder.Grant!.DisposeAsync();
  }

  [Test]
  public async Task ABridgeResolvedThroughAPooledKey_WarnsBeforeTakingTheSessionLockAsync() {
    var notification = new WhizbangNotificationOptions { ConnectionStringKey = "app-db" };
    var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> {
      // No "app-db-direct" entry, so only the pooled key resolves; the port is closed, so nothing is ever reached.
      ["ConnectionStrings:app-db"] = "Host=127.0.0.1;Port=1;Username=nobody;Password=nobody;Database=nothing;Timeout=1",
    }).Build();
    var logger = new CapturingLogger();
    var pod = new ServiceInstanceProvider(Guid.NewGuid(), "role-svc", "role-host", processId: 1);
    var elector = new PgRoleElector(
      Options.Create(notification),
      Options.Create(new RoleAssignmentOptions { HoldLegacySessionLock = true }),
      configuration,
      pod,
      new PgDutyElector(Options.Create(notification), configuration, pod, NullLogger<PgDutyElector>.Instance),
      logger,
      null);

    await Assert.That(async () => await elector.TryAcquireAsync(ROLE, CancellationToken.None)).ThrowsException();

    await Assert.That(logger.Warnings).Contains(m =>
      m.Contains($"role '{ROLE}'", StringComparison.Ordinal) && m.Contains("pooled", StringComparison.Ordinal));
  }

  private static IConfiguration _config() => new ConfigurationBuilder().AddInMemoryCollection([]).Build();

  private PgRoleElector _bridgedElector(IServiceInstanceProvider pod, WhizbangNotificationOptions notification, ILogger<PgRoleElector> logger) =>
    new(
      Options.Create(notification),
      Options.Create(new RoleAssignmentOptions { HoldLegacySessionLock = true }),
      _config(),
      pod,
      new PgDutyElector(Options.Create(notification), _config(), pod, NullLogger<PgDutyElector>.Instance),
      logger,
      null);

  private async Task<IServiceInstanceProvider> _joinAsync(CancellationToken cancellationToken) {
    var pod = new ServiceInstanceProvider(Guid.NewGuid(), "role-svc", "role-host", processId: 1);
    await using var ctx = CreateDbContext();
    var coordinator = new EFCoreWorkCoordinator<WorkCoordinationDbContext>(ctx, JsonContextRegistry.CreateCombinedOptions());
    await coordinator.RecordHeartbeatAsync(new HeartbeatRequest(pod.InstanceId, pod.ServiceName, pod.HostName, 1), cancellationToken);
    return pod;
  }

  private sealed class CapturingLogger : ILogger<PgRoleElector> {
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
