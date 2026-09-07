using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Notifications;
using Whizbang.Core.Observability;
using Whizbang.Core.Startup;
using Whizbang.Data.Postgres.Notifications;

namespace Whizbang.Data.EFCore.Postgres.Tests;

/// <summary>
/// Unit tests for <see cref="PgDutyElector"/> that do NOT require Postgres. The elector resolves
/// its connection before it opens anything, so the "nothing to contend on" answer is decided
/// entirely from options and configuration — no server involved. The contended, granted and
/// eviction paths need a real advisory lock and live in
/// <c>tests/Whizbang.Data.EFCore.Postgres.Tests/DutyElectionE2ETests.cs</c>.
/// </summary>
/// <docs>operations/startup/capabilities-and-duties</docs>
[Category("Shard1")]
public class PgDutyElectorUnitTests {

  private static PgDutyElector _elector(ILogger<PgDutyElector>? logger = null) {
    // No DirectConnectionString, no ConnectionStringKey, no data source, no fallback: the shape a
    // host has when it wired duty election but never configured a database for it.
    var options = new WhizbangNotificationOptions();
    var configuration = new ConfigurationBuilder().Build();
    return new PgDutyElector(
      Options.Create(options),
      configuration,
      new ServiceInstanceProvider(Guid.NewGuid(), "utest-service", "utest-host", processId: 1),
      logger ?? new CapturingElectorLogger());
  }

  [Test]
  public async Task TryAcquire_WithNoConnectionConfigured_RefusesAsUnavailableNotContendedAsync() {
    // Which refusal comes back decides what the caller does next, and only one of the three is
    // resolvable by trying again. Reporting a missing connection as Contended would send a caller
    // into a retry loop against a condition no amount of retrying can change -- the elector would
    // look busy forever on a host that simply has no database configured.
    var attempt = await _elector().TryAcquireAsync("commit-order-stamper", CancellationToken.None);

    await Assert.That(attempt.Refusal).IsEqualTo(DutyRefusal.Unavailable)
      .Because("a standing configuration gap is not contention, and Contended is the only refusal a retry can resolve");
    await Assert.That(attempt.Grant).IsNull()
      .Because("nothing was acquired, so there is no grant to release later");
  }

  [Test]
  public async Task TryAcquire_WithNoConnectionConfigured_SaysWhichDutyAndWhatToConfigureAsync() {
    // This detail is what surfaces in a startup step reason. A caller that has several duties in
    // flight cannot act on "no connection available" alone -- it needs to know which duty went
    // unheld, and an operator needs to know which setting is missing. Both halves are the message.
    var attempt = await _elector().TryAcquireAsync("commit-order-stamper", CancellationToken.None);

    await Assert.That(attempt.Detail).IsNotNull();
    await Assert.That(attempt.Detail!).Contains("commit-order-stamper")
      .Because("a host contending for several duties cannot tell from the refusal alone which one went unheld");
    await Assert.That(attempt.Detail!).Contains("DirectConnectionString")
      .Because("the refusal is only actionable if it names the setting that would fix it");
  }

  [Test]
  public async Task TryAcquire_WithNoConnectionConfigured_LogsTheStandingConditionAsync() {
    // The refusal is returned to one caller; the log is what a second instance, an operator, or a
    // post-mortem sees. A duty silently never being held is the failure mode this guards against.
    var logger = new CapturingElectorLogger();

    _ = await _elector(logger).TryAcquireAsync("commit-order-stamper", CancellationToken.None);

    await Assert.That(logger.Messages.Any(m => m.Contains("commit-order-stamper", StringComparison.Ordinal))).IsTrue()
      .Because("an unheld duty that logs nothing is indistinguishable from one nobody asked for");
  }

  [Test]
  public async Task TryAcquire_WithAnEmptyDutyName_ThrowsBeforeResolvingAnythingAsync() {
    // The duty name is hashed into the advisory-lock key, so an empty one is not a harmless
    // no-op -- it would contend on a key shared by every other caller that made the same mistake,
    // and they would silently exclude each other.
    await Assert.That(async () => await _elector().TryAcquireAsync("", CancellationToken.None))
      .Throws<ArgumentException>()
      .Because("an empty duty name would collide into one shared lock key rather than failing");
  }

  /// <summary>Captures what the elector reported, at every level.</summary>
  private sealed class CapturingElectorLogger : ILogger<PgDutyElector> {
    private readonly Lock _lock = new();
    private readonly List<string> _messages = [];
    public List<string> Messages { get { lock (_lock) { return [.. _messages]; } } }
    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
    public bool IsEnabled(LogLevel logLevel) => true;
    public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
        Func<TState, Exception?, string> formatter) {
      lock (_lock) { _messages.Add(formatter(state, exception)); }
    }
  }
}
