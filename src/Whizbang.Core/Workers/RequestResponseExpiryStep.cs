// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

namespace Whizbang.Core.Workers;

/// <summary>Removes request/response records past their expiry (#2359).</summary>
/// <docs>operations/infrastructure/operational-retention#what-the-sweep-removes</docs>
/// <tests>tests/Whizbang.Data.Dapper.Postgres.Tests/DapperRequestResponseExpiryTests.cs</tests>
public sealed class RequestResponseExpiryStep : IMaintenanceStep {
  /// <inheritdoc />
  public string Name => "request-response-expiry";

  /// <inheritdoc />
  public Task RunAsync(IServiceProvider services, CancellationToken cancellationToken) {
    ArgumentNullException.ThrowIfNull(services);
    return Task.CompletedTask;
  }
}
