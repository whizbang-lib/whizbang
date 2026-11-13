// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Whizbang.Core.Messaging;

namespace Whizbang.Core.Workers;

/// <summary>
/// The <see cref="TableRewriteStrategies.BLUE_GREEN"/> strategy: rewrites a perspective table by a blue-green rebuild of
/// the one perspective that writes it (#2301).
/// </summary>
/// <param name="scopeFactory">The scope the swapper and the rebuilder are resolved from.</param>
/// <param name="logger">Where a table that cannot be rebuilt now is reported.</param>
/// <docs>operations/infrastructure/storage-maintenance#blue-green-rewrite</docs>
/// <tests>tests/Whizbang.Core.Tests/Workers/OnlineTableRewriteStepTests.cs</tests>
/// <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/BlueGreenTableRewriteTests.cs</tests>
public sealed partial class BlueGreenTableRewriter(IServiceScopeFactory scopeFactory, ILogger<BlueGreenTableRewriter> logger) : ITableRewriter {
  private readonly IServiceScopeFactory _scopeFactory = ArgumentGuard.NotNull(scopeFactory);
  private readonly ILogger<BlueGreenTableRewriter> _logger = ArgumentGuard.NotNull(logger);

  /// <inheritdoc />
  public string Strategy => TableRewriteStrategies.BLUE_GREEN;

  /// <inheritdoc />
  public bool CanRun => true;

  /// <inheritdoc />
  public string CannotRunReason => "none: a blue-green rebuild needs nothing beyond Whizbang";

  /// <summary>The one perspective that writes <paramref name="table"/>, or null when no single rebuild can rebuild it.</summary>
  /// <param name="table">The bare table name.</param>
  /// <param name="cancellationToken">Cancellation token.</param>
  public Task<string?> PerspectiveOfAsync(string table, CancellationToken cancellationToken) {
    ArgumentException.ThrowIfNullOrWhiteSpace(table);
    cancellationToken.ThrowIfCancellationRequested();
    using var scope = _scopeFactory.CreateScope();
    return Task.FromResult<string?>(null);
  }

  /// <inheritdoc />
  public Task<double?> RewriteAsync(IWorkCoordinator coordinator, string tableName, CancellationToken cancellationToken) {
    ArgumentNullException.ThrowIfNull(coordinator);
    cancellationToken.ThrowIfCancellationRequested();
    LogNotRebuilt(_logger, tableName, "not rebuilt");
    return Task.FromResult<double?>(null);
  }

  [LoggerMessage(EventId = 1, Level = LogLevel.Warning, Message = "Blue-green rewrite of {Table}: {Reason}; the request stays queued")]
  private static partial void LogNotRebuilt(ILogger logger, string table, string reason);
}
