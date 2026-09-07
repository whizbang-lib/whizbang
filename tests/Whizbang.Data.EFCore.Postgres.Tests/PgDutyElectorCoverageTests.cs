using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Whizbang.Core.Messaging;
using Whizbang.Core.Notifications;
using Whizbang.Core.Observability;
using Whizbang.Core.Serialization;
using Whizbang.Data.Postgres.Notifications;

namespace Whizbang.Data.EFCore.Postgres.Tests;

/// <summary>
/// Coverage-round tests for <see cref="PgDutyElector"/> paths that need a real advisory-lock
/// connection but aren't exercised by <see cref="DutyElectionE2ETests"/>: the catch-all in
/// <c>TryAcquireAsync</c> that releases a just-won lock and rethrows when recording fails after
/// the lock is held, and the idempotent early-return paths on the returned grant
/// (<c>VerifyStillHeldAsync</c> / <c>DisposeAsync</c> called after the grant is already gone).
/// </summary>
/// <code-under-test>src/Whizbang.Data.Postgres/Notifications/PgDutyElector.cs</code-under-test>
[Category("Shard1")]
public class PgDutyElectorCoverageTests : EFCoreTestBase {

  private async Task _joinFleetAsync(IServiceInstanceProvider instance, CancellationToken ct) {
    await using var ctx = CreateDbContext();
    var coordinator = new EFCoreWorkCoordinator<WorkCoordinationDbContext>(ctx, JsonContextRegistry.CreateCombinedOptions());
    await coordinator.RecordHeartbeatAsync(new HeartbeatRequest(instance.InstanceId, instance.ServiceName, instance.HostName, 1), ct);
  }

  private PgDutyElector _elector(IServiceInstanceProvider instance, WhizbangNotificationOptions? options = null) => new(
    Options.Create(options ?? new WhizbangNotificationOptions { DirectConnectionString = ConnectionString }),
    new ConfigurationBuilder().AddInMemoryCollection([]).Build(),
    instance,
    NullLogger<PgDutyElector>.Instance);

  // record_capability is looked up unqualified, so it resolves through whatever schema the
  // resolved connection's search_path names. A search path that doesn't cover the schema the
  // function actually lives in must surface as a thrown error AFTER the advisory lock is already
  // won -- swallowing that instead of releasing the lock and rethrowing would leave the duty held
  // by a session the caller believes never acquired anything, with no other instance able to take
  // over until that session eventually dies on its own.
  [Test]
  [Timeout(60000)]
  public async Task TryAcquire_WhenRecordCapabilityIsUnresolvable_PropagatesTheFailureInsteadOfANormalRefusalAsync(CancellationToken cancellationToken) {
    var instance = new ServiceInstanceProvider(Guid.NewGuid(), "utest-service", "utest-host", processId: 1);
    var brokenOptions = new WhizbangNotificationOptions {
      DirectConnectionString = ConnectionString,
      SearchPath = "wb_coverage_missing_schema_zzz",
    };
    var elector = _elector(instance, brokenOptions);

    await Assert.That(async () => await elector.TryAcquireAsync("coverage-catchall-duty", cancellationToken))
      .ThrowsException()
      .Because("record_capability doesn't resolve under a search path that excludes its schema, and that failure must propagate rather than be swallowed, not be reported as a normal refusal");
  }

  // A caller that keeps a disposed grant around and asks "am I still holding this" must get an
  // immediate, honest "no" without touching the connection disposal already closed -- pinging a
  // disposed connection would either throw an unrelated error or, worse, could spuriously report
  // holding state for a session nobody owns anymore.
  [Test]
  [Timeout(60000)]
  public async Task VerifyStillHeld_AfterDisposal_ReturnsFalseWithoutPingingTheClosedConnectionAsync(CancellationToken cancellationToken) {
    var instance = new ServiceInstanceProvider(Guid.NewGuid(), "utest-service", "utest-host", processId: 1);
    await _joinFleetAsync(instance, cancellationToken);
    var elector = _elector(instance);

    var attempt = await elector.TryAcquireAsync("coverage-grant-disposed", cancellationToken);
    await Assert.That(attempt.Grant).IsNotNull();
    await attempt.Grant!.DisposeAsync();

    await Assert.That(await attempt.Grant.VerifyStillHeldAsync(cancellationToken)).IsFalse()
      .Because("a disposed grant no longer holds anything and must say so immediately, not by pinging a connection Dispose already closed");
  }

