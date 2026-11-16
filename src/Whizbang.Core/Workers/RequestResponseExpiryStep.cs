// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using Microsoft.Extensions.DependencyInjection;
using Whizbang.Core.Messaging;

namespace Whizbang.Core.Workers;

/// <summary>
/// Removes request/response records past their own expiry, every maintenance cycle (#2359).
/// </summary>
/// <remarks>
/// Each record carries its expiry, and a waiter has given up on it by then, so removing it loses nothing. The store's
/// expiry (<see cref="IRequestResponseStore.CleanupExpiredAsync"/>) existed, but nothing called it, so the table kept
/// every record it was given. A host without a store has nothing to expire.
/// </remarks>
/// <docs>operations/infrastructure/operational-retention#what-the-sweep-removes</docs>
/// <tests>tests/Whizbang.Data.Dapper.Postgres.Tests/DapperRequestResponseExpiryTests.cs</tests>
public sealed class RequestResponseExpiryStep : IMaintenanceStep {
  /// <inheritdoc />
  public string Name => "request-response-expiry";

  /// <inheritdoc />
  public Task RunAsync(IServiceProvider services, CancellationToken cancellationToken) {
    ArgumentNullException.ThrowIfNull(services);
    var store = services.GetService<IRequestResponseStore>();
    return store is null ? Task.CompletedTask : store.CleanupExpiredAsync(cancellationToken);
  }
}
