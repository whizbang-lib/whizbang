using Microsoft.Extensions.Logging;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Startup;
using Whizbang.Data.Postgres;

namespace Whizbang.Data.EFCore.Postgres.Tests.Migrations;

/// <summary>
/// That deciding who migrates can never be the reason a service fails to start.
/// </summary>
/// <remarks>
/// <para>
/// An election is an optimization. It stops every replica scanning every table a rewrite touches,
/// which is worth having, and it is worth nothing at all compared with a fleet that starts. So the
/// rule every test here checks one face of: <b>no answer from the elector is fatal</b>. Each one
/// ends either with a duty or with this instance migrating under the advisory lock that guarded
/// this before an election existed.
/// </para>
/// <para>
/// That rule is not theoretical. An earlier attempt at this treated
/// <see cref="DutyRefusal.Refused"/> as a condition to throw on, and
/// <c>record_capability</c> answers exactly that for an instance which has not yet joined the
/// registry, which at schema-initialization time is every instance on every established database.
/// <see cref="ARefusedInstanceMigratesUnderTheLockRatherThanThrowingAsync"/> is that regression.
/// </para>
/// </remarks>
/// <code-under-test>src/Whizbang.Data.Postgres/MigratorDutyStaging.cs</code-under-test>
/// <docs>operations/infrastructure/migrations#which-instance-migrates</docs>
[Category("Unit")]
[Category("Shard1")]
public class MigratorDutyStagingTests {

  private const string SCHEMA = "public";

  /// <summary>The order registration and election must happen in.</summary>
  private static readonly string[] REGISTER_THEN_ELECT = ["register", "elect"];

  /// <summary>A grant that records whether the caller gave it back.</summary>
  private sealed class FakeGrant : IDutyGrant {
    public string Duty => StartupDuties.MIGRATOR;
    public DateTimeOffset AcquiredAt => DateTimeOffset.UnixEpoch;
    public Task<bool> VerifyStillHeldAsync(CancellationToken cancellationToken) => Task.FromResult(true);
    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
  }

  /// <summary>An elector with a scripted answer, or a scripted failure.</summary>
  private sealed class Elector(Func<DutyAttempt> answer) : IDutyElector {
    public int Attempts;
    public Task<DutyAttempt> TryAcquireAsync(string duty, CancellationToken cancellationToken) {
      Interlocked.Increment(ref Attempts);
      return Task.FromResult(answer());
    }
  }

