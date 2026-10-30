// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using Microsoft.Extensions.Logging;
using Whizbang.Core.Archival;
using Whizbang.Core.Workers;

namespace Whizbang.Data.Postgres;

/// <summary>
/// The maintenance step that performs queued restores (#1738): each restore request an AutoRestore background write
/// queued is restored through the registered <see cref="IArchiveStore"/>.
/// </summary>
/// <docs>fundamentals/events/archive-store#restore-requests</docs>
/// <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/ArchiveRestoreRequestSqlTests.cs</tests>
public sealed partial class ArchiveRestoreRequestStep : IMaintenanceStep {
  /// <summary>The most requests one cycle restores.</summary>
  public const int BATCH_SIZE = 20;

  /// <summary>Creates the step.</summary>
  /// <param name="database">How the step reaches the service's database (the requests).</param>
  /// <param name="store">The archive store that restores a stream.</param>
  /// <param name="timeProvider">The clock the request times are written from.</param>
  /// <param name="logger">Logs each restore's outcome.</param>
  public ArchiveRestoreRequestStep(
      PostgresArchiveConnection database, IArchiveStore store, TimeProvider timeProvider, ILogger<ArchiveRestoreRequestStep> logger) {
    _ = database;
    _ = store;
    _ = timeProvider;
    _ = logger;
  }

  /// <inheritdoc />
  public string Name => "archive-restore-requests";

  /// <inheritdoc />
  public Task RunAsync(IServiceProvider services, CancellationToken cancellationToken) => throw new NotImplementedException();
}
