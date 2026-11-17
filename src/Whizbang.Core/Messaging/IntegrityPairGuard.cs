// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using System.Collections.Concurrent;
using System.Diagnostics.Metrics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Whizbang.Core.Observability;
using Whizbang.Core.Rules;

namespace Whizbang.Core.Messaging;

/// <summary>Which end of a service pair this service is for one integrity message (#2362).</summary>
/// <docs>resilience/stream-integrity#the-pair-guard</docs>
public enum IntegrityPairSide {
  /// <summary>This service is the origin: it answers the peer's manifest or redelivery requests.</summary>
  Origin = 0,

  /// <summary>This service is the consumer: it compares the peer's manifests and checkpoints.</summary>
  Consumer = 1,
}

/// <summary>One service pair's guard state, as the guard last saw it (#2362).</summary>
/// <param name="Peer">The other service's name.</param>
/// <param name="Side">Which end this service is.</param>
/// <param name="Verdict">The last comparison.</param>
/// <param name="HaltedSince">When the pair was first seen halted, or null while it agrees.</param>
/// <param name="Alerted">True once the halt passed <see cref="IntegrityPairGuardOptions.HaltedAlertAfter"/> and was alerted.</param>
/// <docs>resilience/stream-integrity#the-pair-guard</docs>
public sealed record IntegrityPairState(
  string Peer, IntegrityPairSide Side, RulePairVerdict Verdict, DateTimeOffset? HaltedSince, bool Alerted);

/// <summary>
/// The pair guard (#2362): integrity runs for a service pair only while both apply the same eligibility rules to the
/// event types they share. Every integrity receptor asks it before comparing, repairing or redelivering; on a mismatch
/// the pair is halted, with an error naming both sides' versions and the types that differ, a halted-pair metric, and an
/// alert once it stays halted past <see cref="IntegrityPairGuardOptions.HaltedAlertAfter"/>. The pair resumes on its own
/// with the first message whose rules match.
/// </summary>
/// <remarks>
/// A peer that sends no fingerprint runs a release that predates it and may compute digests another way, so it is a
/// mismatch: a rolling deploy halts a mixed pair instead of repairing losses that are not real.
/// </remarks>
/// <docs>resilience/stream-integrity#the-pair-guard</docs>
/// <tests>tests/Whizbang.Core.Tests/Messaging/IntegrityPairGuardTests.cs</tests>
/// <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/TwoServicePairGuardTests.cs</tests>
public sealed partial class IntegrityPairGuard {
  /// <summary>The <c>state</c> attribute of a pair that agrees.</summary>
  public const string STATE_AGREED = "agreed";

  /// <summary>The <c>state</c> attribute of a halted pair.</summary>
  public const string STATE_HALTED = "halted";

  private readonly ConcurrentDictionary<(string Peer, IntegrityPairSide Side), IntegrityPairState> _pairs = new();
  private readonly IRuleFingerprintSource _rules;
  private readonly IntegrityPairGuardOptions _options;
  private readonly TimeProvider _time;
  private readonly ILogger _logger;
  private readonly Counter<long>? _halts;
  private readonly Counter<long>? _alerts;

  /// <summary>A guard over this service's rules.</summary>
  /// <param name="rules">This service's rules.</param>
  /// <param name="options">The integrity options; the guard reads <see cref="StreamIntegrityOptions.PairGuard"/>.</param>
  /// <param name="logger">Where halts, alerts and resumptions are logged.</param>
  /// <param name="time">The clock the halt age is measured on; null for the system clock.</param>
  /// <param name="metrics">The metrics root, or null for none.</param>
  public IntegrityPairGuard(
      IRuleFingerprintSource rules, IOptions<StreamIntegrityOptions> options, ILogger<IntegrityPairGuard> logger,
      TimeProvider? time = null, WhizbangMetrics? metrics = null) {
    _rules = ArgumentGuard.NotNull(rules);
    _options = (options?.Value ?? throw new ArgumentNullException(nameof(options))).PairGuard;
    _logger = ArgumentGuard.NotNull(logger);
    _time = time ?? TimeProvider.System;
    if (metrics is not null) {
      var meter = metrics.MeterFactory.Create(StreamIntegrityMetrics.METER_NAME);
      _halts = meter.CreateCounter<long>("whizbang.stream_integrity.pair_halts",
        description: "Service pairs halted because their eligibility rules differ; tagged by reason and side");
      _alerts = meter.CreateCounter<long>("whizbang.stream_integrity.pair_halt_alerts",
        description: "Halted service pairs that stayed halted past PairGuard:HaltedAlertAfter; tagged by side");
      _ = meter.CreateObservableGauge("whizbang.stream_integrity.pair_state", _stateMeasurements,
        description: "1 for each service pair in its current state (agreed | halted), by peer and side");
      _ = meter.CreateObservableGauge("whizbang.stream_integrity.pair_halted_seconds", _haltedSecondsMeasurements, unit: "s",
        description: "How long each halted service pair has been halted, by peer and side");
      _ = meter.CreateObservableGauge("whizbang.stream_integrity.rule_fingerprint_info",
        () => new Measurement<long>(1, new KeyValuePair<string, object?>("fingerprint", ServiceFingerprint)),
        description: "Info gauge: this service's rule fingerprint over every event type it knows");
    }
  }