  /// <summary>A logger that keeps what it was told, so "loudly" is assertable.</summary>
  private sealed class RecordingLogger : ILogger {
    public List<(LogLevel Level, string Message)> Entries { get; } = [];
    public IDisposable BeginScope<TState>(TState state) where TState : notnull => new Scope();
    public bool IsEnabled(LogLevel logLevel) => true;
    public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
        Func<TState, Exception?, string> formatter) =>
      Entries.Add((logLevel, formatter(state, exception)));
    private sealed class Scope : IDisposable { public void Dispose() { } }
  }

  private static Func<CancellationToken, Task> _registers(Action? onCall = null) =>
    _ => { onCall?.Invoke(); return Task.CompletedTask; };

  /// <summary>Winning the duty makes this instance the migrator, and hands the grant over.</summary>
  [Test]
  public async Task WinningTheDutyMakesThisInstanceTheMigratorAsync() {
    var grant = new FakeGrant();
    var registered = false;

    var staging = await MigratorDutyStaging.ElectAsync(
      new Elector(() => DutyAttempt.Granted(grant)),
      _registers(() => registered = true),
      SCHEMA);

    await Assert.That(staging.Stage).IsEqualTo(SchemaStage.Migrator);
    await Assert.That(staging.Grant).IsEqualTo(grant)
      .Because("the caller has to hold the duty for as long as it is migrating");
    await Assert.That(registered).IsTrue();
  }

  /// <summary>
  /// Registration happens before the election, not after and not instead.
  /// </summary>
  /// <remarks>
  /// The ordering the whole bootstrap exists to make possible. <c>record_capability</c> returns
  /// false for an instance it cannot find in the registry, so an election attempted first is
  /// refused for a reason that has nothing to do with another instance holding the duty.
  /// </remarks>
  [Test]
  public async Task TheInstanceJoinsTheRegistryBeforeElectingAsync() {
    var order = new List<string>();
    var elector = new Elector(() => {
      order.Add("elect");
      return DutyAttempt.Granted(new FakeGrant());
    });

    await MigratorDutyStaging.ElectAsync(
      elector, _registers(() => order.Add("register")), SCHEMA);

    await Assert.That(order).IsEquivalentTo(REGISTER_THEN_ELECT);
  }

  /// <summary>A contended duty makes this instance a waiter.</summary>
  [Test]
  public async Task AContendedDutyMakesThisInstanceAWaiterAsync() {
    var staging = await MigratorDutyStaging.ElectAsync(
      new Elector(() => DutyAttempt.Lost(DutyRefusal.Contended, "another instance holds it")),
      _registers(),
      SCHEMA);

    await Assert.That(staging.Stage).IsEqualTo(SchemaStage.Waiter);
    await Assert.That(staging.Grant).IsNull();
  }

  /// <summary>
  /// An instance the capability record refuses migrates under the lock, loudly, rather than throwing.
  /// </summary>
  /// <remarks>
  /// <para>
  /// The regression. <c>record_capability</c> answers false for an instance that is tombstoned in
  /// <c>wh_instance_evictions</c> <b>or</b> simply absent from <c>wh_service_instances</c>, and the
  /// elector reports both as <see cref="DutyRefusal.Refused"/>. Throwing on that took every replica
  /// of every service down on any database where the function already existed, because at this
  /// point in startup no instance has joined the registry yet.
  /// </para>
  /// <para>
  /// The bootstrap now registers first, so this should not happen. It has to be survivable anyway:
  /// a genuinely evicted instance that refuses to migrate leaves the schema behind for the entire
  /// fleet, and the advisory lock still stops two instances migrating at once. The warning is what
  /// an operator acts on.
  /// </para>
  /// </remarks>
  [Test]
  public async Task ARefusedInstanceMigratesUnderTheLockRatherThanThrowingAsync() {
    var logger = new RecordingLogger();

    var staging = await MigratorDutyStaging.ElectAsync(
      new Elector(() => DutyAttempt.Lost(DutyRefusal.Refused, "instance is evicted or unregistered")),
      _registers(),
      SCHEMA,
      logger);

    await Assert.That(staging.Stage).IsEqualTo(SchemaStage.Unstaged)
      .Because("never migrating is a worse failure than migrating without a duty, and it needs a "
        + "human to clear");
    await Assert.That(logger.Entries.Any(e => e.Level == LogLevel.Warning)).IsTrue()
      .Because("an evicted or unknown instance is a real condition an operator should see");
  }

  /// <summary>An elector with nothing to contend over leaves the instance unstaged.</summary>
  /// <remarks>
  /// A deployment with no direct connection has no way to hold a session lock, so there is nothing
  /// to elect with. Migrating anyway is right.
  /// </remarks>
  [Test]
  public async Task AnUnavailableElectorLeavesTheInstanceUnstagedAsync() {
    var staging = await MigratorDutyStaging.ElectAsync(
      new Elector(() => DutyAttempt.Lost(DutyRefusal.Unavailable, "no direct connection")),
      _registers(),
      SCHEMA,
      new RecordingLogger());

    await Assert.That(staging.Stage).IsEqualTo(SchemaStage.Unstaged);
  }

  /// <summary>
  /// An elector that throws leaves the instance unstaged.
  /// </summary>
  /// <remarks>
  /// What a database whose capability function does not exist yet actually produces: the elector
  /// wins its lock, calls a function that is not there, and the <c>42883</c> comes back as an
  /// exception rather than a refusal. That is the shape of a bootstrap that did not take, and it
  /// must cost the election rather than the startup.
  /// </remarks>
  [Test]
  public async Task AnElectorThatThrowsLeavesTheInstanceUnstagedAsync() {
    var logger = new RecordingLogger();

    var staging = await MigratorDutyStaging.ElectAsync(
      new Elector(() => throw new InvalidOperationException(
        "42883: function record_capability(uuid, text) does not exist")),
      _registers(),
      SCHEMA,
      logger);

    await Assert.That(staging.Stage).IsEqualTo(SchemaStage.Unstaged);
    await Assert.That(logger.Entries.Any(e => e.Level == LogLevel.Warning)).IsTrue();
  }

  /// <summary>No elector registered at all leaves the instance unstaged, quietly.</summary>
  /// <remarks>
  /// Distinct from an elector that answers: a consumer who never wired the notification services
  /// has nothing in the container, and that is an ordinary supported deployment rather than a
  /// problem to warn about.
  /// </remarks>
  [Test]
  public async Task NoElectorAtAllLeavesTheInstanceUnstagedAsync() {
    var logger = new RecordingLogger();
    var registered = false;

    var staging = await MigratorDutyStaging.ElectAsync(
      null, _registers(() => registered = true), SCHEMA, logger);

    await Assert.That(staging.Stage).IsEqualTo(SchemaStage.Unstaged);
    await Assert.That(registered).IsFalse()
      .Because("there is nothing to register for; the registry is maintained by the heartbeat "
        + "worker once the service is up");
    await Assert.That(logger.Entries.Any(e => e.Level == LogLevel.Warning)).IsFalse()
      .Because("a deployment without notification services is supported, not broken");
  }

  /// <summary>
  /// An instance that could not join the registry does not try to be elected.
  /// </summary>
  /// <remarks>
  /// Electing would be refused, and the refusal would report "unregistered" rather than whatever
  /// actually went wrong. Reporting the real failure and standing down is more useful than a
  /// misleading second one.
  /// </remarks>
  [Test]
  public async Task AFailedRegistrationStopsTheElectionAsync() {
    var logger = new RecordingLogger();
    var elector = new Elector(() => DutyAttempt.Granted(new FakeGrant()));

    var staging = await MigratorDutyStaging.ElectAsync(
      elector,
      _ => throw new InvalidOperationException("relation \"wh_service_instances\" does not exist"),
      SCHEMA,
      logger);

    await Assert.That(staging.Stage).IsEqualTo(SchemaStage.Unstaged);
    await Assert.That(elector.Attempts).IsEqualTo(0)
      .Because("an unregistered instance cannot hold a capability, so asking only hides the cause");
    await Assert.That(logger.Entries.Any(e => e.Level == LogLevel.Warning)).IsTrue();
  }

  /// <summary>The duty is asked for once, not once per table.</summary>
  [Test]
  public async Task TheDutyIsAskedForOnceAsync() {
    var elector = new Elector(() => DutyAttempt.Granted(new FakeGrant()));

    await MigratorDutyStaging.ElectAsync(elector, _registers(), SCHEMA);

    await Assert.That(elector.Attempts).IsEqualTo(1);
  }

  /// <summary>Cancellation is not swallowed along with the failures.</summary>
  /// <remarks>
  /// Every other failure here is answered rather than thrown, which makes it worth pinning that
  /// host shutdown still propagates: an instance that carried on staging through cancellation would
  /// go on to run DDL while the process was being torn down.
  /// </remarks>
  [Test]
  public async Task CancellationPropagatesAsync() {
    using var cts = new CancellationTokenSource();
    await cts.CancelAsync();

    await Assert.That(async () => await MigratorDutyStaging.ElectAsync(
      new Elector(() => DutyAttempt.Granted(new FakeGrant())), _registers(), SCHEMA, null, cts.Token))
      .Throws<OperationCanceledException>();
  }

  /// <summary>A missing registration step is a caller error.</summary>
  [Test]
  public async Task MissingArgumentsAreRefusedAsync() =>
    await Assert.That(async () => await MigratorDutyStaging.ElectAsync(
      new Elector(() => DutyAttempt.Granted(new FakeGrant())), null!, SCHEMA))
      .Throws<ArgumentNullException>();
}
