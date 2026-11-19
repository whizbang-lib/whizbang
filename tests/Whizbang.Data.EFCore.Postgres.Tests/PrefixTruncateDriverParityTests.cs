// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Messaging;
using Whizbang.Data.Dapper.Postgres;

namespace Whizbang.Data.EFCore.Postgres.Tests;

/// <summary>
/// Every <see cref="IWorkCoordinator"/> member the prefix truncate and the stream compactor call is implemented by both
/// shipped Postgres coordinators, never left at the interface default (#2043).
/// </summary>
/// <remarks>
/// <para>
/// The interface defaults are "unsupported" no-ops for engines without the primitive. The Dapper coordinator was left on
/// them, so the prefix truncator fired the destruction hook, the Pre stages and any compaction handler, then reported
/// <c>unsupported</c> having removed nothing: a hook was told data was about to be destroyed when nothing was. This test
/// fails the moment either driver serves the default for one of these members, so a hook is never told about a
/// destruction that does not happen.
/// </para>
/// <para>
/// The list names what <c>StreamPrefixTruncator</c> and <c>StreamCompactor</c> call on the coordinator. A member either of
/// them starts calling belongs here. Design choice documented at
/// fundamentals/events/event-streams#prefix-truncate-on-every-data-layer.
/// </para>
/// </remarks>
/// <docs>fundamentals/events/event-streams#prefix-truncate-on-every-data-layer</docs>
/// <code-under-test>src/Whizbang.Data.Dapper.Postgres/DapperWorkCoordinator.PrefixTruncate.cs</code-under-test>
/// <code-under-test>src/Whizbang.Data.EFCore.Postgres/EFCoreWorkCoordinator.cs</code-under-test>
[Category("Shard2")]
public class PrefixTruncateDriverParityTests {
  /// <summary>The coordinator members the prefix truncate and the compactor depend on.</summary>
  private static readonly string[] _prefixTruncateMembers = [
    nameof(IWorkCoordinator.TruncateStreamPrefixAsync),
    nameof(IWorkCoordinator.GetStreamTruncateTargetsAsync),
    nameof(IWorkCoordinator.GetArchivedEventsAsync),
    nameof(IWorkCoordinator.GetConsumingPerspectiveNamesAsync),
    nameof(IWorkCoordinator.FoldStreamApplyPathsAsync),
    nameof(IWorkCoordinator.GetEventVersionAsync),
  ];

  /// <summary>The shipped Postgres coordinators.</summary>
  public static IEnumerable<Func<Type>> Drivers() {
    yield return () => typeof(DapperWorkCoordinator);
    yield return () => typeof(EFCoreWorkCoordinator<WorkCoordinationDbContext>);
  }

  [Test]
  [MethodDataSource(nameof(Drivers))]
  public async Task EveryMemberThePrefixTruncateCalls_IsImplementedByTheDriver_NotTheInterfaceDefaultAsync(Type driver) {
    var map = driver.GetInterfaceMap(typeof(IWorkCoordinator));
    var defaulted = new List<string>();
    var found = 0;
    for (var i = 0; i < map.InterfaceMethods.Length; i++) {
      if (!_prefixTruncateMembers.Contains(map.InterfaceMethods[i].Name, StringComparer.Ordinal)) {
        continue;
      }
      found++;
      if (map.TargetMethods[i].DeclaringType == typeof(IWorkCoordinator)) {
        defaulted.Add(map.InterfaceMethods[i].Name);
      }
    }

    await Assert.That(found).IsEqualTo(_prefixTruncateMembers.Length)
      .Because("every listed member is on the interface; finding fewer means the check is looking at nothing");
    await Assert.That(defaulted).IsEmpty()
      .Because($"{driver.Name} serves the interface default for [{string.Join(", ", defaulted)}], so a prefix truncate "
             + "on it tells the destruction hook about a destruction that does not happen");
  }
}
