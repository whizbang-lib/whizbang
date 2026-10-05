// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using Microsoft.EntityFrameworkCore.ChangeTracking;
using Whizbang.Core.Perspectives;

namespace Whizbang.Data.EFCore.Postgres;

/// <summary>
/// Reads a Split model's promoted columns from the shadow properties of the tracked row they were loaded
/// into. Each shadow property is declared with its model property's own type, so its value is that type
/// already; a vector is declared as <c>Pgvector.Vector</c> and read back as its components.
/// </summary>
/// <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/Perspectives/SplitPhysicalFieldReloadTests.cs</tests>
internal sealed class EntityEntryPhysicalColumnReader(EntityEntry entry) : IPhysicalColumnReader {
  /// <inheritdoc/>
  public T Read<T>(string column) => entry.Property(column).CurrentValue is T value ? value : default!;

  /// <inheritdoc/>
  public float[]? GetVector(string column) => (entry.Property(column).CurrentValue as Pgvector.Vector)?.ToArray();
}
