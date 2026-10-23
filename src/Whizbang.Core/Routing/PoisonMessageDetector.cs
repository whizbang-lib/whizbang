// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using System.Diagnostics;
using System.Diagnostics.Metrics;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Whizbang.Core.Observability;

namespace Whizbang.Core.Routing;

/// <summary>
/// Which gate quarantined the message. Used as an OTel tag value.
/// </summary>
public enum PoisonQuarantineGate {
  /// <summary>Transport receive boundary — message just pulled off the broker (layer 1).</summary>
  Receive,
  /// <summary>Inbox store gate — a redelivery observation recorded against an existing row (layer 2).</summary>
  Inbox,
}

/// <summary>
/// Why a message was quarantined. <see cref="None"/> accompanies a proceed verdict.
/// </summary>
public enum PoisonQuarantineReason {
  /// <summary>No quarantine — the message proceeds.</summary>
  None,

  /// <summary>
  /// The message is older than the age threshold. The default detector no longer produces this
  /// (issue #1663): the receive gate never quarantines on age, it reports
  /// <see cref="AgedOnReceive"/> instead. Kept so a custom detector can still quarantine on age and
  /// so dead-letter records written before the change still read.
  /// </summary>
  MessageAgeExceeded,

  /// <summary>
  /// Layer 2 — this service has durably observed the same message id more times than the bound
  /// allows. Covers poison that dies mid-processing rather than mid-lock, and the surfaces where
  /// no trustworthy first-enqueue timestamp exists.
  /// </summary>
  ObservationCountExceeded,

  /// <summary>
  /// Accompanies a PROCEED verdict at the receive gate: the message is older than the age threshold
  /// (issue #1663). The receive gate never quarantines on age, because age measures queueing, not
  /// failure, and a backlog ages every message in it. The message is accepted and stored; the
  /// verdict names the case so the caller can make the backlog visible with
  /// <see cref="IPoisonMessageDetector.RecordAgedOnReceive"/>.
  /// </summary>
  AgedOnReceive,
}

/// <summary>
/// Transport-NEUTRAL facts a poison verdict is made from. Every field a transport cannot supply is
/// nullable, and a null field is inert — never defaulted into a value that would manufacture a
/// quarantine.
/// </summary>
/// <param name="MessageId">Broker or envelope message id, for logs and metrics.</param>
/// <param name="FirstEnqueuedAt">
/// Broker-supplied first-enqueue timestamp. Azure Service Bus always has one; RabbitMQ's is
/// publisher-set and therefore optional. Used only to REPORT an aged message on receive; it never
/// quarantines (issue #1663). <c>null</c> means the age cannot be judged for this message.
/// </param>
/// <param name="BrokerDeliveryCount">
/// The broker's delivery counter. Carried for telemetry and for custom detectors ONLY — the
/// default detector deliberately does not act on it, because on session-enabled entities it does
/// not rise under connection-death lock loss, and where it does rise the broker's own
/// MaxDeliveryCount valve and the transport's existing failure branch already own the decision.
/// </param>
/// <param name="DurableObservationCount">
/// How many times this service has durably recorded a delivery of this message id, including the
/// current one. <c>null</c> when the caller has no durable observation to report.
/// </param>
/// <param name="Now">Evaluation instant — injected, never read from the ambient clock.</param>
/// <docs>fundamentals/dispatcher/routing#poison-messages</docs>
/// <tests>tests/Whizbang.Core.Tests/Routing/PoisonMessageDetectorTests.cs</tests>
public readonly record struct PoisonEvaluationContext(
  string MessageId,
  DateTimeOffset? FirstEnqueuedAt,
  int? BrokerDeliveryCount,
  int? DurableObservationCount,
  DateTimeOffset Now) {

  /// <summary>
  /// How many times a receptor has actually ATTEMPTED this message, when the caller can supply it.
  /// </summary>
  /// <remarks>
  /// Distinct from <c>DurableObservationCount</c>, which counts DELIVERIES. A broadcast fanned out to
  /// more subscriptions than the observation bound crosses that bound without any receptor having
  /// tried it, so quarantining on deliveries alone destroys messages that never failed.
  /// <see langword="null"/> means UNMEASURED — never treated as "zero failures".
  /// </remarks>
  public int? ProcessingAttempts { get; init; }
}