  // Duty grants are normally released via `await using`; a caller path that also disposes
  // explicitly, or a retry that disposes twice, must not attempt a second release_capability /
  // pg_advisory_unlock round trip against a connection the first Dispose already closed.
  [Test]
  [Timeout(60000)]
  public async Task DisposeAsync_CalledTwice_IsIdempotentAsync(CancellationToken cancellationToken) {
    var instance = new ServiceInstanceProvider(Guid.NewGuid(), "utest-service", "utest-host", processId: 1);
    await _joinFleetAsync(instance, cancellationToken);
    var elector = _elector(instance);

    var attempt = await elector.TryAcquireAsync("coverage-grant-double-dispose", cancellationToken);
    await Assert.That(attempt.Grant).IsNotNull();
    await attempt.Grant!.DisposeAsync();

    await Assert.That(async () => await attempt.Grant.DisposeAsync()).ThrowsNothing()
      .Because("a second Dispose on the same grant must be a no-op, not a second attempt against an already-closed connection");
  }
}

/// <summary>
/// Coverage for the one <see cref="PgDutyElector"/> path that neither needs Postgres nor belongs
/// with <see cref="PgDutyElectorUnitTests"/>'s no-connection-configured scenarios: the pooled-key
/// fallback warning. It logs BEFORE the elector ever opens a connection, so the test doesn't need
/// a real database -- it only needs the resolution to land on
/// <see cref="Whizbang.Core.Notifications.NotificationConnectionStringResolver.ResolutionSource.PooledKeyFallback"/>.
/// The subsequent connection attempt (against a deliberately closed local port, same pattern used
/// throughout this test suite for "resolves to a string but must never actually reach a server")
/// is expected to fail; only the warning that fired before it matters here.
/// </summary>
/// <code-under-test>src/Whizbang.Data.Postgres/Notifications/PgDutyElector.cs</code-under-test>
[Category("Shard1")]
public class PgDutyElectorPooledFallbackCoverageTests {

  /// <summary>Captures what the elector reported, at every level.</summary>
  private sealed class CapturingLogger : ILogger<PgDutyElector> {
    private readonly Lock _lock = new();
    private readonly List<string> _messages = [];
    public List<string> Messages { get { lock (_lock) { return [.. _messages]; } } }
    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
    public bool IsEnabled(LogLevel logLevel) => true;
    public void Log<TState>(LogLevel logLevel, Microsoft.Extensions.Logging.EventId eventId, TState state, Exception? exception,
        Func<TState, Exception?, string> formatter) {
      lock (_lock) { _messages.Add(formatter(state, exception)); }
    }
  }

  // Session locks do not survive pgbouncer transaction pooling: a duty won on a pooled connection
  // would silently un-hold itself the moment the physical connection is handed back to the pool.
  // This warning is the only signal an operator gets before that happens in production, so it
  // must fire whenever the resolution lands on the pooled (non "-direct") connection string key --
  // before the elector ever tries to open anything.
  [Test]
  public async Task TryAcquire_ResolvedThroughPooledKey_LogsThePoolingRiskBeforeOpeningAnythingAsync() {
    var options = new WhizbangNotificationOptions { ConnectionStringKey = "app-db" };
    var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> {
      // No "app-db-direct" entry -- only the pooled key resolves, which is exactly the shape
      // that trips ResolutionSource.PooledKeyFallback.
      ["ConnectionStrings:app-db"] = "Host=127.0.0.1;Port=1;Username=nobody;Password=nobody;Database=nothing;Timeout=1",
    }).Build();
    var logger = new CapturingLogger();
    var elector = new PgDutyElector(
      Options.Create(options),
      configuration,
      new ServiceInstanceProvider(Guid.NewGuid(), "utest-service", "utest-host", processId: 1),
      logger);

    // The connection is intentionally unreachable -- only the pre-open warning is under test.
    await Assert.That(async () => await elector.TryAcquireAsync("commit-order-stamper", CancellationToken.None))
      .ThrowsException();

    await Assert.That(logger.Messages.Any(m => m.Contains("pgbouncer transaction pooling", StringComparison.Ordinal))).IsTrue()
      .Because("a pooled-key resolution must log the transaction-pooling risk before the duty can silently un-hold itself in production");
  }
}
