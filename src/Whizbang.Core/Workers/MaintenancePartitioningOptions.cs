// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

namespace Whizbang.Core.Workers;

/// <summary>Stub (RED).</summary>
public sealed class MaintenancePartitioningOptions {
  /// <summary>Stub.</summary>
  public const int DEFAULT_CREATE_AHEAD = 3;

  /// <summary>Stub.</summary>
  public bool Enabled { get; set; }

  /// <summary>Stub.</summary>
  public int CreateAhead { get; set; } = DEFAULT_CREATE_AHEAD;
}