/// <summary>
/// Outcome of a poison evaluation. <see cref="Reason"/> is <see cref="PoisonQuarantineReason.None"/>
/// when <see cref="ShouldQuarantine"/> is <c>false</c>.
/// </summary>
/// <param name="ShouldQuarantine">Whether the caller must quarantine instead of processing.</param>
/// <param name="Reason">Which layer fired.</param>
/// <param name="Detail">Human-readable diagnosis, carried onto the dead-letter description.</param>
public readonly record struct PoisonVerdict(
  bool ShouldQuarantine,
  PoisonQuarantineReason Reason,
  string? Detail = null) {

  /// <summary>The message proceeds.</summary>
  /// <returns>A non-quarantining verdict.</returns>
  public static PoisonVerdict Proceed() => new(ShouldQuarantine: false, PoisonQuarantineReason.None);

  /// <summary>The message is quarantined.</summary>
  /// <param name="reason">Which layer fired.</param>
  /// <param name="detail">Human-readable diagnosis for the dead-letter description.</param>
  /// <returns>A quarantining verdict.</returns>
  public static PoisonVerdict Quarantine(PoisonQuarantineReason reason, string detail) =>
    new(ShouldQuarantine: true, reason, detail);

  /// <summary>
  /// The message proceeds although it is past the age threshold (issue #1663). Carries
  /// <see cref="PoisonQuarantineReason.AgedOnReceive"/> so the caller can report the backlog with
  /// <see cref="IPoisonMessageDetector.RecordAgedOnReceive"/>.
  /// </summary>
  /// <param name="detail">Human-readable diagnosis for the log line.</param>
  /// <returns>A non-quarantining verdict naming the aged case.</returns>
  public static PoisonVerdict ProceedAgedOnReceive(string detail) =>
    new(ShouldQuarantine: false, PoisonQuarantineReason.AgedOnReceive, detail);
}

/// <summary>
/// The single "is this message poison?" decision, shared by every transport. Follows the
/// <see cref="IMessageDiscardPolicy"/> shape exactly: the DECISION lives in Core, the EXECUTION
/// lives in each transport, and the policy is an OPTIONAL injected dependency — a null detector
/// means pre-phase-8.5 behavior, so <c>ITransport</c> gains no member and custom or in-process
/// transports are unaffected.
/// </summary>
/// <remarks>
/// It is deliberately not on <c>ITransport</c> (that surface is publish/subscribe capability, and
/// every implementation including the in-process transport and the test doubles would be forced to
/// carry it) and deliberately not transport-private (the decision is identical everywhere and would
/// drift).
/// </remarks>
/// <docs>fundamentals/dispatcher/routing#poison-messages</docs>
/// <tests>tests/Whizbang.Core.Tests/Routing/PoisonMessageDetectorTests.cs</tests>
public interface IPoisonMessageDetector {
  /// <summary>
  /// Pure decision over transport-neutral facts. No side effects — callers act on the verdict and
  /// then call <see cref="RecordQuarantine"/> exactly once.
  /// </summary>
  /// <param name="context">The facts.</param>
  /// <returns>Proceed, or quarantine with a reason.</returns>
  PoisonVerdict Evaluate(PoisonEvaluationContext context);

  /// <summary>
  /// Records a quarantine — structured warning plus the <c>whizbang.message.quarantined</c> OTel
  /// counter. No-op for a proceed verdict, so callers can invoke it unconditionally.
  /// </summary>
  /// <param name="gate">Which gate acted.</param>
  /// <param name="verdict">The verdict acted on.</param>
  /// <param name="context">The facts the verdict was made from.</param>
  /// <param name="additionalTags">Extra OTel tags (entity, subscription, …).</param>
  void RecordQuarantine(
    PoisonQuarantineGate gate,
    PoisonVerdict verdict,
    PoisonEvaluationContext context,
    IReadOnlyDictionary<string, object?>? additionalTags = null);

