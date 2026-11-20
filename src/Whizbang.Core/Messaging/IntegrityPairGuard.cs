// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using System.Collections.Concurrent;
using System.Diagnostics.CodeAnalysis;
using System.Diagnostics.Metrics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Whizbang.Core.Observability;
using Whizbang.Rules;

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
/// One pair's halt as the store keeps it (#2377): who and which end, why, both sides' rule versions and fingerprints,
/// when it began and when it was alerted. A service keeps one open halt per peer and side.
/// </summary>
/// <param name="Peer">The other service's name.</param>
/// <param name="Side">Which end this service is.</param>
/// <param name="Reason">Why the pair is halted.</param>
/// <param name="DifferingTypes">The shared types whose rules differ; empty unless the reason is the types' rules.</param>
/// <param name="LocalSemanticsVersion">This service's rule-semantics version.</param>
/// <param name="LocalEligibilitySqlVersion">This service's eligibility SQL version.</param>
/// <param name="LocalFingerprint">This service's rule fingerprint over the types the pair shares.</param>
/// <param name="PeerSemanticsVersion">The peer's rule-semantics version; null when it sent no fingerprint.</param>
/// <param name="PeerEligibilitySqlVersion">The peer's eligibility SQL version; null when it sent no fingerprint.</param>
/// <param name="PeerFingerprint">The peer's fingerprint; null when it sent none.</param>
/// <param name="HaltedAt">When the pair was first seen halted.</param>
/// <param name="AlertedAt">When the halt was alerted, or null while it has not been.</param>
/// <docs>resilience/stream-integrity#the-pair-guard</docs>
[System.Diagnostics.CodeAnalysis.SuppressMessage("Major Code Smell", "S107:Methods should not have too many parameters", Justification = "A positional record of the columns one halt keeps: both sides' versions and fingerprints are separate fields an operator reads.")]
public sealed record IntegrityPairHaltRecord(
  string Peer, IntegrityPairSide Side, RulePairHaltReason Reason, IReadOnlyList<string> DifferingTypes,
  int LocalSemanticsVersion, int LocalEligibilitySqlVersion, string LocalFingerprint,
  int? PeerSemanticsVersion, int? PeerEligibilitySqlVersion, string? PeerFingerprint,
  DateTimeOffset HaltedAt, DateTimeOffset? AlertedAt);

