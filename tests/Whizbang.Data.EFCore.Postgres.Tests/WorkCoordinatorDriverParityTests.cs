// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Messaging;
using Whizbang.Data.Dapper.Postgres;

namespace Whizbang.Data.EFCore.Postgres.Tests;

/// <summary>
/// Every <see cref="IWorkCoordinator"/> member with a default body is either implemented by the Dapper coordinator or
/// named on <see cref="DapperAllowList"/> with the reason it is not, so no member is left at the interface's inert
/// default on Dapper without a decision (#2051).
/// </summary>
/// <remarks>
/// <para>
/// The interface defaults are inert answers for engines without the primitive: <c>unsupported</c>, zero, an empty list.
/// The Dapper coordinator inherited 85 of them silently, so a retention setting such as the pointer prune was accepted on
/// that driver and removed nothing, and integrity repair was inert. A silent no-op is the one outcome not allowed: a
/// member is implemented with the EF Core contract, or allow-listed here with its reason, and a feature that depends on
/// an allow-listed member is refused at startup on a Dapper host when its configuration enables it.
/// </para>
/// <para>
/// The member list is read from the interface by reflection, never written by hand, so a member added to the interface
/// with a default body fails this test until someone decides how Dapper serves it. The allow-list also may not hold a
/// member Dapper implements, so it cannot go stale. Design choice documented at data/drivers#dapper-coordinator-parity.
/// </para>
/// </remarks>
/// <docs>data/drivers#dapper-coordinator-parity</docs>
/// <code-under-test>src/Whizbang.Data.Dapper.Postgres/DapperWorkCoordinator.cs</code-under-test>
/// <code-under-test>src/Whizbang.Data.EFCore.Postgres/EFCoreWorkCoordinator.cs</code-under-test>
[Category("Shard2")]
public class WorkCoordinatorDriverParityTests {
  /// <summary>
  /// The default-bodied members the Dapper coordinator deliberately serves at the interface default, each with the reason.
  /// Every entry is a decision; a feature that depends on one is refused at startup on Dapper when it is enabled.
  /// </summary>
  internal static readonly IReadOnlyDictionary<string, string> DapperAllowList = new Dictionary<string, string>(StringComparer.Ordinal) {
    [nameof(IWorkCoordinator.AdoptRedeliveredOriginAsync)] = INTEGRITY,
    [nameof(IWorkCoordinator.ReconcileRedeliveredBundlesAsync)] = INTEGRITY,
    [nameof(IWorkCoordinator.GetIntegrityDomainClaimsAsync)] = INTEGRITY,
    [nameof(IWorkCoordinator.GetOriginReattributionWindowAsync)] = INTEGRITY,
    [nameof(IWorkCoordinator.ReattributeOriginsAsync)] = INTEGRITY,
    [nameof(IWorkCoordinator.RecordIntegrityDomainClaimsAsync)] = INTEGRITY,
    [nameof(IWorkCoordinator.AdvanceIntegrityCheckpointAsync)] = INTEGRITY,
    [nameof(IWorkCoordinator.AdvanceIntegritySealAsync)] = INTEGRITY,
    [nameof(IWorkCoordinator.CloseDigestEpochsAsync)] = INTEGRITY,
    [nameof(IWorkCoordinator.CompleteIntegrityDeliveryAsync)] = INTEGRITY,
    [nameof(IWorkCoordinator.ComputeStreamDigestsAsync)] = INTEGRITY,
    [nameof(IWorkCoordinator.ComputeStreamDigestsForChunkAsync)] = INTEGRITY,
    [nameof(IWorkCoordinator.ComputeStreamDigestsWindowedAsync)] = INTEGRITY,
    [nameof(IWorkCoordinator.ComputeTypeDigestsAsync)] = INTEGRITY,
    [nameof(IWorkCoordinator.ComputeTypeDigestsWindowedAsync)] = INTEGRITY,
    [nameof(IWorkCoordinator.CountReceivedFromOriginAsync)] = INTEGRITY,
    [nameof(IWorkCoordinator.EnqueueIntegrityDeliveryAsync)] = INTEGRITY,
    [nameof(IWorkCoordinator.EnsureIntegritySealGenerationAsync)] = INTEGRITY,
    [nameof(IWorkCoordinator.FailIntegrityDeliveryAsync)] = INTEGRITY,
    [nameof(IWorkCoordinator.GetConsumedTypeRegistrationsAsync)] = INTEGRITY,
    [nameof(IWorkCoordinator.GetIntegrityLedgerSummaryAsync)] = INTEGRITY,
    [nameof(IWorkCoordinator.GetIntegrityOriginGenerationAsync)] = INTEGRITY,
    [nameof(IWorkCoordinator.GetIntegritySealAsync)] = INTEGRITY,
    [nameof(IWorkCoordinator.GetIntegritySettledMaxAsync)] = INTEGRITY,
    [nameof(IWorkCoordinator.GetOwnAuditedEventTypesAsync)] = INTEGRITY,
    [nameof(IWorkCoordinator.GetPerspectiveCoverageGapsAsync)] = INTEGRITY,
    [nameof(IWorkCoordinator.GetStreamDigestsAsync)] = INTEGRITY,
    [nameof(IWorkCoordinator.GetTypeDigestsAsync)] = INTEGRITY,
    [nameof(IWorkCoordinator.IntegrityClaimRepairDrainAsync)] = INTEGRITY,
    [nameof(IWorkCoordinator.IntegrityForgetUnconsumedEventTypesAsync)] = INTEGRITY,
    [nameof(IWorkCoordinator.IntegrityMarkAnswerPendingAsync)] = INTEGRITY,
    [nameof(IWorkCoordinator.IntegrityMarkHealedAsync)] = INTEGRITY,
    [nameof(IWorkCoordinator.IntegrityMarkHealedBatchAsync)] = INTEGRITY,
    [nameof(IWorkCoordinator.IntegrityMarkHealedBatchWithAgesAsync)] = INTEGRITY,
    [nameof(IWorkCoordinator.IntegrityMarkUnhealableBatchAsync)] = INTEGRITY,
    [nameof(IWorkCoordinator.IntegrityProbeRepairBacklogAsync)] = INTEGRITY,
    [nameof(IWorkCoordinator.IntegrityRecordBundleAnswerAsync)] = INTEGRITY,
    [nameof(IWorkCoordinator.IntegrityStampRepairWindowsAsync)] = INTEGRITY,
    [nameof(IWorkCoordinator.IntegrityTryBeginRepairAsync)] = INTEGRITY,
    [nameof(IWorkCoordinator.IntegrityTryBeginRepairBatchAsync)] = INTEGRITY,
    [nameof(IWorkCoordinator.IntegrityTryBeginReportAsync)] = INTEGRITY,
    [nameof(IWorkCoordinator.IntegrityTryBeginReportBatchAsync)] = INTEGRITY,
    [nameof(IWorkCoordinator.LeaseIntegrityDeliveriesAsync)] = INTEGRITY,
    [nameof(IWorkCoordinator.MarkConsumedTypeBackfillRequestedAsync)] = INTEGRITY,
    [nameof(IWorkCoordinator.RegisterConsumedTypesAsync)] = INTEGRITY,
    [nameof(IWorkCoordinator.SelectRedeliveryEventsAsync)] = INTEGRITY,
    [nameof(IWorkCoordinator.TryClaimIntegrityAuditCycleAsync)] = INTEGRITY,
    [nameof(IWorkCoordinator.VerifyDigestEpochsAsync)] = INTEGRITY,
    [nameof(IWorkCoordinator.VerifyDigestTableAsync)] = INTEGRITY,
  };