  /// <summary>
  /// Declares whether a receive surface can supply a trustworthy broker first-enqueue timestamp.
  /// A surface that cannot has no aged-on-receive reporting (layer 1 only reports since issue
  /// #1663) — and says so, loudly and once, on the log and health surfaces; redelivery loops stay
  /// bounded by layer 2. A signal must never go silently inert; that silence is exactly how the
  /// delivery-count valve failed.
  /// </summary>
  /// <param name="transport">Transport identifier, e.g. <c>azure-service-bus</c>.</param>
  /// <param name="entity">The broker entity the surface consumes.</param>
  /// <param name="canSupplyTrustworthyAge">Whether a usable first-enqueue timestamp is present.</param>
  void ReportAgeCapability(string transport, string entity, bool canSupplyTrustworthyAge);

  /// <summary>
  /// Records an aged message that the receive gate accepted (issue #1663): structured warning plus
  /// the <c>whizbang.message.aged_on_receive</c> OTel counter, so a backlog stays visible. No-op for
  /// every other verdict, so callers can invoke it unconditionally. The default implementation does
  /// nothing, which keeps a custom detector's behavior as it was.
  /// </summary>
  /// <param name="gate">Which gate evaluated the message.</param>
  /// <param name="verdict">The verdict acted on.</param>
  /// <param name="context">The facts the verdict was made from.</param>
  /// <param name="additionalTags">Extra OTel tags (entity, subscription, …).</param>
  void RecordAgedOnReceive(
      PoisonQuarantineGate gate,
      PoisonVerdict verdict,
      PoisonEvaluationContext context,
      IReadOnlyDictionary<string, object?>? additionalTags = null) {
  }
}

/// <summary>
/// Default two-layer detector. Layer 1, at the receive gate, never quarantines: it reports a message
/// older than the age threshold and lets it be stored (issue #1663), because age measures queueing,
/// not failure, and a backlog ages every message in it. Layer 2 quarantines on durable redelivery
/// observations after storage, which is what bounds a redelivery loop, including a session lock-loss
/// storm whose broker delivery count never moves. The broker delivery count is carried but never acted on — see
/// <see cref="PoisonEvaluationContext.BrokerDeliveryCount"/>.
/// </summary>
/// <docs>fundamentals/dispatcher/routing#poison-messages</docs>
/// <tests>tests/Whizbang.Core.Tests/Routing/PoisonMessageDetectorTests.cs</tests>
public sealed class PoisonMessageDetector : IPoisonMessageDetector {
  private readonly PoisonMessageOptions _options;
  private readonly TimeSpan _ageThreshold;
  private readonly ILogger<PoisonMessageDetector> _logger;
  private readonly PassiveCounter<long> _quarantinedCounter;
  private readonly PassiveCounter<long> _agedOnReceiveCounter;
  private readonly PoisonDetectionCapabilityState? _capabilityState;

#pragma warning disable CA1707 // Repo style: public const fields are ALL_CAPS_SNAKE per editorconfig.
  /// <summary>OTel meter name for the quarantine counter.</summary>
  public const string METER_NAME = "Whizbang.Core.Routing.PoisonMessageDetector";

  /// <summary>OTel counter name. Keep stable — dashboards depend on it.</summary>
  public const string COUNTER_NAME = "whizbang.message.quarantined";

  /// <summary>
  /// OTel counter for aged messages the receive gate accepted (issue #1663). Tagged by <c>gate</c>.
  /// Keep stable — dashboards depend on it.
  /// </summary>
  public const string AGED_ON_RECEIVE_COUNTER_NAME = "whizbang.message.aged_on_receive";
#pragma warning restore CA1707

