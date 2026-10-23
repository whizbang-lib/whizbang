// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using System.Diagnostics.Metrics;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Routing;

namespace Whizbang.Core.Tests.Routing;

/// <summary>
/// Issue #1663: the receive gate dead-lettered a backlog of messages that were never attempted,
/// because layer 1 quarantined on age alone. The gate now accepts every message until it is
/// stored: an aged message proceeds and is reported on <c>whizbang.message.aged_on_receive</c> plus
/// a warning, so a backlog stays visible. Loops are bounded after storage by layer 2.
/// </summary>
/// <docs>fundamentals/dispatcher/routing#poison-messages</docs>
[Category("Routing")]
public class PoisonAgedOnReceiveTests {

  private static readonly DateTimeOffset _now = new(2026, 8, 20, 12, 0, 0, TimeSpan.Zero);
  private static readonly DateTimeOffset _aged = _now - TimeSpan.FromHours(3);
  private const string MESSAGE_ID = "0199aa00-0000-7000-8000-000000000001";

  private static PoisonMessageDetector _detector(
      ILogger<PoisonMessageDetector>? logger = null, Meter? meter = null, int maxDurableObservations = 10) =>
    new(
      Options.Create(new PoisonMessageOptions {
        AgeThreshold = TimeSpan.FromMinutes(30),
        MaxDurableObservations = maxDurableObservations,
      }),
      logger ?? NullLogger<PoisonMessageDetector>.Instance,
      meter ?? new Meter("Whizbang.Core.Tests.PoisonAgedOnReceive"));

  private static PoisonEvaluationContext _context(
      DateTimeOffset? firstEnqueuedAt, int? brokerDeliveryCount = 1) =>
    new(MESSAGE_ID, firstEnqueuedAt, brokerDeliveryCount, DurableObservationCount: null, _now);

  #region Evaluate

  [Test]
  [Arguments(1)]
  [Arguments(2)]
  [Arguments(50)]
  public async Task Evaluate_AgedMessage_ProceedsWhateverTheBrokerDeliveryCountAsync(int deliveryCount) {
    // First delivery or redelivery, an aged message is accepted: the loop a redelivery belongs to
    // is bounded after storage, where the message's own history is durable.
    var verdict = _detector().Evaluate(_context(_aged, deliveryCount));

    await Assert.That(verdict.ShouldQuarantine).IsFalse();
    await Assert.That(verdict.Reason).IsEqualTo(PoisonQuarantineReason.AgedOnReceive);
    await Assert.That(verdict.Detail).Contains(MESSAGE_ID);
  }

  [Test]
  public async Task Evaluate_AgedAndLoopingPastTheObservationBound_Layer2StillQuarantinesAsync() {
    // Age no longer quarantines, but it must not mask layer 2 either.
    var context = _context(_aged) with { DurableObservationCount = 12, ProcessingAttempts = 3 };

    var verdict = _detector().Evaluate(context);

    await Assert.That(verdict.ShouldQuarantine).IsTrue();
    await Assert.That(verdict.Reason).IsEqualTo(PoisonQuarantineReason.ObservationCountExceeded);
  }

  [Test]
  public async Task Evaluate_FreshMessage_ProceedsWithNoReasonAsync() {
    var verdict = _detector().Evaluate(_context(_now - TimeSpan.FromMinutes(1)));

    await Assert.That(verdict).IsEqualTo(PoisonVerdict.Proceed());
  }

  #endregion

  #region RecordAgedOnReceive

  [Test]
  public async Task RecordAgedOnReceive_CountsByGateAndLogsAWarningAsync() {
    var meterName = "Whizbang.Core.Tests.PoisonAgedOnReceive." + Guid.NewGuid();
    using var recorder = new AgedCounterRecorder(meterName);
    var logger = new RecordingLogger<PoisonMessageDetector>();
    var detector = _detector(logger, new Meter(meterName));

    ((IPoisonMessageDetector)detector).RecordAgedOnReceive(
      PoisonQuarantineGate.Receive, PoisonVerdict.ProceedAgedOnReceive("waited in a backlog"), _context(_aged),
      new Dictionary<string, object?> { ["transport"] = "test-transport" });

    await Assert.That(recorder.Collect()).IsEquivalentTo(new (string?, string?, long)[] { ("receive", "test-transport", 1L) });
    await Assert.That(logger.Contains(LogLevel.Warning, "Gate=receive")).IsTrue();
    await Assert.That(logger.Contains(LogLevel.Warning, MESSAGE_ID)).IsTrue();
    await Assert.That(logger.Contains(LogLevel.Warning, "waited in a backlog")).IsTrue();
  }