  /// <summary>
  /// Stream integrity (digests, checkpoints, seals, repair, redelivery, the audit and subscription-growth backfill) is
  /// being reworked; the Dapper driver refuses every integrity switch at startup instead (DapperStreamIntegrityRefusal).
  /// </summary>
  private const string INTEGRITY = "stream integrity runs on EF Core only; a Dapper host that turns it on is refused at startup";

  /// <summary>
  /// Convenience overloads whose default body forwards to another overload of the same name (the one taking
  /// <c>debugMode</c>), which both drivers implement. Such a default runs the driver's own implementation, so it is not a
  /// gap; <see cref="EveryForwardingOverload_ReachesAnOverloadTheDriverImplementsAsync"/> holds each driver to that.
  /// </summary>
  private static readonly string[] _forwardingOverloads = [
    nameof(IWorkCoordinator.CompleteOutboxPublishedAsync),
    nameof(IWorkCoordinator.CompletePerspectiveAsync),
    nameof(IWorkCoordinator.CompletePerspectiveEventsAsync),
  ];

  /// <summary>
  /// Members that may never be allow-listed: the prefix truncate's (#2043). A hook is told data is about to be destroyed
  /// before they run, so serving the default on any of them tells it about a destruction that does not happen.
  /// </summary>
  private static readonly string[] _neverAllowListed = [
    nameof(IWorkCoordinator.TruncateStreamPrefixAsync),
    nameof(IWorkCoordinator.GetStreamTruncateTargetsAsync),
    nameof(IWorkCoordinator.GetArchivedEventsAsync),
    nameof(IWorkCoordinator.GetConsumingPerspectiveNamesAsync),
    nameof(IWorkCoordinator.FoldStreamApplyPathsAsync),
    nameof(IWorkCoordinator.GetEventVersionAsync),
  ];