  /// <summary>
  /// The guard a receptor falls back to when none is registered: it knows no type (so it compares versions only),
  /// keeps state per process and logs nothing. Integrity is never unguarded.
  /// </summary>
  public static IntegrityPairGuard Fallback { get; } = new(
    CatalogRuleFingerprintSource.Empty, Options.Create(new StreamIntegrityOptions()), NullLogger<IntegrityPairGuard>.Instance);

  /// <summary>The registered guard, or <see cref="Fallback"/>.</summary>
  /// <param name="services">The scope's services.</param>
  /// <returns>The guard.</returns>
  public static IntegrityPairGuard Resolve(IServiceProvider services) =>
    ArgumentGuard.NotNull(services).GetService<IntegrityPairGuard>() ?? Fallback;

  /// <summary>This service's rule fingerprint over every event type it knows.</summary>
  public string ServiceFingerprint => _rules.RulesFor(null).Fingerprint();

  /// <summary>This service's rules for <paramref name="eventTypes"/>, as a message carries them.</summary>
  /// <param name="eventTypes">The types the message is about; null for every type this service knows.</param>
  /// <returns>The fingerprint to stamp.</returns>
  public RuleFingerprint Stamp(IEnumerable<string>? eventTypes) => RuleFingerprint.From(_rules.RulesFor(eventTypes));

  /// <summary>The pairs seen so far, as last seen.</summary>
  public IReadOnlyList<IntegrityPairState> Pairs => [.. _pairs.Values];

  /// <summary>
  /// Compares a peer's rules with this service's over the types they share, records the pair's state and reports a
  /// change: an error when it halts (or halts for another reason), a warning when it stays halted past the alert
  /// threshold, and an information line when it resumes.
  /// </summary>
  /// <param name="peer">The other service's name.</param>
  /// <param name="side">Which end this service is.</param>
  /// <param name="peerRules">The fingerprint the peer's message carried, or null when it carried none.</param>
  /// <returns>The verdict: integrity runs for the message only when it agrees.</returns>
  public RulePairVerdict Admit(string peer, IntegrityPairSide side, RuleFingerprint? peerRules) {
    ArgumentNullException.ThrowIfNull(peer);
    var theirs = peerRules?.ToRuleSet();
    var mine = _rules.RulesFor(theirs?.TypeHashes.Keys ?? []);
    var verdict = mine.CompareWith(theirs);
    var now = _time.GetUtcNow();
    var key = (peer, side);
    var previous = _pairs.TryGetValue(key, out var known) ? known : null;
    if (verdict.IsAgreed) {
      _pairs[key] = new IntegrityPairState(peer, side, verdict, null, false);
      if (previous?.HaltedSince is { } since) {
        var resumedAfter = now - since;
        LogPairResumed(_logger, peer, side, resumedAfter);
      }
      return verdict;
    }

    var sameHalt = previous is { HaltedSince: not null } && previous.Verdict.Reason == verdict.Reason
      && previous.Verdict.DifferingTypes.SequenceEqual(verdict.DifferingTypes, StringComparer.Ordinal);
    var haltedSince = previous?.HaltedSince ?? now;
    var alerted = previous?.Alerted ?? false;
    var reason = ReasonTag(verdict.Reason);
    var sideTag = _sideTag(side);
    if (!sameHalt) {
      _halts?.Add(1, new KeyValuePair<string, object?>("reason", reason), new KeyValuePair<string, object?>("side", sideTag));
      var localFingerprint = mine.Fingerprint();
      var differing = string.Join(", ", verdict.DifferingTypes);
      LogPairHalted(_logger, peer, side, reason, mine.SemanticsVersion, mine.EligibilitySqlVersion, localFingerprint,
        theirs?.SemanticsVersion, theirs?.EligibilitySqlVersion, peerRules?.Fingerprint, differing);
    }
    var haltedFor = now - haltedSince;
    if (!alerted && haltedFor >= _options.HaltedAlertAfter) {
      alerted = true;
      _alerts?.Add(1, new KeyValuePair<string, object?>("side", sideTag));
      LogPairHaltAlert(_logger, peer, side, reason, haltedFor, _options.HaltedAlertAfter);
    }
    _pairs[key] = new IntegrityPairState(peer, side, verdict, haltedSince, alerted);
    return verdict;
  }