  [Test]
  public async Task RecordAgedOnReceive_NoAdditionalTagsAndWarningsDisabled_StillCountsAsync() {
    var meterName = "Whizbang.Core.Tests.PoisonAgedOnReceive." + Guid.NewGuid();
    using var recorder = new AgedCounterRecorder(meterName);
    var detector = _detector(new WarningsDisabledLogger(), new Meter(meterName));

    ((IPoisonMessageDetector)detector).RecordAgedOnReceive(
      PoisonQuarantineGate.Inbox, PoisonVerdict.ProceedAgedOnReceive("backlog"), _context(_aged));

    await Assert.That(recorder.Collect()).IsEquivalentTo(new (string?, string?, long)[] { ("inbox", null, 1L) });
  }

  [Test]
  public async Task RecordAgedOnReceive_OtherVerdicts_AreSilentAsync() {
    var meterName = "Whizbang.Core.Tests.PoisonAgedOnReceive." + Guid.NewGuid();
    using var recorder = new AgedCounterRecorder(meterName);
    var logger = new RecordingLogger<PoisonMessageDetector>();
    var detector = _detector(logger, new Meter(meterName));

    ((IPoisonMessageDetector)detector).RecordAgedOnReceive(PoisonQuarantineGate.Receive, PoisonVerdict.Proceed(), _context(_aged));
    ((IPoisonMessageDetector)detector).RecordAgedOnReceive(
      PoisonQuarantineGate.Receive,
      PoisonVerdict.Quarantine(PoisonQuarantineReason.ObservationCountExceeded, "looping"),
      _context(_aged));

    await Assert.That(recorder.Collect()).IsEmpty();
    await Assert.That(logger.Entries).IsEmpty();
  }

  [Test]
  public async Task AgedOnReceiveCounter_ReadsZeroPerGateAtConstructionAsync() {
    // Issue #711: every series exists at zero from construction, so a dashboard sees the counter
    // before the first backlog rather than discovering it during one.
    var meterName = "Whizbang.Core.Tests.PoisonAgedOnReceive." + Guid.NewGuid();
    using var recorder = new AgedCounterRecorder(meterName, includeZero: true);

    _ = _detector(meter: new Meter(meterName));

    var gates = recorder.Collect().ConvertAll(m => m.Gate);
    await Assert.That(gates).Contains("receive");
    await Assert.That(gates).Contains("inbox");
  }

  [Test]
  public async Task CustomDetector_DefaultRecordAgedOnReceive_IsANoOpAsync() {
    IPoisonMessageDetector custom = new MinimalDetector();

    await Assert.That(() => custom.RecordAgedOnReceive(
        PoisonQuarantineGate.Receive, PoisonVerdict.ProceedAgedOnReceive("backlog"), _context(_aged)))
      .ThrowsNothing()
      .Because("a custom detector written before #1663 keeps compiling and behaving as it did");
  }

  #endregion

  #region Doubles

  private sealed class MinimalDetector : IPoisonMessageDetector {
    public PoisonVerdict Evaluate(PoisonEvaluationContext context) => PoisonVerdict.Proceed();

    public void RecordQuarantine(
        PoisonQuarantineGate gate, PoisonVerdict verdict, PoisonEvaluationContext context,
        IReadOnlyDictionary<string, object?>? additionalTags = null) {
    }

    public void ReportAgeCapability(string transport, string entity, bool canSupplyTrustworthyAge) {
    }
  }

  private sealed class WarningsDisabledLogger : ILogger<PoisonMessageDetector> {
    public IDisposable BeginScope<TState>(TState state) where TState : notnull => NullScope.Instance;
    public bool IsEnabled(LogLevel logLevel) => false;
    public void Log<TState>(
        LogLevel logLevel, EventId eventId, TState state, Exception? exception,
        Func<TState, Exception?, string> formatter) {
    }
  }

  /// <summary>Reads the passive aged-on-receive counter for one meter, by gate and transport tag.</summary>
  private sealed class AgedCounterRecorder : IDisposable {
    private readonly MeterListener _listener;
    private readonly List<(string? Gate, string? Transport, long Value)> _measurements = [];

    public AgedCounterRecorder(string meterName, bool includeZero = false) {
      _listener = new MeterListener {
        InstrumentPublished = (instrument, l) => {
          if (instrument.Meter.Name == meterName
              && instrument.Name == PoisonMessageDetector.AGED_ON_RECEIVE_COUNTER_NAME) {
            l.EnableMeasurementEvents(instrument);
          }
        },
      };
      _listener.SetMeasurementEventCallback<long>((_, value, tags, _) => {
        if (value == 0 && !includeZero) { return; }
        string? gate = null;
        string? transport = null;
        foreach (var tag in tags) {
          if (tag.Key == "gate") { gate = tag.Value?.ToString(); }
          if (tag.Key == "transport") { transport = tag.Value?.ToString(); }
        }
        _measurements.Add((gate, transport, value));
      });
      _listener.Start();
    }

    public List<(string? Gate, string? Transport, long Value)> Collect() {
      _listener.RecordObservableInstruments();
      return [.. _measurements];
    }

    public void Dispose() => _listener.Dispose();
  }

  #endregion
}
