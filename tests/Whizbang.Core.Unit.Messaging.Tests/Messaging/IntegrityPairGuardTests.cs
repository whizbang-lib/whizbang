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

  private static RuleFingerprint _peerOnRules(int semanticsVersion, int eligibilitySqlVersion) =>
    RuleFingerprint.From(new RuleSet(semanticsVersion, eligibilitySqlVersion,
      new Dictionary<string, string>(StringComparer.Ordinal) { [SHARED] = "a" }));

  /// <summary>
  /// #2588 (maintainer's decision on D2): all instances of one service deploy together, different services may not, so
  /// two services on different rules versions are an expected state until both upgrade. The pair still compares nothing
  /// (it is paused, so no false mismatch), but the pause is a Warning with its own metric, never the Error and the
  /// Critical alert of a real halt; it ends with the first message from the upgraded peer.
  /// </summary>
  [Test]
  [Arguments(0, -1)]
  [Arguments(-1, 0)]
  public async Task Admit_APeerOnAnotherRulesVersion_IsPausedWithAWarning_NotHaltedAsync(int semanticsDelta, int sqlDelta) {
    var (guard, log, _, services) = _guard(new Rules((SHARED, "a")));
    await using var owned = services;
    var factory = services.GetRequiredService<IMeterFactory>();
    using var pauses = new Recorder<long>(factory, "whizbang.stream_integrity.pair_rules_version_pauses");
    using var halts = new Recorder<long>(factory, "whizbang.stream_integrity.pair_halts");
    var olderPeer = _peerOnRules(RuleSemantics.VERSION + semanticsDelta, RuleSemantics.ELIGIBILITY_SQL_VERSION + sqlDelta);

    var paused = guard.Admit(PEER, IntegrityPairSide.Consumer, olderPeer);
    _ = guard.Admit(PEER, IntegrityPairSide.Consumer, olderPeer);
    var resumed = guard.Admit(PEER, IntegrityPairSide.Consumer, _peer((SHARED, "a")));

    await Assert.That(paused.IsAgreed).IsFalse().Because("a pair on two rules versions compares nothing, so nothing is falsely repaired");
    await Assert.That(IntegrityPairGuard.IsRulesVersionPause(paused.Reason)).IsTrue();
    await Assert.That(resumed.IsAgreed).IsTrue().Because("the pair resumes with the first message from the upgraded peer");
    await Assert.That(log.Entries.Any(e => e.Level >= LogLevel.Error)).IsFalse()
      .Because("an expected state between services that deploy separately is not an error");
    var warning = log.Entries.Single(e => e.Level == LogLevel.Warning).Message;
    await Assert.That(warning).Contains("paused").And.Contains("rules version")
      .And.Contains($"eligibility SQL {RuleSemantics.ELIGIBILITY_SQL_VERSION}")
      .Because("the operator sees which side runs which rules version, once per pause");
    await Assert.That(log.Entries[^1].Level).IsEqualTo(LogLevel.Information).Because("the resumption is logged");
    var counted = pauses.GetMeasurementSnapshot();
    await Assert.That(counted.Count).IsEqualTo(1);
    await Assert.That(counted[0].Tags["side"]).IsEqualTo("consumer");
    await Assert.That(halts.GetMeasurementSnapshot()).IsEmpty().Because("a pause is not counted as a halt");
  }

  [Test]
  public async Task Admit_APausePastTheThreshold_WarnsOnce_AndNeverRaisesTheCriticalHaltAlertAsync() {
    var (guard, log, time, services) = _guard(new Rules((SHARED, "a")), TimeSpan.FromMinutes(30));
    await using var owned = services;
    var factory = services.GetRequiredService<IMeterFactory>();
    using var pauseAlerts = new Recorder<long>(factory, "whizbang.stream_integrity.pair_rules_version_pause_alerts");
    using var haltAlerts = new Recorder<long>(factory, "whizbang.stream_integrity.pair_halt_alerts");
    var olderPeer = _peerOnRules(RuleSemantics.VERSION, RuleSemantics.ELIGIBILITY_SQL_VERSION - 1);
    _ = guard.Admit(PEER, IntegrityPairSide.Origin, olderPeer);

    time.Advance(TimeSpan.FromMinutes(30));
    _ = guard.Admit(PEER, IntegrityPairSide.Origin, olderPeer);
    _ = guard.Admit(PEER, IntegrityPairSide.Origin, olderPeer);

    await Assert.That(log.Entries.Any(e => e.Level == LogLevel.Critical)).IsFalse()
      .Because("a rules-version pause is expected until both services upgrade, however long that takes");
    await Assert.That(log.Entries.Count(e => e.Level == LogLevel.Warning && e.Message.Contains("still paused", StringComparison.Ordinal)))
      .IsEqualTo(1).Because("a long pause is worth one reminder, at Warning");
    await Assert.That(pauseAlerts.GetMeasurementSnapshot().Count).IsEqualTo(1);
    await Assert.That(haltAlerts.GetMeasurementSnapshot()).IsEmpty();
    await Assert.That(guard.Pairs.Single().Alerted).IsTrue();
  }

  [Test]
  public async Task Admit_APauseOnAGuardWithoutMetrics_StillWarnsAndAlertsOnceAsync() {
    var (guard, log, time, services) = _guard(new Rules((SHARED, "a")), TimeSpan.FromMinutes(30), withMetrics: false);
    await using var owned = services;
    var olderPeer = _peerOnRules(RuleSemantics.VERSION, RuleSemantics.ELIGIBILITY_SQL_VERSION - 1);
    var paused = guard.Admit(PEER, IntegrityPairSide.Origin, olderPeer);

    time.Advance(TimeSpan.FromMinutes(30));
    _ = guard.Admit(PEER, IntegrityPairSide.Origin, olderPeer);

    await Assert.That(IntegrityPairGuard.IsRulesVersionPause(paused.Reason)).IsTrue();
    await Assert.That(log.Entries.Count(e => e.Level == LogLevel.Warning)).IsEqualTo(2)
      .Because("a host without metrics still gets the pause and the one reminder, both at Warning");
    await Assert.That(log.Entries.Any(e => e.Level == LogLevel.Critical)).IsFalse();
    await Assert.That(guard.Pairs.Single().Alerted).IsTrue();
  }

  [Test]
  public async Task TheStateGauge_TellsAPauseFromAHaltAsync() {
    var (guard, _, _, services) = _guard(new Rules((SHARED, "a")));
    await using var owned = services;
    using var state = new Recorder<long>(services.GetRequiredService<IMeterFactory>(), "whizbang.stream_integrity.pair_state");
    _ = guard.Admit("paused-peer", IntegrityPairSide.Consumer, _peerOnRules(RuleSemantics.VERSION, RuleSemantics.ELIGIBILITY_SQL_VERSION - 1));
    _ = guard.Admit("halted-peer", IntegrityPairSide.Consumer, _peer((SHARED, "b")));

    state.RecordObservableInstruments();

    var states = state.GetMeasurementSnapshot().ToDictionary(m => (string)m.Tags["peer"]!, m => (string)m.Tags["state"]!);
    await Assert.That(states["paused-peer"]).IsEqualTo(IntegrityPairGuard.STATE_PAUSED);
    await Assert.That(states["halted-peer"]).IsEqualTo(IntegrityPairGuard.STATE_HALTED);
  }

  /// <summary>
  /// #2588: the pair keys on the rules library's own version track, never the package version. All a message carries
  /// of its sender's rules is the rules versions, the fingerprint and each type's rule hash, so two services on
  /// different package versions that run the same rules keep comparing: an ordinary upgrade pauses nothing.
  /// </summary>
  [Test]
  public async Task TwoServicesOnDifferentPackagesWithTheSameRules_KeepComparing_AndTheWireCarriesNoPackageVersionAsync() {
    var (guard, log, _, services) = _guard(new Rules((SHARED, "a")));
    await using var owned = services;
    var samePeerOnAnotherPackage = _peerOnRules(RuleSemantics.VERSION, RuleSemantics.ELIGIBILITY_SQL_VERSION);

    var verdict = guard.Admit(PEER, IntegrityPairSide.Consumer, samePeerOnAnotherPackage);
    var wire = System.Text.Json.JsonSerializer.Serialize(samePeerOnAnotherPackage,
      Whizbang.Core.Serialization.JsonContextRegistry.CreateCombinedOptions().GetTypeInfo(typeof(RuleFingerprint)));
    var fields = System.Text.Json.JsonElement.Parse(wire).EnumerateObject().Select(p => p.Name.ToUpperInvariant()).Order().ToList();

    await Assert.That(verdict.IsAgreed).IsTrue();
    await Assert.That(log.Entries).IsEmpty();
    await Assert.That(fields).IsEquivalentTo(_ruleFingerprintFields)
      .Because("a package version on the wire would pause every ordinary upgrade; the rules versions are what is compared");
  }

  private static readonly string[] _ruleFingerprintFields = ["ELIGIBILITYSQLVERSION", "FINGERPRINT", "SEMANTICSVERSION", "TYPES"];

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
