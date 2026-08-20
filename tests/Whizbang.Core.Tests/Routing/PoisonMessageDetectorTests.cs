using System.Diagnostics.Metrics;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Health;
using Whizbang.Core.Routing;

namespace Whizbang.Core.Tests.Routing;

/// <summary>
/// Topology arc phase 8.5 — the non-count-based poison detector. A live Standard-namespace probe
/// (and the phase-6 emulator spike) established that on SESSION-enabled entities a lock loss via
/// connection death does NOT increment the broker's delivery count: explicit abandon does,
/// non-session lock loss does, session lock loss leaves it at 1. Command inboxes are session
/// enabled by default, so the broker's MaxDeliveryCount valve — and every transport branch that
/// reads the same counter — can never fire under a consumer-death storm. These tests lock the
/// replacement decision: age at the receive boundary (layer 1) and durable redelivery-observation
/// counting (layer 2), both owned by ONE Core policy so the two transports cannot drift.
/// </summary>
public class PoisonMessageDetectorTests {

  private static readonly DateTimeOffset _now = new(2026, 8, 20, 12, 0, 0, TimeSpan.Zero);

  private static PoisonMessageDetector _detector(
      PoisonMessageOptions? options = null,
      ILogger<PoisonMessageDetector>? logger = null,
      PoisonDetectionCapabilityState? capabilityState = null) =>
    new(
      Options.Create(options ?? new PoisonMessageOptions()),
      logger ?? NullLogger<PoisonMessageDetector>.Instance,
      new Meter("Whizbang.Core.Tests.PoisonMessageDetector"),
      capabilityState);

  private static PoisonEvaluationContext _context(
      DateTimeOffset? firstEnqueuedAt = null,
      int? brokerDeliveryCount = null,
      int? durableObservationCount = null) =>
    new(
      MessageId: "message-1",
      FirstEnqueuedAt: firstEnqueuedAt,
      BrokerDeliveryCount: brokerDeliveryCount,
      DurableObservationCount: durableObservationCount,
      Now: _now);

  #region Layer 1 — age at the receive boundary

  [Test]
  public async Task Evaluate_AgedMessage_QuarantinesOnAgeAsync() {
    // The hostage case: the broker's delivery count is stuck at 1 (session lock loss never
    // increments it) and no durable observation has been recorded, so EVERY count-based layer
    // is inert. Age is the only signal that survives redelivery — it must fire alone.
    var options = new PoisonMessageOptions { AgeThreshold = TimeSpan.FromMinutes(30) };
    var detector = _detector(options);

    var verdict = detector.Evaluate(_context(
      firstEnqueuedAt: _now - TimeSpan.FromMinutes(31),
      brokerDeliveryCount: 1,
      durableObservationCount: null));

    await Assert.That(verdict.ShouldQuarantine).IsTrue()
      .Because("age is the only bound that survives a session lock-loss storm");
    await Assert.That(verdict.Reason).IsEqualTo(PoisonQuarantineReason.MessageAgeExceeded);
    await Assert.That(verdict.Detail).IsNotNull();
  }

  [Test]
  public async Task Evaluate_FreshMessage_ProceedsAsync() {
    var options = new PoisonMessageOptions { AgeThreshold = TimeSpan.FromMinutes(30) };
    var detector = _detector(options);

    var verdict = detector.Evaluate(_context(firstEnqueuedAt: _now - TimeSpan.FromSeconds(5)));

    await Assert.That(verdict.ShouldQuarantine).IsFalse();
    await Assert.That(verdict.Reason).IsEqualTo(PoisonQuarantineReason.None);
  }

  [Test]
  public async Task Evaluate_SlowButProgressingMessage_ProceedsAsync() {
    // A legitimately slow message: it has been in flight for several lock-renewal windows but
    // is still inside the derived bound (renewal x attempts). The derivation exists precisely
    // so this message is never quarantined — quarantining progress is worse than the disease.
    var options = new PoisonMessageOptions {
      LockRenewalDuration = TimeSpan.FromMinutes(5),
      MaxDeliveryAttempts = 10,
    };
    var detector = _detector(options);
    var inFlightFor = TimeSpan.FromMinutes(5 * 9);   // 9 of 10 renewal windows consumed

    var verdict = detector.Evaluate(_context(firstEnqueuedAt: _now - inFlightFor));

    await Assert.That(verdict.ShouldQuarantine).IsFalse()
      .Because("a message still inside renewal x attempts is progressing, not poison");
  }

