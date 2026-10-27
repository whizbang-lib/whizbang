// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using System.Data.Common;

namespace Whizbang.Data.Postgres;

/// <summary>Stub.</summary>
public readonly record struct PgSqueezePrerequisiteFacts(
  string Role,
  bool TableExists,
  bool SqueezeInstalled,
  bool CanExecuteSqueeze,
  string WalLevel,
  int ServerVersionNum,
  int SlotsInUse,
  int MaxReplicationSlots,
  bool HasIdentityIndex,
  bool CanReplicate,
  bool OwnsTable);

/// <summary>Stub.</summary>
public static class PgSqueezePrerequisites {
  /// <summary>Stub.</summary>
  public static string? Unmet(PgSqueezePrerequisiteFacts facts, string table) => null;

  /// <summary>Stub.</summary>
  public static Task<PgSqueezePrerequisiteFacts> ReadAsync(DbConnection connection, string qualifiedTable, CancellationToken cancellationToken) =>
    throw new NotSupportedException();
}
