// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core;
using Whizbang.Core.ValueObjects;

namespace Whizbang.Data.EFCore.Postgres.Tests;

/// <summary>
/// #2269: a released type's former names are stored names too. A bare former name (a rename within the assembly) is
/// stored under the type's current assembly, so the release list resolves it with <see cref="TypeNameFormatter.FormatFormerName"/>;
/// added as written it had no assembly and matched no stored event.
/// </summary>
public partial class EphemeralBlockerReportSqlTests {
  [Test]
  public async Task Release_AReleasedTypesBareFormerName_ReachesTheEventsStoredUnderItAsync() {
    await using var conn = await _openAsync();
    var probe = TypeNameFormatter.Format(typeof(BlockerReleaseProbe));
    const string FORMER = "Whizbang.Data.EFCore.Postgres.Tests.BlockerReleaseProbeFormer";
    var formerStored = TypeNameFormatter.FormatFormerName(typeof(BlockerReleaseProbe), FORMER);
    var mixed = (Guid)TrackedGuid.New();
    await _appendAsync(conn, mixed, TYPE);
    var underFormer = await _appendAsync(conn, mixed, formerStored);
    var catalog = new Catalog(_ephemeral(typeof(SessionPinged)),
      new MessageTypeCatalogEntry(typeof(BlockerReleaseProbe), TypeNameFormatter.GetFullName(probe), "event", null) {
        FormerNames = [FORMER],
      });

    var result = await _step(new ListLogger()).ReclassifyAsync(
      _services(release: new() { [TYPE_KEY] = [TypeNameFormatter.GetFullName(probe)] }, catalog: catalog), CancellationToken.None);

    await Assert.That(result.EventsReleased).IsEqualTo(1L);
    await Assert.That(await _flagsAsync(conn, underFormer)).IsEqualTo((long)EPHEMERAL)
      .Because("an event stored under the released type's former name is released with it");
  }
}
