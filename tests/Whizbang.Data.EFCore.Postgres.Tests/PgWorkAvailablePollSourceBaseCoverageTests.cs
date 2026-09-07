using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Notifications;
using Whizbang.Core.Observability;
using Whizbang.Core.Signals;
using Whizbang.Data.Postgres.Notifications;

namespace Whizbang.Data.EFCore.Postgres.Tests;

/// <summary>
/// Coverage for <see cref="PgWorkAvailablePollSourceBase{TSignal}.DetectAsync"/>'s "no connection
/// available" branch — no existing test builds a poll source with nothing configured and ticks it.
/// Uses <see cref="PgOutboxWorkAvailablePollSource"/>, a concrete subclass, with no
/// <c>DirectConnectionString</c>, no <c>ConnectionStringKey</c>, no fallback, and no data source, so
/// <c>NotificationConnectionPlan.IsAvailable</c> is false before a socket is ever touched — the same
/// no-database pattern <see cref="PgScheduleManagerCoverageTests"/> and
/// <see cref="PgScheduleOccurrenceStoreCoverageTests"/> already use. No database anywhere in this
/// file.
/// </summary>
/// <code-under-test>src/Whizbang.Data.Postgres/Notifications/PgWorkAvailablePollSourceBase.cs</code-under-test>
[Category("Shard1")]
public class PgWorkAvailablePollSourceBaseCoverageTests {

  // A false detection result is a silent no-op — the doorbell just doesn't ring. If a
  // misconfigured (or not-yet-configured) connection instead threw out of DetectAsync, every
  // poll-source tick would surface as a logged failure instead of the documented "NOTIFY path will
  // drive us when the DB comes back" graceful degradation, and the sink would never be given the
  // chance to be asked for signals it isn't meant to receive on an unreachable poll.
  [Test]
  public async Task DetectAsync_WithNoConnectionConfigured_ReportsNoWorkAndNeverCallsTheSinkAsync() {
    var instanceProvider = new ServiceInstanceProvider(
      Guid.NewGuid(), "coverage-svc", "coverage-host", processId: 1);
    var source = new PgOutboxWorkAvailablePollSource(
      new FakeTimeProvider(),
      Options.Create(new WhizbangNotificationOptions()),
      new ConfigurationBuilder().Build(),
      instanceProvider,
      NullLogger<PgOutboxWorkAvailablePollSource>.Instance);
    var sink = new _fakeSink();
    await source.StartAsync(sink);

    await source.TickForTestsAsync(CancellationToken.None);

    await Assert.That(sink.ReceivedCount).IsEqualTo(0)
      .Because("with no connection available there is nothing to detect — the sink must never be "
             + "asked to deliver a signal for a tick that found no usable connection");
  }

  private sealed class _fakeSink : ISignalSink {
    public int ReceivedCount { get; private set; }
    public ValueTask ReceiveAsync<TSignal>(TSignal signal, CancellationToken cancellationToken = default)
        where TSignal : ISignal {
      ReceivedCount++;
      return ValueTask.CompletedTask;
    }
  }
}
