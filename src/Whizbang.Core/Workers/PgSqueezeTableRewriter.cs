// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using Whizbang.Core.Messaging;

namespace Whizbang.Core.Workers;

/// <summary>Stub.</summary>
public sealed class PgSqueezeTableRewriter : ITableRewriter {
  /// <inheritdoc />
  public string Strategy => "";

  /// <inheritdoc />
  public bool CanRun => false;

  /// <inheritdoc />
  public string CannotRunReason => "";

  /// <inheritdoc />
  public Task<double?> RewriteAsync(IWorkCoordinator coordinator, string tableName, CancellationToken cancellationToken) =>
    Task.FromResult<double?>(null);
}