  [Test]
  public async Task Evaluate_NoFirstEnqueuedAt_DoesNotQuarantineOnAgeAsync() {
    // Capability honesty: a transport that cannot supply a trustworthy first-enqueue timestamp
    // passes null. Layer 1 MUST NOT invent an age (a default/zero timestamp would quarantine
    // every message ever received) — it degrades to layer 2 instead.
    var detector = _detector(new PoisonMessageOptions { AgeThreshold = TimeSpan.FromMinutes(30) });

    var verdict = detector.Evaluate(_context(firstEnqueuedAt: null, brokerDeliveryCount: 1));

    await Assert.That(verdict.ShouldQuarantine).IsFalse();
  }

  [Test]
  public async Task Evaluate_FutureFirstEnqueuedAt_ProceedsAsync() {
    // Clock skew between broker and consumer must never manufacture a negative age that
    // wraps into a quarantine.
    var detector = _detector(new PoisonMessageOptions { AgeThreshold = TimeSpan.FromMinutes(30) });

    var verdict = detector.Evaluate(_context(firstEnqueuedAt: _now + TimeSpan.FromMinutes(5)));

    await Assert.That(verdict.ShouldQuarantine).IsFalse();
  }

  #endregion

  #region Layer 2 — durable observation counting

  [Test]
  public async Task Evaluate_DurableObservationsAtBound_QuarantinesAsync() {
    // Layer 2 stands alone: NO timestamp at all (the RabbitMQ-without-a-publisher-timestamp
    // case), delivery count stuck at 1. Only the durable redelivery-observation counter can
    // bound the loop, so this test proves it fires with layer 1 fully inert.
    var options = new PoisonMessageOptions { MaxDurableObservations = 5 };
    var detector = _detector(options);

    var verdict = detector.Evaluate(_context(
      firstEnqueuedAt: null,
      brokerDeliveryCount: 1,
      durableObservationCount: 5));

    await Assert.That(verdict.ShouldQuarantine).IsTrue();
    await Assert.That(verdict.Reason).IsEqualTo(PoisonQuarantineReason.ObservationCountExceeded);
  }

  [Test]
  public async Task Evaluate_DurableObservationsBelowBound_ProceedsAsync() {
    var detector = _detector(new PoisonMessageOptions { MaxDurableObservations = 5 });

    var verdict = detector.Evaluate(_context(firstEnqueuedAt: null, durableObservationCount: 4));

    await Assert.That(verdict.ShouldQuarantine).IsFalse();
  }

  [Test]
  public async Task Evaluate_BrokerDeliveryCountAlone_NeverQuarantinesAsync() {
    // The whole reason this phase exists: the default detector deliberately does NOT act on the
    // broker's delivery count. It cannot rise on session entities, and where it CAN rise the
    // broker's own MaxDeliveryCount valve plus the transport's existing failure branch already
    // own that decision. It travels in the context for custom detectors and telemetry only.
    var detector = _detector(new PoisonMessageOptions {
      MaxDeliveryAttempts = 10,
      MaxDurableObservations = 5,
    });

    var verdict = detector.Evaluate(_context(
      firstEnqueuedAt: null,
      brokerDeliveryCount: 10_000,
      durableObservationCount: null));

    await Assert.That(verdict.ShouldQuarantine).IsFalse()
      .Because("the counter this phase exists to replace must not be the default detector's authority");
  }

  #endregion

  #region Killswitch