/// <summary>
/// The pair guard (#2362): integrity runs for a service pair only while both apply the same eligibility rules to the
/// event types they share. Every integrity receptor asks it before comparing, repairing or redelivering; on a mismatch
/// the pair is halted, with an error naming both sides' versions and the types that differ, a halted-pair metric, and an
/// alert once it stays halted past <see cref="IntegrityPairGuardOptions.HaltedAlertAfter"/>. The pair resumes on its own
/// with the first message whose rules match.
/// </summary>
/// <remarks>
/// <para>
/// A peer that sends no fingerprint runs a release that predates it and may compute digests another way, so it is a
/// mismatch: a rolling deploy halts a mixed pair instead of repairing losses that are not real.
/// </para>
/// <para>
/// #2377: a halt survives a restart. <see cref="AdmitAsync"/> writes a halt, its alert and its resumption to the store as
/// they happen, and a new process loads the open halts once before its first verdict, so the halted-at time (and with it
/// the alert clock) carries on where it was and the history of stops and resumes is kept. The store keeps history; it
/// never decides a verdict, so a store that cannot be read or written is logged and the guard goes on in memory.
/// </para>
/// </remarks>
/// <docs>resilience/stream-integrity#the-pair-guard</docs>
/// <tests>tests/Whizbang.Core.Unit.Messaging.Tests/Messaging/IntegrityPairGuardTests.cs</tests>
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
  private readonly object _hydrateLock = new();
  private bool _hydrated;
  private Task? _hydrating;

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
  /// threshold, and an information line when it resumes. #2377: with a <paramref name="store"/>, the open halts it kept
  /// are loaded first (once per process), and a halt, its alert and its resumption are written to it as they happen.
  /// </summary>
  /// <param name="store">The service's store; null keeps the state in memory only.</param>
  /// <param name="peer">The other service's name.</param>
  /// <param name="side">Which end this service is.</param>
  /// <param name="peerRules">The fingerprint the peer's message carried, or null when it carried none.</param>
  /// <param name="cancellationToken">Cancellation token.</param>
  /// <returns>The verdict: integrity runs for the message only when it agrees.</returns>
  /// <docs>resilience/stream-integrity#the-pair-guard</docs>
  /// <tests>tests/Whizbang.Core.Unit.Messaging.Tests/Messaging/IntegrityPairGuardDurabilityTests.cs</tests>
  /// <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/TwoServicePairGuardTests.cs:ARestartDuringAHalt_KeepsTheHaltedAt_TheAlertFiresOnSchedule_AndTheResumptionIsKeptAsync</tests>
  public async Task<RulePairVerdict> AdmitAsync(
      IWorkCoordinator? store, string peer, IntegrityPairSide side, RuleFingerprint? peerRules,
      CancellationToken cancellationToken = default) {
    if (store is null) {
      return Admit(peer, side, peerRules);
    }
    await _ensureHydratedAsync(store, cancellationToken).ConfigureAwait(false);
    var verdict = _admit(peer, side, peerRules, out var change);
    await _keepAsync(store, change, cancellationToken).ConfigureAwait(false);
    return verdict;
  }

  /// <summary>
  /// The verdict, with the pair's state kept in memory only (<see cref="AdmitAsync"/> without a store).
  /// </summary>
  /// <param name="peer">The other service's name.</param>
  /// <param name="side">Which end this service is.</param>
  /// <param name="peerRules">The fingerprint the peer's message carried, or null when it carried none.</param>
  /// <returns>The verdict.</returns>
  internal RulePairVerdict Admit(string peer, IntegrityPairSide side, RuleFingerprint? peerRules) =>
    _admit(peer, side, peerRules, out _);

  /// <summary>What one verdict changed, for the store (#2377).</summary>
  private readonly record struct PairChange(
    IntegrityPairHaltRecord? Halted, (string Peer, IntegrityPairSide Side, DateTimeOffset At)? Alerted,
    (string Peer, IntegrityPairSide Side, DateTimeOffset At)? Resumed);

  private RulePairVerdict _admit(string peer, IntegrityPairSide side, RuleFingerprint? peerRules, out PairChange change) {
    ArgumentNullException.ThrowIfNull(peer);
    change = default;
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
        change = new PairChange(null, null, (peer, side, now));
      }
      return verdict;
    }

    var sameHalt = previous is { HaltedSince: not null } && previous.Verdict.Reason == verdict.Reason
      && previous.Verdict.DifferingTypes.SequenceEqual(verdict.DifferingTypes, StringComparer.Ordinal);
    var haltedSince = previous?.HaltedSince ?? now;
    var alerted = previous?.Alerted ?? false;
    var reason = ReasonTag(verdict.Reason);
    var sideTag = _sideTag(side);
    IntegrityPairHaltRecord? halted = null;
    if (!sameHalt) {
      _halts?.Add(1, new KeyValuePair<string, object?>("reason", reason), new KeyValuePair<string, object?>("side", sideTag));
      var localFingerprint = mine.Fingerprint();
      var differing = string.Join(", ", verdict.DifferingTypes);
      LogPairHalted(_logger, peer, side, reason, mine.SemanticsVersion, mine.EligibilitySqlVersion, localFingerprint,
        theirs?.SemanticsVersion, theirs?.EligibilitySqlVersion, peerRules?.Fingerprint, differing);
      halted = new IntegrityPairHaltRecord(peer, side, verdict.Reason, verdict.DifferingTypes,
        mine.SemanticsVersion, mine.EligibilitySqlVersion, localFingerprint,
        theirs?.SemanticsVersion, theirs?.EligibilitySqlVersion, peerRules?.Fingerprint, haltedSince, null);
    }
    var haltedFor = now - haltedSince;
    (string, IntegrityPairSide, DateTimeOffset)? alertedNow = null;
    if (!alerted && haltedFor >= _options.HaltedAlertAfter) {
      alerted = true;
      alertedNow = (peer, side, now);
      _alerts?.Add(1, new KeyValuePair<string, object?>("side", sideTag));
      LogPairHaltAlert(_logger, peer, side, reason, haltedFor, _options.HaltedAlertAfter);
    }
    _pairs[key] = new IntegrityPairState(peer, side, verdict, haltedSince, alerted);
    change = new PairChange(halted, alertedNow, null);
    return verdict;
  }

  // #2377: loads the open halts the store kept, once per process, before the first verdict. What this process already
  // learned wins over the kept copy. A load that fails is logged and leaves the guard unloaded, so the next verdict tries
  // again; the store keeps history and never decides a verdict. A canceled load is a stopping host and propagates.
  private async Task _ensureHydratedAsync(IWorkCoordinator store, CancellationToken cancellationToken) {
    Task loading;
    lock (_hydrateLock) {
      if (_hydrated) {
        return;
      }
      loading = _hydrating ??= _loadAsync(store, cancellationToken);
    }
    try {
      await loading.ConfigureAwait(false);
    } catch (OperationCanceledException) {
      _unload();
      throw;
    } catch (Exception ex) {
      _unload();
      LogPairHaltsNotLoaded(_logger, ex);
    }
  }

  private void _unload() {
    lock (_hydrateLock) {
      _hydrating = null;
    }
  }

  private async Task _loadAsync(IWorkCoordinator store, CancellationToken cancellationToken) {
    foreach (var halt in await store.IntegrityLoadPairHaltsAsync(cancellationToken).ConfigureAwait(false) ?? []) {
      _pairs.TryAdd((halt.Peer, halt.Side), new IntegrityPairState(halt.Peer, halt.Side,
        new RulePairVerdict(halt.Reason, halt.DifferingTypes), halt.HaltedAt, halt.AlertedAt is not null));
    }
    lock (_hydrateLock) {
      _hydrated = true;
    }
  }

  // #2377: writes what the verdict changed. A halt comes back as the store keeps it: another instance of this service may
  // have halted the pair earlier, or alerted it, and one service has one halt, so the earlier time and the alert are
  // taken up. A failed write is logged; the verdict stands.
  private async Task _keepAsync(IWorkCoordinator store, PairChange change, CancellationToken cancellationToken) {
    try {
      if (change.Halted is { } halt && await store.IntegrityRecordPairHaltAsync(halt, cancellationToken).ConfigureAwait(false) is { } kept) {
        _takeUp(kept);
      }
      if (change.Alerted is { } alert) {
        await store.IntegrityRecordPairHaltAlertAsync(alert.Peer, alert.Side, alert.At, cancellationToken).ConfigureAwait(false);
      }
      if (change.Resumed is { } resumed) {
        await store.IntegrityRecordPairResumedAsync(resumed.Peer, resumed.Side, resumed.At, cancellationToken).ConfigureAwait(false);
      }
    } catch (OperationCanceledException) {
      throw;
    } catch (Exception ex) {
      LogPairStateNotKept(_logger, ex);
    }
  }

  private void _takeUp(IntegrityPairHaltRecord kept) {
    var key = (kept.Peer, kept.Side);
    if (_pairs.TryGetValue(key, out var current) && current.HaltedSince is { } since) {
      _pairs[key] = current with {
        HaltedSince = kept.HaltedAt < since ? kept.HaltedAt : since,
        Alerted = current.Alerted || kept.AlertedAt is not null,
      };
    }
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
  [SuppressMessage("Major Code Smell", "S107:Methods should not have too many parameters", Justification = "LoggerMessage source-generated method: the parameters are the structured log template's placeholders, both sides' versions and fingerprints, and grouping them would lose them as separate structured fields.")]
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

  [LoggerMessage(EventId = 4, Level = LogLevel.Warning,
    Message = "The pair guard could not load the halts kept before this process started; verdicts go on, and a halt that "
            + "began before the restart is timed from when it is next seen until the load succeeds")]
  static partial void LogPairHaltsNotLoaded(ILogger logger, Exception exception);

  [LoggerMessage(EventId = 5, Level = LogLevel.Warning,
    Message = "The pair guard could not keep a pair's halt, alert or resumption in the store; the verdict stands, and the "
            + "halt history misses this change")]
  static partial void LogPairStateNotKept(ILogger logger, Exception exception);
}
