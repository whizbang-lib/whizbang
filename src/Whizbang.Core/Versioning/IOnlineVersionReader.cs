// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace Whizbang.Core.Versioning;

/// <summary>
/// Reads the versions of every instance that is online (#2022): live by the one liveness rule, not evicted, and serving
/// (its row records AcceptingCommands or Running). Supplied by the storage driver, from the same read Assess makes; a
/// newer instance reads it once it serves, to announce itself only when an older instance is online to hear it.
/// </summary>
/// <docs>operations/startup/rolling-upgrades#cluster-wide-stand-down</docs>
/// <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/UpgradeStandDownE2ETests.cs:ReadOnline_ReturnsEveryServingLiveInstance_WithBothVersionsAsync</tests>
/// <tests>tests/Whizbang.Data.Dapper.Postgres.Tests/DapperStartupAssessorTests.cs:TheOnlineVersions_AreReadForTheAnnouncementAsync</tests>
public interface IOnlineVersionReader {
  /// <summary>Reads the online instances, with both versions and their ids.</summary>
  /// <param name="cancellationToken">Cancellation token.</param>
  /// <returns>The online instances; empty on a schema older than the one liveness rule.</returns>
  Task<IReadOnlyList<VersionSighting>> ReadOnlineAsync(CancellationToken cancellationToken);
}