  [Test]
  public async Task EveryDefaultBodiedMember_IsImplementedByDapper_OrAllowListedWithAReasonAsync() {
    var defaulted = _servedByTheDefault(typeof(DapperWorkCoordinator));

    await Assert.That(_defaultBodiedMembers().Count).IsGreaterThan(100)
      .Because("the interface carries well over a hundred default-bodied members; finding fewer means the reflection looks at nothing");
    var undecided = defaulted.Where(name => !DapperAllowList.ContainsKey(name)).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToList();
    await Assert.That(undecided).IsEmpty()
      .Because("DapperWorkCoordinator serves the interface default for these members with no decision recorded: implement "
             + $"each with the EF Core contract or allow-list it with its reason: [{string.Join(", ", undecided)}]");
  }

  [Test]
  public async Task TheAllowList_HoldsOnlyMembersDapperServesAtTheDefault_EachWithAReasonAsync() {
    var defaulted = _servedByTheDefault(typeof(DapperWorkCoordinator)).ToHashSet(StringComparer.Ordinal);

    var stale = DapperAllowList.Keys.Where(name => !defaulted.Contains(name)).Order(StringComparer.Ordinal).ToList();
    await Assert.That(stale).IsEmpty()
      .Because($"these allow-list entries name a member Dapper implements, or no default-bodied member at all: [{string.Join(", ", stale)}]");
    var unreasoned = DapperAllowList.Where(e => string.IsNullOrWhiteSpace(e.Value)).Select(e => e.Key).ToList();
    await Assert.That(unreasoned).IsEmpty()
      .Because("every allow-list entry carries the reason Dapper serves the default");
    var forbidden = _neverAllowListed.Where(DapperAllowList.ContainsKey).ToList();
    await Assert.That(forbidden).IsEmpty()
      .Because("a prefix-truncate member at the default tells the destruction hook about a destruction that does not happen (#2043)");
  }

  [Test]
  [MethodDataSource(nameof(Drivers))]
  public async Task EveryForwardingOverload_ReachesAnOverloadTheDriverImplementsAsync(Type driver) {
    var map = driver.GetInterfaceMap(typeof(IWorkCoordinator));
    var unreached = _forwardingOverloads.Where(name => !Enumerable.Range(0, map.InterfaceMethods.Length).Any(i =>
      map.InterfaceMethods[i].Name == name && map.TargetMethods[i].DeclaringType == driver)).ToList();

    await Assert.That(unreached).IsEmpty()
      .Because($"{driver.Name} implements no overload of [{string.Join(", ", unreached)}], so the forwarding default reaches the inert one");
  }

  /// <summary>The shipped Postgres coordinators.</summary>
  public static IEnumerable<Func<Type>> Drivers() {
    yield return () => typeof(DapperWorkCoordinator);
    yield return () => typeof(EFCoreWorkCoordinator<WorkCoordinationDbContext>);
  }

  [Test]
  public async Task EveryDefaultBodiedMember_IsImplementedByEFCoreAsync() {
    var defaulted = _servedByTheDefault(typeof(EFCoreWorkCoordinator<WorkCoordinationDbContext>)).Distinct(StringComparer.Ordinal).ToList();

    await Assert.That(defaulted).IsEmpty()
      .Because($"the EF Core coordinator is the reference implementation of every member: [{string.Join(", ", defaulted)}]");
  }

  /// <summary>The interface members that carry a default body.</summary>
  private static List<MethodInfo> _defaultBodiedMembers() =>
    [.. typeof(IWorkCoordinator).GetMethods(BindingFlags.Public | BindingFlags.Instance).Where(m => !m.IsAbstract)];

  /// <summary>
  /// The names of the default-bodied members the driver leaves at the interface default (overloads repeat), other than a
  /// forwarding overload whose target the driver implements.
  /// </summary>
  private static List<string> _servedByTheDefault(Type driver) {
    var map = driver.GetInterfaceMap(typeof(IWorkCoordinator));
    var names = new List<string>();
    for (var i = 0; i < map.InterfaceMethods.Length; i++) {
      var member = map.InterfaceMethods[i];
      if (member.IsAbstract || map.TargetMethods[i].DeclaringType != typeof(IWorkCoordinator)) {
        continue;
      }
      // A forwarding overload's default runs the overload the driver implements (checked separately).
      if (_forwardingOverloads.Contains(member.Name, StringComparer.Ordinal)
          && Enumerable.Range(0, map.InterfaceMethods.Length).Any(j => map.InterfaceMethods[j].Name == member.Name && map.TargetMethods[j].DeclaringType == driver)) {
        continue;
      }
      names.Add(member.Name);
    }
    return names;
  }
}
