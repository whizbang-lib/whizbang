// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using Microsoft.Extensions.Logging;
using Whizbang.Core.Workers;

namespace Whizbang.Data.Dapper.Postgres;

/// <summary>Stub (RED).</summary>
/// <param name="connectionString">Stub.</param>
/// <param name="logger">Stub.</param>
/// <param name="timeProvider">Stub.</param>
public sealed class DapperPartitionMaintenanceStep(
    string connectionString,
    ILogger<DapperPartitionMaintenanceStep> logger,
    TimeProvider timeProvider) : IMaintenanceStep {
  private readonly object[] _unused = [connectionString, logger, timeProvider];

  /// <inheritdoc />
  public string Name => _unused.Length.ToString(System.Globalization.CultureInfo.InvariantCulture);

  /// <inheritdoc />
  public Task RunAsync(IServiceProvider services, CancellationToken cancellationToken) => throw new NotImplementedException();
}