  [Test]
  public async Task Evaluate_Disabled_ProceedsEvenWhenBothLayersWouldFireAsync() {
    // The killswitch must neuter EVERY layer, not just the newest one.
    var options = new PoisonMessageOptions {
      Enabled = false,
      AgeThreshold = TimeSpan.FromMinutes(1),
      MaxDurableObservations = 1,
    };
    var detector = _detector(options);

    var verdict = detector.Evaluate(_context(
      firstEnqueuedAt: _now - TimeSpan.FromDays(30),
      brokerDeliveryCount: 99,
      durableObservationCount: 99));

    await Assert.That(verdict.ShouldQuarantine).IsFalse();
    await Assert.That(verdict.Reason).IsEqualTo(PoisonQuarantineReason.None);
  }

  #endregion

  #region Threshold derivation (property)

  [Test]
  public async Task DeriveAgeThreshold_IsRenewalTimesAttemptsClampedToFloor_AcrossTheMatrixAsync() {
    // Property, not a magic number: for EVERY (renewal, attempts, floor) the derived threshold
    // is exactly max(floor, renewal x attempts). Two consequences are load-bearing and asserted
    // as such: it is never BELOW the window a legitimately slow message may legally occupy
    // (renewal x attempts), and never below the documented floor.
    var renewals = new[] {
      TimeSpan.FromSeconds(30), TimeSpan.FromMinutes(1), TimeSpan.FromMinutes(5),
      TimeSpan.FromMinutes(30), TimeSpan.FromHours(1),
    };
    var attempts = new[] { 1, 2, 3, 5, 10, 50, 100 };
    var floors = new[] { TimeSpan.Zero, TimeSpan.FromMinutes(10), TimeSpan.FromMinutes(30), TimeSpan.FromHours(6) };

    foreach (var renewal in renewals) {
      foreach (var attempt in attempts) {
        foreach (var floor in floors) {
          var derived = PoisonMessageOptions.DeriveAgeThreshold(renewal, attempt, floor);
          var product = renewal * attempt;

          await Assert.That(derived).IsEqualTo(product > floor ? product : floor)
            .Because($"renewal={renewal} attempts={attempt} floor={floor} must derive max(floor, renewal x attempts)");
          await Assert.That(derived >= product).IsTrue()
            .Because("a message may legally occupy renewal x attempts while progressing");
          await Assert.That(derived >= floor).IsTrue()
            .Because("the documented floor is a lower bound, never advisory");
        }
      }
    }
  }

  [Test]
  public async Task DeriveAgeThreshold_NonPositiveInputs_FallBackToTheFloorAsync() {
    // Degenerate configuration must not produce a zero/negative threshold that quarantines
    // every message the instant it arrives.
    var floor = TimeSpan.FromMinutes(30);

    await Assert.That(PoisonMessageOptions.DeriveAgeThreshold(TimeSpan.Zero, 10, floor)).IsEqualTo(floor);
    await Assert.That(PoisonMessageOptions.DeriveAgeThreshold(TimeSpan.FromMinutes(-5), 10, floor)).IsEqualTo(floor);
    await Assert.That(PoisonMessageOptions.DeriveAgeThreshold(TimeSpan.FromMinutes(5), 0, floor)).IsEqualTo(floor);
    await Assert.That(PoisonMessageOptions.DeriveAgeThreshold(TimeSpan.FromMinutes(5), -3, floor)).IsEqualTo(floor);
  }

  [Test]
  public async Task DeriveAgeThreshold_OverflowingProduct_SaturatesInsteadOfThrowingAsync() {
    var derived = PoisonMessageOptions.DeriveAgeThreshold(TimeSpan.FromDays(1000), int.MaxValue, TimeSpan.FromMinutes(30));

    await Assert.That(derived).IsEqualTo(TimeSpan.MaxValue);
  }

  [Test]
  public async Task EffectiveAgeThreshold_DefaultOptions_AreDerivedFromLockAndDeliveryOptionsAsync() {
    // The default is DERIVED, not guessed — changing the transport's lock/delivery knobs moves
    // the poison threshold with them.
    var options = new PoisonMessageOptions();

    await Assert.That(options.EffectiveAgeThreshold).IsEqualTo(
      PoisonMessageOptions.DeriveAgeThreshold(
        PoisonMessageOptions.DEFAULT_LOCK_RENEWAL_DURATION,
        PoisonMessageOptions.DEFAULT_MAX_DELIVERY_ATTEMPTS,
        PoisonMessageOptions.DEFAULT_AGE_THRESHOLD_FLOOR));
  }

