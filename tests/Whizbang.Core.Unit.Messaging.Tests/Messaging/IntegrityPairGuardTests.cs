// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using System.Diagnostics.Metrics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using TUnit.Core;
using Whizbang.Core.Messaging;
using Whizbang.Core.Observability;
using Whizbang.Core.Workers;
using Whizbang.Rules;

namespace Whizbang.Core.Tests.Messaging;

/// <summary>
/// #2362: the pair guard. It admits a peer whose rules match over the shared types, halts one whose rules differ or who
/// sent none, reports the halt once (log and metric), alerts once the halt passes the threshold, and resumes on its own.
/// </summary>
[Category("Messaging")]
public class IntegrityPairGuardTests {
  private const string PEER = "orders-svc";
  private const string SHARED = "Contracts.OrderPlaced";

  private sealed class Rules(params (string Type, string Hash)[] types) : IRuleFingerprintSource {
    public RuleSet RulesFor(IEnumerable<string>? eventTypes) {
      var all = types.ToDictionary(t => t.Type, t => t.Hash, StringComparer.Ordinal);
      var wanted = eventTypes is null ? all.Keys.ToHashSet(StringComparer.Ordinal) : eventTypes.Select(RuleTypeName.Of).ToHashSet(StringComparer.Ordinal);
      return new RuleSet(RuleSemantics.VERSION, RuleSemantics.ELIGIBILITY_SQL_VERSION,
        all.Where(t => wanted.Contains(t.Key)).ToDictionary(t => t.Key, t => t.Value, StringComparer.Ordinal));
    }
  }

  private sealed class CapturingLog : ILogger<IntegrityPairGuard> {
    public List<(LogLevel Level, string Message)> Entries { get; } = [];
    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
    public bool IsEnabled(LogLevel logLevel) => true;
    public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
      Entries.Add((logLevel, formatter(state, exception)));
  }

  /// <summary>Records one instrument of the meters one factory created, so other tests' meters never reach it.</summary>
  private sealed class Recorder<T> : IDisposable where T : struct {
    private readonly MeterListener _listener = new();
    private readonly List<(T Value, Dictionary<string, object?> Tags)> _seen = [];

    public Recorder(IMeterFactory factory, string instrumentName) {
      _listener.InstrumentPublished = (instrument, l) => {
        if (ReferenceEquals(instrument.Meter.Scope, factory) && instrument.Name == instrumentName) {
          l.EnableMeasurementEvents(instrument);
        }
      };
      _listener.SetMeasurementEventCallback<T>((_, value, tags, _) => {
        var map = new Dictionary<string, object?>(StringComparer.Ordinal);
        foreach (var tag in tags) {
          map[tag.Key] = tag.Value;
        }
        lock (_seen) {
          _seen.Add((value, map));
        }
      });
      _listener.Start();
    }

    public void RecordObservableInstruments() => _listener.RecordObservableInstruments();

    public IReadOnlyList<(T Value, Dictionary<string, object?> Tags)> GetMeasurementSnapshot() {
      lock (_seen) {
        return [.. _seen];
      }
    }

    public void Dispose() => _listener.Dispose();
  }

