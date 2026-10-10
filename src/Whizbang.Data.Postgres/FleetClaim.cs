// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using Npgsql;
using Whizbang.Core.Dispatch;

namespace Whizbang.Data.Postgres;

/// <summary>
/// The one way a maintenance step claims a window so it runs on one instance of the fleet: through the registered
/// <see cref="IClaimedEmissionStore"/> when there is one, and directly in <c>wh_unique_emission_claims</c> otherwise.
/// Both write the same row under the same key, so a fleet that mixes the two still runs the step once.
/// </summary>
/// <docs>fundamentals/perspectives/managed-schema-objects#every-later-start</docs>
/// <tests>tests/Whizbang.Data.Dapper.Postgres.Tests/DapperManagedObjectsStartupTests.cs:TheMaintenanceStep_DropsWhatAStartHeldBack_OncePerWindow_WithNoClaimStoreAsync</tests>
/// <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/Migrations/ManagedSchemaReconcileStepTests.cs:ASecondRunInTheSameWindow_ReconcilesNothingAsync</tests>
public static class FleetClaim {
  /// <summary>Claims <paramref name="claimKey"/> for this instance.</summary>
  /// <param name="store">The registered claim store, or null to claim in the table.</param>
  /// <param name="connection">An open connection, used when there is no store.</param>
  /// <param name="quotedSchema">The schema the claim table lives in, quoted as an identifier.</param>
  /// <param name="claimKey">The window's key; one instance gets it.</param>
  /// <param name="cancellationToken">Cancellation token.</param>
  /// <returns><see langword="true"/> when this instance took the claim.</returns>
  public static async Task<bool> TryClaimAsync(
      IClaimedEmissionStore? store, NpgsqlConnection connection, string quotedSchema, string claimKey,
      CancellationToken cancellationToken) =>
    store is not null
      ? await store.TryClaimAsync(claimKey, Guid.CreateVersion7(), cancellationToken).ConfigureAwait(false)
      : await PhysicalColumnFill.TryClaimAsync(connection, quotedSchema, claimKey, cancellationToken).ConfigureAwait(false);
}