  [Test]
  public async Task EffectiveAgeThreshold_TransportSuppliedLockAndDelivery_MoveTheThresholdAsync() {
    var options = new PoisonMessageOptions {
      LockRenewalDuration = TimeSpan.FromMinutes(10),
      MaxDeliveryAttempts = 20,
      AgeThresholdFloor = TimeSpan.FromMinutes(1),
    };

    await Assert.That(options.EffectiveAgeThreshold).IsEqualTo(TimeSpan.FromMinutes(200));
  }

  [Test]
  public async Task EffectiveAgeThreshold_ExplicitOverride_WinsOverDerivationAsync() {
    var options = new PoisonMessageOptions {
      AgeThreshold = TimeSpan.FromMinutes(7),
      LockRenewalDuration = TimeSpan.FromMinutes(10),
      MaxDeliveryAttempts = 20,
    };

    await Assert.That(options.EffectiveAgeThreshold).IsEqualTo(TimeSpan.FromMinutes(7));
  }

  #endregion

  #region Capability honesty

  [Test]
  public async Task ReportAgeCapability_UntrustworthyTimestamp_DegradesLoudlyAsync() {
    // "It must never go silently inert" — the original valve failed silently, and that silence
    // is what this phase is correcting. An incapable surface logs a WARNING and shows up on the
    // health surface; it does not merely stop working.
    var logger = new RecordingLogger<PoisonMessageDetector>();
    var state = new PoisonDetectionCapabilityState();
    var detector = _detector(logger: logger, capabilityState: state);

    detector.ReportAgeCapability("rabbitmq", "inbox.orders", canSupplyTrustworthyAge: false);

    await Assert.That(state.HasDegradedSurface).IsTrue();
    await Assert.That(state.DegradedSurfaces).Count().IsEqualTo(1);
    await Assert.That(state.DegradedSurfaces[0].Transport).IsEqualTo("rabbitmq");
    await Assert.That(state.DegradedSurfaces[0].Entity).IsEqualTo("inbox.orders");
    await Assert.That(logger.Contains(LogLevel.Warning, "age-based poison detection")).IsTrue()
      .Because("degrading to layer 2 must be audible, not silent");
  }

  [Test]
  public async Task ReportAgeCapability_RepeatedIncapableReports_LogOnceAsync() {
    // A storm delivers the same message thousands of times; the degradation notice must not
    // become the log flood it is warning about.
    var logger = new RecordingLogger<PoisonMessageDetector>();
    var detector = _detector(logger: logger, capabilityState: new PoisonDetectionCapabilityState());

    for (var i = 0; i < 50; i++) {
      detector.ReportAgeCapability("rabbitmq", "inbox.orders", canSupplyTrustworthyAge: false);
    }

    await Assert.That(logger.Count(LogLevel.Warning, "age-based poison detection")).IsEqualTo(1);
  }

  [Test]
  public async Task ReportAgeCapability_CapableSurface_RecordsNoDegradationAsync() {
    var logger = new RecordingLogger<PoisonMessageDetector>();
    var state = new PoisonDetectionCapabilityState();
    var detector = _detector(logger: logger, capabilityState: state);

    detector.ReportAgeCapability("azure-service-bus", "inbox.orders", canSupplyTrustworthyAge: true);

    await Assert.That(state.HasDegradedSurface).IsFalse();
    await Assert.That(logger.Contains(LogLevel.Warning, "age-based poison detection")).IsFalse();
  }

  [Test]
  public async Task ReportAgeCapability_SurfaceRecovers_ClearsTheDegradationAsync() {
    var state = new PoisonDetectionCapabilityState();
    var detector = _detector(capabilityState: state);

    detector.ReportAgeCapability("rabbitmq", "inbox.orders", canSupplyTrustworthyAge: false);
    detector.ReportAgeCapability("rabbitmq", "inbox.orders", canSupplyTrustworthyAge: true);

    await Assert.That(state.HasDegradedSurface).IsFalse();
  }