  /// <summary>Constructs the default detector.</summary>
  /// <param name="options">Thresholds and the killswitch.</param>
  /// <param name="logger">Diagnostic logger.</param>
  /// <param name="meter">OTel meter for the quarantine counter.</param>
  /// <param name="capabilityState">
  /// Shared age-capability record backing the health surface. Optional — when null, capability
  /// reports still log but do not reach the health endpoint.
  /// </param>
  /// <exception cref="ArgumentNullException">Thrown when options, logger, or meter is null.</exception>
  public PoisonMessageDetector(
      IOptions<PoisonMessageOptions> options,
      ILogger<PoisonMessageDetector> logger,
      Meter meter,
      PoisonDetectionCapabilityState? capabilityState = null) {
    ArgumentNullException.ThrowIfNull(options);
    ArgumentNullException.ThrowIfNull(meter);
    _options = options.Value ?? throw new ArgumentNullException(nameof(options));
    _ageThreshold = _options.EffectiveAgeThreshold;
    ArgumentNullException.ThrowIfNull(logger);
    _logger = logger;
    _capabilityState = capabilityState;
    _quarantinedCounter = meter.CreatePassiveCounter<long>(
      COUNTER_NAME,
      unit: "{message}",
      description: "Count of messages quarantined by the poison detector (durable observations).");
    _agedOnReceiveCounter = meter.CreatePassiveCounter<long>(
      AGED_ON_RECEIVE_COUNTER_NAME,
      unit: "{message}",
      description: "Count of messages older than the poison age threshold that the receive gate accepted.");
    // Issue #711: one series per gate exists at zero from construction (see PassiveCounter).
    foreach (var gate in Enum.GetValues<PoisonQuarantineGate>()) {
      _quarantinedCounter.Touch(new KeyValuePair<string, object?>("gate", _gateTag(gate)));
      _agedOnReceiveCounter.Touch(new KeyValuePair<string, object?>("gate", _gateTag(gate)));
    }
  }

  /// <inheritdoc />
  public PoisonVerdict Evaluate(PoisonEvaluationContext context) {
    if (!_options.Enabled) {
      return PoisonVerdict.Proceed();
    }

    // Layer 2 — durable redelivery observations. Bounds the loop where no trustworthy timestamp
    // exists, and catches poison that dies mid-processing rather than mid-lock.
    // The bound alone is NOT sufficient. The observation counter counts DELIVERIES, so a message
    // broadcast to more subscriptions than the bound crosses it without any receptor having tried
    // it — quarantining then destroys a message that never failed. Not hypothetical: this
    // dead-lettered control-plane broadcasts on a healthy system, clustered exactly one past the
    // bound with an attempt count of zero.
    //
    // "Redelivery is not making progress" is a claim about PROCESSING, so processing evidence is
    // required before it can be made. Null attempts means UNMEASURED, never "zero failures so far" —
    // destroying a message on the strength of a reading nobody took is the dangerous direction to be
    // wrong in.
    if (context.DurableObservationCount is { } observations
        && observations >= _options.MaxDurableObservations
        && context.ProcessingAttempts is { } attempts
        && attempts > 0) {
      return PoisonVerdict.Quarantine(
        PoisonQuarantineReason.ObservationCountExceeded,
        $"Message '{context.MessageId}' has been durably observed {observations} times, at or past the "
        + $"{_options.MaxDurableObservations} bound, after {attempts} processing attempt(s); "
        + "redelivery is not making progress.");
    }

    // Layer 1 — age at the receive gate (issue #1663). It REPORTS, never quarantines: age measures
    // queueing, not failure, so a backlog that took longer than the threshold to drain would be
    // dead-lettered on its first delivery. Every message is accepted until it is stored; a loop is
    // bounded after storage by layer 2, whose observation counter rises on every redelivery of a
    // stored message before any handler runs. A null timestamp is INERT, never defaulted.
    if (context.FirstEnqueuedAt is { } firstEnqueuedAt && context.Now - firstEnqueuedAt > _ageThreshold) {
      return PoisonVerdict.ProceedAgedOnReceive(
        $"Message '{context.MessageId}' first enqueued at {firstEnqueuedAt:O} is {context.Now - firstEnqueuedAt} "
        + $"old, past the {_ageThreshold} threshold; it is accepted and stored, because age alone is "
        + "a backlog, not a failure.");
    }

    return PoisonVerdict.Proceed();
  }

