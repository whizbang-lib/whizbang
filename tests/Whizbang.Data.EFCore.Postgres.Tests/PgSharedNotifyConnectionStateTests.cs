using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Configuration;
using Whizbang.Core.Notifications;
using Whizbang.Core.Observability;
using Whizbang.Data.Postgres.Notifications;

namespace Whizbang.Data.EFCore.Postgres.Tests;

/// <summary>
/// The shared LISTEN connection's two liveness surfaces, which other components steer on.
/// <para>
/// <c>IsAliveLockHeld</c> drives the heartbeat cadence: holding the advisory lock means this pod is
/// the one whose liveness the rest of the fleet reads, so it heartbeats fast; not holding it means
/// it can afford the slow cadence. Reporting the lock as held while the connection is unavailable
/// would claim the fast path for a pod that cannot answer, which is why the property is the
/// conjunction of both and not just the lock flag.
/// </para>
/// <para>
/// <c>ProbeNowAsync</c> is the on-demand version of the same question. Its whole point is that it
/// answers rather than throws — a caller asking "is the notification path working right now" gets
/// false for a broken one, because an exception at that call site is indistinguishable from the
/// caller itself being broken.
/// </para>
/// </summary>
/// <remarks>
/// Live PostgreSQL: both surfaces are about a real session holding a real advisory lock, and the
/// unavailable case is specifically "a connection that cannot be opened", which an in-memory double
/// cannot represent honestly.
/// </remarks>
/// <code-under-test>src/Whizbang.Data.Postgres/Notifications/PgSharedNotifyConnection.cs</code-under-test>
[Category("Integration")]
[Category("Shard4")]
public class PgSharedNotifyConnectionStateTests : EFCoreTestBase {

  private PgSharedNotifyConnection _connection(string? connectionString) =>
    new(Options.Create(new WhizbangNotificationOptions {
      DirectConnectionString = connectionString,
      SignalingMode = WorkSignalingMode.ListenNotify,
    }),
        new ConfigurationBuilder().AddInMemoryCollection([]).Build(),
        new ServiceInstanceProvider(new ConfigurationBuilder().AddInMemoryCollection([]).Build()),
        NullLogger<PgSharedNotifyConnection>.Instance,
        connectionStringFallback: null,
        timeProvider: null);

  [Test]
  [Timeout(60000)]
  public async Task BeforeStarting_TheAliveLockIsNotClaimedAsync(CancellationToken cancellationToken) {
    using var shared = _connection(ConnectionString);

    await Assert.That(shared.IsAliveLockHeld).IsFalse()
      .Because("a connection that has never started holds nothing, and a pod claiming the lock it "
             + "does not hold would take the fast heartbeat cadence on false pretenses");
  }

  [Test]
  [Timeout(60000)]
  public async Task OnceStarted_TheLockAndAvailabilityAgreeAsync(CancellationToken cancellationToken) {
    using var shared = _connection(ConnectionString);
    await shared.StartAsync(cancellationToken);
    try {
      // The property is the conjunction: whatever the lock outcome, it can never report held while
      // the connection is unavailable, because that pairing is the one that misleads the heartbeat.
      await Assert.That(shared.IsAliveLockHeld && !shared.IsAvailable).IsFalse()
        .Because("held-but-unavailable claims the fast cadence for a pod that cannot answer");
    } finally {
      await shared.StopAsync(CancellationToken.None);
    }
  }

  [Test]
  [Timeout(60000)]
  public async Task AProbeAgainstAnUnreachableServer_AnswersFalseRatherThanThrowingAsync(
      CancellationToken cancellationToken) {
    // Port 1 is reserved and never listening, so this is a connection failure rather than a
    // credential or schema problem — the wide catch is what turns it into an answer.
    using var shared = _connection(
      "Host=127.0.0.1;Port=1;Database=whizbang;Username=whizbang;Password=whizbang;Timeout=2");

    var ok = await shared.ProbeNowAsync(cancellationToken);

    await Assert.That(ok).IsFalse()
      .Because("the caller asked whether the notification path works; throwing at them is not an "
             + "answer, and reads as the caller being broken rather than the dependency");
    await Assert.That(shared.IsAvailable).IsFalse()
      .Because("a failed probe must also move the state the rest of the system reads, or the two "
             + "disagree and the next reader trusts the stale one");
  }

  [Test]
  [Timeout(60000)]
  public async Task AProbeWithNoResolvableConnectionString_AnswersFalseAsync(
      CancellationToken cancellationToken) {
    // The earliest exit: nothing to connect with at all. Still an answer, not an exception.
    using var shared = _connection(connectionString: null);

    await Assert.That(await shared.ProbeNowAsync(cancellationToken)).IsFalse()
      .Because("no connection string is a configuration problem the caller must see as "
             + "unavailable, not as a crash inside the probe");
  }

  [Test]
  [Timeout(60000)]
  public async Task AProbeCanceledByTheCaller_PropagatesRatherThanReportingUnavailableAsync(
      CancellationToken cancellationToken) {
    // The guard ahead of the try. A caller who cancelled is not asking any more, and answering
    // "unavailable" would write a verdict about the dependency that nobody requested.
    using var shared = _connection(ConnectionString);
    using var stopping = new CancellationTokenSource();
    await stopping.CancelAsync();

    await Assert.That(async () => await shared.ProbeNowAsync(stopping.Token))
      .Throws<OperationCanceledException>()
      .Because("cancellation is the caller withdrawing the question, not the dependency failing");
  }
}