  /// <summary>The <c>reason</c> attribute of a halt.</summary>
  /// <param name="reason">The reason.</param>
  /// <returns>Its snake_case name.</returns>
  public static string ReasonTag(RulePairHaltReason reason) => reason switch {
    RulePairHaltReason.NoFingerprint => "no_fingerprint",
    RulePairHaltReason.SemanticsVersion => "semantics_version",
    RulePairHaltReason.EligibilitySqlVersion => "eligibility_sql_version",
    RulePairHaltReason.TypeRules => "type_rules",
    _ => "none",
  };

  private static string _sideTag(IntegrityPairSide side) => side == IntegrityPairSide.Origin ? "origin" : "consumer";

  private IEnumerable<Measurement<long>> _stateMeasurements() =>
    _pairs.Values.Select(p => new Measurement<long>(1,
      new KeyValuePair<string, object?>("peer", p.Peer),
      new KeyValuePair<string, object?>("side", _sideTag(p.Side)),
      new KeyValuePair<string, object?>("state", p.HaltedSince is null ? STATE_AGREED : STATE_HALTED)));

  private IEnumerable<Measurement<double>> _haltedSecondsMeasurements() {
    var now = _time.GetUtcNow();
    return _pairs.Values.Where(p => p.HaltedSince is not null).Select(p => new Measurement<double>(
      (now - p.HaltedSince!.Value).TotalSeconds,
      new KeyValuePair<string, object?>("peer", p.Peer),
      new KeyValuePair<string, object?>("side", _sideTag(p.Side))));
  }

  [LoggerMessage(EventId = 1, Level = LogLevel.Error,
    Message = "Integrity halted for the pair with '{Peer}' (this service is the {Side}): {Reason}. This service runs rule "
            + "semantics {LocalSemantics}, eligibility SQL {LocalSql}, fingerprint {LocalFingerprint}; the peer runs "
            + "{PeerSemantics}, {PeerSql}, {PeerFingerprint}. Types whose rules differ: [{DifferingTypes}]. Nothing is "
            + "compared, repaired or redelivered for the pair until its rules match; it resumes on its own")]
  static partial void LogPairHalted(ILogger logger, string peer, IntegrityPairSide side, string reason,
    int localSemantics, int localSql, string localFingerprint, int? peerSemantics, int? peerSql, string? peerFingerprint,
    string differingTypes);

  [LoggerMessage(EventId = 2, Level = LogLevel.Critical,
    Message = "Integrity for the pair with '{Peer}' (this service is the {Side}) has been halted for {HaltedFor} ({Reason}), "
            + "past the alert threshold of {Threshold}: a rollout that should have finished, or two services built from "
            + "different rules. Align their releases or their declared rules")]
  static partial void LogPairHaltAlert(ILogger logger, string peer, IntegrityPairSide side, string reason,
    TimeSpan haltedFor, TimeSpan threshold);

  [LoggerMessage(EventId = 3, Level = LogLevel.Information,
    Message = "Integrity resumed for the pair with '{Peer}' (this service is the {Side}) after {HaltedFor}: the rules match")]
  static partial void LogPairResumed(ILogger logger, string peer, IntegrityPairSide side, TimeSpan haltedFor);
}