  private static (IntegrityPairGuard Guard, CapturingLog Log, FakeTimeProvider Time, ServiceProvider Services) _guard(
      IRuleFingerprintSource rules, TimeSpan? alertAfter = null, bool withMetrics = true) {
    var services = new ServiceCollection().AddMetrics().AddSingleton<WhizbangMetrics>().BuildServiceProvider();
    var options = new StreamIntegrityOptions();
    if (alertAfter is { } after) {
      options.PairGuard.HaltedAlertAfter = after;
    }
    var log = new CapturingLog();
    var time = new FakeTimeProvider(new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero));
    var metrics = withMetrics ? services.GetRequiredService<WhizbangMetrics>() : null;
    return (new IntegrityPairGuard(rules, Options.Create(options), log, time, metrics),
      log, time, services);
  }

  private static RuleFingerprint _peer(params (string Type, string Hash)[] types) =>
    RuleFingerprint.From(new Rules(types).RulesFor(null));

  [Test]
  public async Task Options_AlertAfterTwoHoursByDefaultAsync() {
    await Assert.That(new StreamIntegrityOptions().PairGuard.HaltedAlertAfter).IsEqualTo(TimeSpan.FromHours(2));
  }

  [Test]
  public async Task Admit_MatchingRules_AgreesAndLogsNothingAsync() {
    var (guard, log, _, services) = _guard(new Rules((SHARED, "a")));
    await using var owned = services;

    var verdict = guard.Admit(PEER, IntegrityPairSide.Consumer, _peer((SHARED, "a"), ("Contracts.TheirsOnly", "z")));

    await Assert.That(verdict.IsAgreed).IsTrue();
    await Assert.That(log.Entries).IsEmpty();
    await Assert.That(guard.Pairs.Single().HaltedSince).IsNull();
  }

  /// <summary>
  /// #2588 (decision D2): version 2 of the eligibility rule leaves out an operator's exclusions, so a peer still on version
  /// 1 halts the pair until both run version 2. Services deploy independently, so this pause can last days: it is reported
  /// once as an Error naming both versions, counted under the <c>eligibility_sql_version</c> reason, and ends on its own
  /// with the first message from the upgraded peer.
  /// </summary>
  [Test]
  public async Task Admit_APeerOnTheEligibilityRuleBeforeExclusions_HaltsUntilItUpgrades_ThenResumesAsync() {
    var (guard, log, _, services) = _guard(new Rules((SHARED, "a")));
    await using var owned = services;
    using var collector = new Recorder<long>(services.GetRequiredService<IMeterFactory>(), "whizbang.stream_integrity.pair_halts");
    var olderPeer = RuleFingerprint.From(new RuleSet(RuleSemantics.VERSION, RuleSemantics.ELIGIBILITY_SQL_VERSION - 1,
      new Dictionary<string, string>(StringComparer.Ordinal) { [SHARED] = "a" }));

    var halted = guard.Admit(PEER, IntegrityPairSide.Consumer, olderPeer);
    var resumed = guard.Admit(PEER, IntegrityPairSide.Consumer, _peer((SHARED, "a")));

    await Assert.That(halted.Reason).IsEqualTo(RulePairHaltReason.EligibilitySqlVersion);
    await Assert.That(resumed.IsAgreed).IsTrue().Because("the pair resumes with the first message from the upgraded peer");
    var error = log.Entries.Single(e => e.Level == LogLevel.Error).Message;
    await Assert.That(error).Contains("eligibility_sql_version")
      .And.Contains($"eligibility SQL {RuleSemantics.ELIGIBILITY_SQL_VERSION}")
      .Because("the operator sees which side runs which rule version");
    await Assert.That(log.Entries.Any(e => e.Level == LogLevel.Information)).IsTrue().Because("the resumption is logged");
    await Assert.That(collector.GetMeasurementSnapshot().Single().Tags["reason"]).IsEqualTo("eligibility_sql_version");
  }

  [Test]
  public async Task Admit_ADifferentSharedType_HaltsWithOneErrorNamingBothSidesAndTheTypeAsync() {
    var (guard, log, _, services) = _guard(new Rules((SHARED, "a")));
    await using var owned = services;
    using var collector = new Recorder<long>(services.GetRequiredService<IMeterFactory>(),
      "whizbang.stream_integrity.pair_halts");
    var peer = _peer((SHARED, "b"));

    var verdict = guard.Admit(PEER, IntegrityPairSide.Origin, peer);
    _ = guard.Admit(PEER, IntegrityPairSide.Origin, peer);

    await Assert.That(verdict.Reason).IsEqualTo(RulePairHaltReason.TypeRules);
    await Assert.That(verdict.DifferingTypes).IsEquivalentTo([SHARED]);
    var (level, message) = log.Entries.Single();
    await Assert.That(level).IsEqualTo(LogLevel.Error).Because("a halt is reported once, not per message");
    await Assert.That(message).Contains(PEER).And.Contains(SHARED).And.Contains("type_rules")
      .And.Contains(peer.Fingerprint).And.Contains(guard.Stamp([SHARED]).Fingerprint);
    var halts = collector.GetMeasurementSnapshot();
    await Assert.That(halts.Count).IsEqualTo(1);
    await Assert.That(halts[0].Tags["reason"]).IsEqualTo("type_rules");
    await Assert.That(halts[0].Tags["side"]).IsEqualTo("origin");
  }

  [Test]
  public async Task Admit_NoFingerprint_IsHalted_AndAnotherReasonIsReportedAgainAsync() {
    var (guard, log, _, services) = _guard(new Rules((SHARED, "a")));
    await using var owned = services;

    var verdict = guard.Admit(PEER, IntegrityPairSide.Consumer, null);
    _ = guard.Admit(PEER, IntegrityPairSide.Consumer, _peer((SHARED, "b")));

    await Assert.That(verdict.Reason).IsEqualTo(RulePairHaltReason.NoFingerprint);
    await Assert.That(log.Entries.Count(e => e.Level == LogLevel.Error)).IsEqualTo(2)
      .Because("a halt for another reason is news");
    await Assert.That(log.Entries[0].Message).Contains("no_fingerprint");
  }

  [Test]
  public async Task Admit_StillHaltedPastTheThreshold_AlertsOnce_ThenResumesOnItsOwnAsync() {
    var (guard, log, time, services) = _guard(new Rules((SHARED, "a")), TimeSpan.FromMinutes(30));
    await using var owned = services;
    using var alerts = new Recorder<long>(services.GetRequiredService<IMeterFactory>(),
      "whizbang.stream_integrity.pair_halt_alerts");
    var halted = guard.Admit(PEER, IntegrityPairSide.Consumer, _peer((SHARED, "b")));
    time.Advance(TimeSpan.FromMinutes(29));
    _ = guard.Admit(PEER, IntegrityPairSide.Consumer, _peer((SHARED, "b")));
    await Assert.That(log.Entries.Any(e => e.Level == LogLevel.Critical)).IsFalse().Because("not yet past the threshold");

    time.Advance(TimeSpan.FromMinutes(1));
    _ = guard.Admit(PEER, IntegrityPairSide.Consumer, _peer((SHARED, "b")));
    _ = guard.Admit(PEER, IntegrityPairSide.Consumer, _peer((SHARED, "b")));
    var resumed = guard.Admit(PEER, IntegrityPairSide.Consumer, _peer((SHARED, "a")));

    await Assert.That(halted.IsAgreed).IsFalse();
    await Assert.That(log.Entries.Count(e => e.Level == LogLevel.Critical)).IsEqualTo(1);
    await Assert.That(alerts.GetMeasurementSnapshot().Count).IsEqualTo(1);
    await Assert.That(resumed.IsAgreed).IsTrue();
    await Assert.That(log.Entries[^1].Level).IsEqualTo(LogLevel.Information);
    await Assert.That(log.Entries[^1].Message).Contains("resumed");
    await Assert.That(guard.Pairs.Single().HaltedSince).IsNull();
  }

  [Test]
  public async Task Admit_StillHaltedPastTheThreshold_OnAHostWithoutMetrics_StillAlertsOnceInTheLogAsync() {
    var (guard, log, time, services) = _guard(new Rules((SHARED, "a")), TimeSpan.FromMinutes(30), withMetrics: false);
    await using var owned = services;
    using var alerts = new Recorder<long>(services.GetRequiredService<IMeterFactory>(),
      "whizbang.stream_integrity.pair_halt_alerts");
    _ = guard.Admit(PEER, IntegrityPairSide.Origin, _peer((SHARED, "b")));

    time.Advance(TimeSpan.FromMinutes(30));
    _ = guard.Admit(PEER, IntegrityPairSide.Origin, _peer((SHARED, "b")));
    _ = guard.Admit(PEER, IntegrityPairSide.Origin, _peer((SHARED, "b")));

    var critical = log.Entries.Where(e => e.Level == LogLevel.Critical).ToList();
    await Assert.That(critical.Count).IsEqualTo(1).Because("the alert does not depend on a meter being configured");
    await Assert.That(critical[0].Message).Contains(PEER);
    await Assert.That(alerts.GetMeasurementSnapshot()).IsEmpty().Because("a guard built without metrics records none");
    await Assert.That(guard.Pairs.Single().Alerted).IsTrue();
  }

  [Test]
  public async Task Gauges_ReportEachPairsState_HaltedAge_AndTheServiceFingerprintAsync() {
    var (guard, _, time, services) = _guard(new Rules((SHARED, "a")));
    await using var owned = services;
    var factory = services.GetRequiredService<IMeterFactory>();
    using var state = new Recorder<long>(factory, "whizbang.stream_integrity.pair_state");
    using var age = new Recorder<double>(factory, "whizbang.stream_integrity.pair_halted_seconds");
    using var info = new Recorder<long>(factory, "whizbang.stream_integrity.rule_fingerprint_info");
    _ = guard.Admit("halted-peer", IntegrityPairSide.Consumer, null);
    _ = guard.Admit("agreed-peer", IntegrityPairSide.Origin, _peer((SHARED, "a")));
    time.Advance(TimeSpan.FromSeconds(90));

    state.RecordObservableInstruments();
    age.RecordObservableInstruments();
    info.RecordObservableInstruments();

    var states = state.GetMeasurementSnapshot().ToDictionary(m => (string)m.Tags["peer"]!, m => (string)m.Tags["state"]!);
    await Assert.That(states["halted-peer"]).IsEqualTo(IntegrityPairGuard.STATE_HALTED);
    await Assert.That(states["agreed-peer"]).IsEqualTo(IntegrityPairGuard.STATE_AGREED);
    var ages = age.GetMeasurementSnapshot();
    await Assert.That(ages.Count).IsEqualTo(1);
    await Assert.That(ages[0].Value).IsEqualTo(90d);
    await Assert.That(info.GetMeasurementSnapshot().Single().Tags["fingerprint"]).IsEqualTo(guard.ServiceFingerprint);
  }

  [Test]
  public async Task Resolve_FallsBackToAGuardThatComparesVersions_AndReasonTagsNameEveryReasonAsync() {
    await using var empty = new ServiceCollection().BuildServiceProvider();
    var (guard, _, _, services) = _guard(new Rules());
    await using var owned = services;
    await using var registered = new ServiceCollection().AddSingleton(guard).BuildServiceProvider();

    await Assert.That(IntegrityPairGuard.Resolve(empty)).IsSameReferenceAs(IntegrityPairGuard.Fallback);
    await Assert.That(IntegrityPairGuard.Resolve(registered)).IsSameReferenceAs(guard);
    await Assert.That(IntegrityPairGuard.Fallback.Admit(PEER, IntegrityPairSide.Origin, IntegrityPairGuard.Fallback.Stamp([SHARED])).IsAgreed).IsTrue();
    await Assert.That(Enum.GetValues<RulePairHaltReason>().Select(IntegrityPairGuard.ReasonTag))
      .IsEquivalentTo(["none", "no_fingerprint", "semantics_version", "eligibility_sql_version", "type_rules"]);
    await Assert.That(() => new IntegrityPairGuard(new Rules(), null!, NullLogger<IntegrityPairGuard>.Instance)).Throws<ArgumentNullException>();
    await Assert.That(() => guard.Admit(null!, IntegrityPairSide.Origin, null)).Throws<ArgumentNullException>();
  }

  [Test]
  public async Task Admit_HaltedAgain_WithOtherDifferingTypes_IsReportedAgainAsync() {
    var (guard, log, _, services) = _guard(new Rules((SHARED, "a"), ("Contracts.Other", "x")));
    await using var owned = services;

    _ = guard.Admit(PEER, IntegrityPairSide.Consumer, _peer((SHARED, "b"), ("Contracts.Other", "x")));
    _ = guard.Admit(PEER, IntegrityPairSide.Consumer, _peer((SHARED, "b"), ("Contracts.Other", "y")));

    await Assert.That(log.Entries.Count(e => e.Level == LogLevel.Error)).IsEqualTo(2)
      .Because("the same reason with another set of differing types is news");
    await Assert.That(guard.Pairs.Single().Verdict.DifferingTypes).IsEquivalentTo(["Contracts.Other", SHARED]);
  }

  /// <summary>#2362: the turnkey worker registration gives every service its catalog rules and a guard over them.</summary>
  [Test]
  public async Task TheWorkerRegistration_ProvidesTheRuleSourceAndTheGuardAsync() {
    var services = new ServiceCollection();
    services.TryAddWhizbangDefaults();
    services.AddSingleton<Microsoft.Extensions.Configuration.IConfiguration>(
      new Microsoft.Extensions.Configuration.ConfigurationBuilder().Build());
    services.AddWhizbangWorkers();
    await using var provider = services.BuildServiceProvider();

    await Assert.That(provider.GetRequiredService<IRuleFingerprintSource>()).IsTypeOf<CatalogRuleFingerprintSource>();
    var guard = provider.GetRequiredService<IntegrityPairGuard>();
    await Assert.That(IntegrityPairGuard.Resolve(provider)).IsSameReferenceAs(guard);
    await Assert.That(guard.Stamp(null).SemanticsVersion).IsEqualTo(RuleSemantics.VERSION);
  }

  /// <summary>Without a catalog registered at all, the source knows no type and still stamps both versions.</summary>
  [Test]
  public async Task TheWorkerRegistration_WithoutACatalog_StampsVersionsOnlyAsync() {
    var services = new ServiceCollection();
    services.AddLogging();
    services.AddSingleton<Microsoft.Extensions.Configuration.IConfiguration>(
      new Microsoft.Extensions.Configuration.ConfigurationBuilder().Build());
    services.AddWhizbangWorkers();
    services.RemoveAll<IMessageTypeCatalog>();
    await using var provider = services.BuildServiceProvider();

    await Assert.That(provider.GetRequiredService<IRuleFingerprintSource>().RulesFor(null).TypeHashes).IsEmpty();
  }

  [Test]
  public async Task AGuardWithoutMetrics_StillGuardsAsync() {
    var guard = new IntegrityPairGuard(new Rules((SHARED, "a")), Options.Create(new StreamIntegrityOptions()),
      NullLogger<IntegrityPairGuard>.Instance);

    await Assert.That(guard.Admit(PEER, IntegrityPairSide.Origin, null).IsAgreed).IsFalse();
    await Assert.That(guard.Admit(PEER, IntegrityPairSide.Origin, _peer((SHARED, "a"))).IsAgreed).IsTrue();
  }
}