  [Test]
  public async Task ReportAgeCapability_DistinctEntities_TrackedSeparatelyAsync() {
    var state = new PoisonDetectionCapabilityState();
    var detector = _detector(capabilityState: state);

    detector.ReportAgeCapability("rabbitmq", "inbox.orders", canSupplyTrustworthyAge: false);
    detector.ReportAgeCapability("rabbitmq", "inbox.billing", canSupplyTrustworthyAge: false);
    detector.ReportAgeCapability("rabbitmq", "inbox.shipping", canSupplyTrustworthyAge: true);

    await Assert.That(state.DegradedSurfaces).Count().IsEqualTo(2);
  }

  [Test]
  public async Task PoisonDetectionHealthSource_NoDegradedSurface_IsOperationalAsync() {
    var source = new PoisonDetectionHealthSource(new PoisonDetectionCapabilityState());

    var health = await source.ReportAsync(CancellationToken.None);

    await Assert.That(health.State).IsEqualTo(ComponentState.Operational);
  }

  [Test]
  public async Task PoisonDetectionHealthSource_DegradedSurface_ReportsDegradedWithDetailAsync() {
    var state = new PoisonDetectionCapabilityState();
    state.ReportAgeCapability("rabbitmq", "inbox.orders", canSupplyTrustworthyAge: false);
    var source = new PoisonDetectionHealthSource(state);

    var health = await source.ReportAsync(CancellationToken.None);

    await Assert.That(health.State).IsEqualTo(ComponentState.Degraded);
    await Assert.That(source.Component).IsEqualTo("poison-detection");
    await Assert.That(health.Detail).IsNotNull();
    await Assert.That(health.Detail!).Contains("inbox.orders");
  }

  #endregion

  #region Quarantine telemetry

  [Test]
  public async Task RecordQuarantine_QuarantinedVerdict_LogsWarningAsync() {
    var logger = new RecordingLogger<PoisonMessageDetector>();
    var detector = _detector(logger: logger);
    var verdict = PoisonVerdict.Quarantine(PoisonQuarantineReason.MessageAgeExceeded, "aged out");

    detector.RecordQuarantine(PoisonQuarantineGate.Receive, verdict, _context());

    await Assert.That(logger.Contains(LogLevel.Warning, "quarantined")).IsTrue();
  }

  [Test]
  public async Task RecordQuarantine_ProceedVerdict_IsSilentAsync() {
    var logger = new RecordingLogger<PoisonMessageDetector>();
    var detector = _detector(logger: logger);

    detector.RecordQuarantine(PoisonQuarantineGate.Receive, PoisonVerdict.Proceed(), _context());

    await Assert.That(logger.Entries).IsEmpty();
  }

  #endregion
}

/// <summary>
/// ILogger double that records every entry with all levels enabled, so level-gated diagnostic
/// branches execute and can be asserted without polling.
/// </summary>
internal sealed class RecordingLogger<T> : ILogger<T> {
  private readonly Lock _sync = new();
  private readonly List<(LogLevel Level, string Message)> _entries = [];

  public IReadOnlyList<(LogLevel Level, string Message)> Entries {
    get {
      lock (_sync) {
        return [.. _entries];
      }
    }
  }

  public IDisposable BeginScope<TState>(TState state) where TState : notnull => NullScope.Instance;

  public bool IsEnabled(LogLevel logLevel) => true;

  public void Log<TState>(
      LogLevel logLevel, EventId eventId, TState state, Exception? exception,
      Func<TState, Exception?, string> formatter) {
    ArgumentNullException.ThrowIfNull(formatter);
    lock (_sync) {
      _entries.Add((logLevel, formatter(state, exception)));
    }
  }

  public bool Contains(LogLevel level, string fragment) =>
    Entries.Any(e => e.Level == level && e.Message.Contains(fragment, StringComparison.OrdinalIgnoreCase));

  public int Count(LogLevel level, string fragment) =>
    Entries.Count(e => e.Level == level && e.Message.Contains(fragment, StringComparison.OrdinalIgnoreCase));

  private sealed class NullScope : IDisposable {
    internal static readonly NullScope Instance = new();
    public void Dispose() { }
  }
}