  /// <inheritdoc />
  public void RecordAgedOnReceive(
      PoisonQuarantineGate gate,
      PoisonVerdict verdict,
      PoisonEvaluationContext context,
      IReadOnlyDictionary<string, object?>? additionalTags = null) {
    if (verdict.ShouldQuarantine || verdict.Reason != PoisonQuarantineReason.AgedOnReceive) {
      return;
    }

    if (_logger.IsEnabled(LogLevel.Warning)) {
#pragma warning disable CA1848 // Diagnostic logging on a backlog path; LoggerMessage source-gen buys nothing here.
      _logger.LogWarning(
        "Whizbang aged message accepted. Gate={Gate} MessageId={MessageId} "
        + "BrokerDeliveryCount={BrokerDeliveryCount} Detail={Detail}",
        _gateTag(gate), context.MessageId, context.BrokerDeliveryCount, verdict.Detail);
#pragma warning restore CA1848
    }

    var tags = new TagList { { "gate", _gateTag(gate) } };
    if (additionalTags is not null) {
      foreach (var (key, value) in additionalTags) { tags.Add(key, value); }
    }
    _agedOnReceiveCounter.Add(1, tags);
  }

  /// <inheritdoc />
  public void RecordQuarantine(
      PoisonQuarantineGate gate,
      PoisonVerdict verdict,
      PoisonEvaluationContext context,
      IReadOnlyDictionary<string, object?>? additionalTags = null) {
    if (!verdict.ShouldQuarantine) {
      return;
    }

    if (_logger.IsEnabled(LogLevel.Warning)) {
#pragma warning disable CA1848 // Diagnostic logging on a rare path; LoggerMessage source-gen buys nothing here.
      _logger.LogWarning(
        "Whizbang message quarantined. Gate={Gate} Reason={Reason} MessageId={MessageId} "
        + "BrokerDeliveryCount={BrokerDeliveryCount} DurableObservations={DurableObservations} Detail={Detail}",
        _gateTag(gate), verdict.Reason, context.MessageId,
        context.BrokerDeliveryCount, context.DurableObservationCount, verdict.Detail);
#pragma warning restore CA1848
    }

    var tags = new TagList {
      { "gate", _gateTag(gate) },
      { "reason", verdict.Reason.ToString() },
    };
    if (additionalTags is not null) {
      foreach (var (key, value) in additionalTags) { tags.Add(key, value); }
    }
    _quarantinedCounter.Add(1, tags);
  }

  /// <inheritdoc />
  public void ReportAgeCapability(string transport, string entity, bool canSupplyTrustworthyAge) {
    var newlyDegraded = _capabilityState?.ReportAgeCapability(transport, entity, canSupplyTrustworthyAge);

    // Log on the transition into degraded. When no capability state is wired there is nothing to
    // deduplicate against, so an incapable report still speaks — once per report, by design: a
    // silent fallback is the failure mode this whole phase exists to correct.
    var shouldLog = newlyDegraded ?? !canSupplyTrustworthyAge;
    if (!shouldLog || !_logger.IsEnabled(LogLevel.Warning)) {
      return;
    }

#pragma warning disable CA1848 // One-shot startup-class diagnostic; LoggerMessage source-gen buys nothing here.
    _logger.LogWarning(
      "Whizbang age-based poison detection is INERT on {Transport} entity {Entity}: the broker supplies no "
      + "trustworthy first-enqueue timestamp, so messages older than {AgeThreshold} are not reported as aged "
      + "here. Redelivery loops stay bounded by durable observation counting (bound {MaxDurableObservations}).",
      transport, entity, _ageThreshold, _options.MaxDurableObservations);
#pragma warning restore CA1848
  }

  private static string _gateTag(PoisonQuarantineGate gate) => gate switch {
    PoisonQuarantineGate.Receive => "receive",
    PoisonQuarantineGate.Inbox => "inbox",
    _ => gate.ToString(),
  };
}
