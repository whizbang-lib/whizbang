// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Options;
using Whizbang.Core.Messaging;

namespace Whizbang.Core.Workers;

/// <summary>RED stub.</summary>
public sealed class VacuumFullTableRewriter : ITableRewriter {
  /// <summary>RED stub.</summary>
  public VacuumFullTableRewriter(IOptions<MaintenanceWorkerOptions> options) {
  }

  /// <inheritdoc />
  public string Strategy => throw new NotImplementedException();

  /// <inheritdoc />
  public bool CanRun => throw new NotImplementedException();

  /// <inheritdoc />
  public string CannotRunReason => throw new NotImplementedException();

  /// <inheritdoc />
  public Task<double?> RewriteAsync(IWorkCoordinator coordinator, string tableName, CancellationToken cancellationToken) =>
    throw new NotImplementedException();
}
