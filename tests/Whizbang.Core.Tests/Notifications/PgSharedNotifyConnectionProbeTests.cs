using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Notifications;
using Whizbang.Core.Observability;
using Whizbang.Data.Postgres.Notifications;

namespace Whizbang.Core.Tests.Notifications;

/// <summary>
/// Slice 33.2 — probe behavior that can be verified WITHOUT a real Postgres: missing
/// connection string for each <see cref="WorkSignalingMode"/>, ProbeNowAsync's "no conn
/// string" path, and the <see cref="INotifySignalingGate.OnAvailabilityChanged"/> fires-once
/// contract under repeated _setAvailable(false) calls. Real-Postgres round-trip + timeout
/// tests live in the integration suite.
/// </summary>
/// <docs>fundamentals/work-coordinator/notifications-and-pgbouncer</docs>
public class PgSharedNotifyConnectionProbeTests {

  private static IConfiguration _emptyConfig() =>
    new ConfigurationBuilder().AddInMemoryCollection([]).Build();

  private static PgSharedNotifyConnection _build(WhizbangNotificationOptions opts, TimeProvider? tp = null) {
    var cfg = _emptyConfig();
    return new PgSharedNotifyConnection(
      Options.Create(opts),
      cfg,
      new ServiceInstanceProvider(cfg),
      NullLogger<PgSharedNotifyConnection>.Instance,
      connectionStringFallback: null,
      timeProvider: tp);
  }

  [Test]
  public async Task ProbeNowAsync_NoConnectionString_AutoMode_ReturnsFalseAndSetsUnavailableAsync() {
    var conn = _build(new WhizbangNotificationOptions { SignalingMode = WorkSignalingMode.Auto });

    var transitions = new List<bool>();
    conn.OnAvailabilityChanged += b => transitions.Add(b);

    var ok = await conn.ProbeNowAsync();

    await Assert.That(ok).IsFalse();
    await Assert.That(conn.IsAvailable).IsFalse();
    await Assert.That(conn.LastFailureReason).IsNotNull();
  }

  [Test]
  public async Task ProbeNowAsync_NoConnectionString_PollingMode_StillReportsUnavailableAsync() {
    var conn = _build(new WhizbangNotificationOptions { SignalingMode = WorkSignalingMode.Polling });

    var ok = await conn.ProbeNowAsync();

    await Assert.That(ok).IsFalse();
    await Assert.That(conn.IsAvailable).IsFalse();
  }

  [Test]
  public async Task ProbeNowAsync_NoConnectionString_ListenNotifyMode_StillReportsFalseWithoutThrowingAsync() {
    // ListenNotifyMode is the fail-fast contract for the BackgroundService startup path
    // (ExecuteAsync throws). ProbeNowAsync runs out-of-band so it doesn't throw — it just
    // reports unavailable. Ops calling ProbeNowAsync should see false + LastFailureReason.
    var conn = _build(new WhizbangNotificationOptions { SignalingMode = WorkSignalingMode.ListenNotify });

    var ok = await conn.ProbeNowAsync();

    await Assert.That(ok).IsFalse();
    await Assert.That(conn.IsAvailable).IsFalse();
  }

  [Test]
  public async Task ProbeNowAsync_BadConnectionString_ReturnsFalse_SetsFailureReasonAsync() {
    var conn = _build(new WhizbangNotificationOptions {
      SignalingMode = WorkSignalingMode.Auto,
      DirectConnectionString = "Host=nonexistent.invalid;Port=5432;Username=u;Password=p;Timeout=2;Command Timeout=2"
    });

    using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
    var ok = await conn.ProbeNowAsync(cts.Token);

    await Assert.That(ok).IsFalse();
    await Assert.That(conn.IsAvailable).IsFalse();
    await Assert.That(conn.LastFailureReason).IsNotNull();
    await Assert.That(conn.LastFailureAt).IsNotNull();
  }

  [Test]
  public async Task AvailabilityChanged_FiresOnceForFalseToFalse_NoOpAsync() {
    var conn = _build(new WhizbangNotificationOptions { SignalingMode = WorkSignalingMode.Auto });

    var transitions = new List<bool>();
    conn.OnAvailabilityChanged += b => transitions.Add(b);

    // Multiple ProbeNowAsync calls all set false. Event must not fire repeatedly for
    // same-state transitions — IsAvailable starts at false and stays false.
    await conn.ProbeNowAsync();
    await conn.ProbeNowAsync();
    await conn.ProbeNowAsync();

    await Assert.That(transitions).IsEmpty();
  }

  [Test]
  public async Task SelfTestTimeout_DefaultIsTwoSecondsAsync() {
    // Locks the documented default so a future options change shows up in test churn.
    var opts = new WhizbangNotificationOptions();

    await Assert.That(opts.SelfTestTimeout).IsEqualTo(TimeSpan.FromSeconds(2));
  }

  [Test]
  public async Task PeriodicReprobeInterval_DefaultIsFiveMinutesAsync() {
    var opts = new WhizbangNotificationOptions();

    await Assert.That(opts.PeriodicReprobeInterval).IsEqualTo(TimeSpan.FromMinutes(5));
  }

  [Test]
  public async Task FailuresBeforeFallback_DefaultIsFiveAsync() {
    var opts = new WhizbangNotificationOptions();

    await Assert.That(opts.FailuresBeforeFallback).IsEqualTo(5);
  }
}
