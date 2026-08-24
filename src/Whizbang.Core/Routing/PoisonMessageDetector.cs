using System.Diagnostics;
using System.Diagnostics.Metrics;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

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
  /// Layer 1 — the message is older than the age threshold. The broker's first-enqueue timestamp
  /// survives every redelivery, so this fires where delivery-count-based valves cannot.
  /// </summary>
  MessageAgeExceeded,

  /// <summary>
  /// Layer 2 — this service has durably observed the same message id more times than the bound
  /// allows. Covers poison that dies mid-processing rather than mid-lock, and the surfaces where
  /// no trustworthy first-enqueue timestamp exists.
  /// </summary>
  ObservationCountExceeded,
}

/// <summary>
/// Transport-NEUTRAL facts a poison verdict is made from. Every field a transport cannot supply is
/// nullable, and a null field is inert — never defaulted into a value that would manufacture a
/// quarantine.
/// </summary>
/// <param name="MessageId">Broker or envelope message id, for logs and metrics.</param>
/// <param name="FirstEnqueuedAt">
/// Broker-supplied first-enqueue timestamp. Azure Service Bus always has one; RabbitMQ's is
/// publisher-set and therefore optional. <c>null</c> disables layer 1 for this message.
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
  /// A surface that cannot degrades from layer 1 to layer 2 — and says so, loudly and once, on the
  /// log and health surfaces. Layer 1 must never go silently inert; that silence is exactly how
  /// the delivery-count valve failed.
  /// </summary>
  /// <param name="transport">Transport identifier, e.g. <c>azure-service-bus</c>.</param>
  /// <param name="entity">The broker entity the surface consumes.</param>
  /// <param name="canSupplyTrustworthyAge">Whether a usable first-enqueue timestamp is present.</param>
  void ReportAgeCapability(string transport, string entity, bool canSupplyTrustworthyAge);
}

/// <summary>
/// Default two-layer detector. Layer 1 quarantines on broker age; layer 2 quarantines on durable
/// redelivery observations. The broker delivery count is carried but never acted on — see
/// <see cref="PoisonEvaluationContext.BrokerDeliveryCount"/>.
/// </summary>
/// <docs>fundamentals/dispatcher/routing#poison-messages</docs>
/// <tests>tests/Whizbang.Core.Tests/Routing/PoisonMessageDetectorTests.cs</tests>
public sealed class PoisonMessageDetector : IPoisonMessageDetector {
  private readonly PoisonMessageOptions _options;
  private readonly TimeSpan _ageThreshold;
  private readonly ILogger<PoisonMessageDetector> _logger;
  private readonly Counter<long> _quarantinedCounter;
  private readonly PoisonDetectionCapabilityState? _capabilityState;

#pragma warning disable CA1707 // Repo style: public const fields are ALL_CAPS_SNAKE per editorconfig.
  /// <summary>OTel meter name for the quarantine counter.</summary>
  public const string METER_NAME = "Whizbang.Core.Routing.PoisonMessageDetector";

  /// <summary>OTel counter name. Keep stable — dashboards depend on it.</summary>
  public const string COUNTER_NAME = "whizbang.message.quarantined";
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
    _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    _capabilityState = capabilityState;
    _quarantinedCounter = meter.CreateCounter<long>(
      COUNTER_NAME,
      unit: "{message}",
      description: "Count of messages quarantined by the poison detector (age | durable observations).");
  }

  /// <inheritdoc />
  public PoisonVerdict Evaluate(PoisonEvaluationContext context) {
    if (!_options.Enabled) {
      return PoisonVerdict.Proceed();
    }

    // Layer 1 — age. The only signal that survives a session lock-loss storm. A null timestamp is
    // INERT, never defaulted: DateTimeOffset.MinValue would quarantine every message on earth.
    if (context.FirstEnqueuedAt is { } firstEnqueuedAt) {
      var age = context.Now - firstEnqueuedAt;
      if (age > _ageThreshold) {
        return PoisonVerdict.Quarantine(
          PoisonQuarantineReason.MessageAgeExceeded,
          $"Message '{context.MessageId}' first enqueued at {firstEnqueuedAt:O} is {age} old, past the "
          + $"{_ageThreshold} threshold; the broker delivery counter cannot bound this loop on a "
          + "session-enabled entity, so it is quarantined on age.");
      }
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

    return PoisonVerdict.Proceed();
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
      + "trustworthy first-enqueue timestamp, so quarantine falls back to durable observation counting "
      + "(bound {MaxDurableObservations}). Messages older than {AgeThreshold} will NOT be quarantined on age here.",
      transport, entity, _options.MaxDurableObservations, _ageThreshold);
#pragma warning restore CA1848
  }

  private static string _gateTag(PoisonQuarantineGate gate) => gate switch {
    PoisonQuarantineGate.Receive => "receive",
    PoisonQuarantineGate.Inbox => "inbox",
    _ => gate.ToString(),
  };
}
